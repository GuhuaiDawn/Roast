using DesktopNarrator.Events;

namespace DesktopNarrator.Monitor;

/// <summary>
/// 在单个后台线程上低频采样，避免重叠执行；采样与事件订阅均不依赖 WPF。
/// 默认每秒检查一次、5 分钟无输入进入空闲；只将状态转换发布给上层。
/// </summary>
public sealed class IdleMonitor : IIdleMonitor
{
    private readonly object _sync = new();
    private readonly IIdleInputSource _source;
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _pollInterval;
    private Thread? _thread;
    private ManualResetEventSlim? _stop;
    private bool _disposed;

    /// <summary>创建监测器；测试可以注入采样源并缩短间隔，不必生成真实键鼠输入。</summary>
    public IdleMonitor(TimeSpan? threshold = null, TimeSpan? pollInterval = null, IIdleInputSource? source = null)
    {
        _threshold = threshold ?? TimeSpan.FromMinutes(5);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (_threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        if (_pollInterval <= TimeSpan.Zero || _pollInterval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _source = source ?? new WindowsIdleInputSource();
    }

    public event EventHandler<UserIdleEvent>? UserIdle;
    public event EventHandler<UserReturnedEvent>? UserReturned;
    public event EventHandler<MonitorErrorEventArgs>? Error;

    public bool IsRunning { get { lock (_sync) return _thread is not null && !_stop!.IsSet; } }

    /// <summary>启动后台线程；重复调用不重复启动，已释放的实例不能再次启动。</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return;
            var stop = new ManualResetEventSlim(false);
            _stop = stop;
            _thread = new Thread(() => Run(stop)) { IsBackground = true, Name = "DesktopNarrator.IdleMonitor" };
            try { _thread.Start(); }
            catch { _thread = null; _stop = null; stop.Dispose(); throw; }
        }
    }

    /// <summary>唤醒等待中的采样线程并等待退出；采样源和订阅回调必须能及时返回。</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_sync)
        {
            thread = _thread;
            _stop?.Set();
        }
        // 在锁外等待，避免阻塞线程 finally 中的清理；回调请求停止时不能等待自身。
        if (thread is not null && thread != Thread.CurrentThread) thread.Join();
    }

    /// <summary>阻止后续启动并停止线程；停止信号由线程退出时释放。</summary>
    public void Dispose()
    {
        lock (_sync) { _disposed = true; }
        Stop();
    }

    private void Run(ManualResetEventSlim stop)
    {
        // 每次启动重建状态，不能把停止监测期间的情况冒充已观察到的历史。
        var tracker = new IdleTransitionTracker(_threshold);
        var readFailed = false;
        try
        {
            while (!stop.IsSet)
            {
                try
                {
                    var transitions = tracker.Observe(_source.Read());
                    readFailed = false;
                    foreach (var transition in transitions)
                    {
                        if (stop.IsSet) break;
                        switch (transition)
                        {
                            case UserIdleEvent idle: Publish(UserIdle, idle, stop); break;
                            case UserReturnedEvent returned: Publish(UserReturned, returned, stop); break;
                        }
                    }
                }
                catch (Exception exception)
                {
                    // 连续读取失败只报告一次；保留之前的状态，在下个周期继续尝试。
                    if (!readFailed) ReportError("Read idle input", exception);
                    readFailed = true;
                }
                // 用可唤醒的等待代替 Sleep，退出时无需等完整采样周期结束。
                if (stop.Wait(_pollInterval)) break;
            }
        }
        finally
        {
            lock (_sync)
            {
                _thread = null;
                _stop = null;
                stop.Dispose();
            }
        }
    }

    /// <summary>逐个隔离订阅方异常；一个消费者失败不阻止其他消费者收到本次事件。</summary>
    private void Publish<T>(EventHandler<T>? handlers, T value, ManualResetEventSlim stop)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            if (stop.IsSet) break;
            try { handler(this, value); }
            catch (Exception exception) { ReportError("Idle event subscriber", exception); }
        }
    }

    private void ReportError(string operation, Exception exception)
    {
        if (Error is not { } handlers) return;
        foreach (EventHandler<MonitorErrorEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, new MonitorErrorEventArgs(operation, exception)); }
            catch { /* 诊断消费者自身的异常不能终止监测，也不能递归报告到同一通道。 */ }
        }
    }
}
