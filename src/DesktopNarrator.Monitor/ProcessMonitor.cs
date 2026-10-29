using DesktopNarrator.Events;

namespace DesktopNarrator.Monitor;

/// <summary>默认每 5 秒枚举一次当前登录会话的进程名，仅发布进程组存在状态变化；不依赖 WPF。</summary>
public sealed class ProcessMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly IProcessSnapshotSource _source;
    private readonly TimeSpan _interval;
    private Thread? _thread;
    private ManualResetEventSlim? _stop;
    private bool _disposed;

    /// <summary>从采样线程发出进程组变化或观察重置；订阅方不能同步等待 UI。</summary>
    public event EventHandler<IMonitorEvent>? Changed;
    /// <summary>连续采样失败只报告一次；一个诊断订阅者异常不影响其他订阅者。</summary>
    public event EventHandler<MonitorErrorEventArgs>? Error;
    /// <summary>表示线程仍运行且尚未请求停止，不代表每次快照都成功。</summary>
    public bool IsRunning { get { lock (_sync) return _thread is not null && !_stop!.IsSet; } }

    /// <summary>测试可注入名称快照；生产默认五秒一次，允许缺口至少为十五秒。</summary>
    public ProcessMonitor(IProcessSnapshotSource? source = null, TimeSpan? interval = null)
    {
        _source = source ?? new WindowsProcessSnapshotSource();
        _interval = interval ?? TimeSpan.FromSeconds(5);
        if (_interval <= TimeSpan.Zero || _interval.TotalMilliseconds > int.MaxValue / 3)
            throw new ArgumentOutOfRangeException(nameof(interval));
    }

    /// <summary>每次启动重新建立基线；重复启动无副作用。</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return;
            var stop = _stop = new ManualResetEventSlim();
            _thread = new Thread(() => Run(stop)) { IsBackground = true, Name = "DesktopNarrator.ProcessMonitor" };
            try { _thread.Start(); }
            catch { _thread = null; _stop = null; stop.Dispose(); throw; }
        }
    }

    /// <summary>采样源及回调必须及时返回；回调内停止不会等待自身，外部停止等待资源释放。</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_sync) { thread = _thread; _stop?.Set(); }
        if (thread is not null && thread != Thread.CurrentThread) thread.Join();
    }

    /// <summary>禁止再次启动并停止线程；停止信号由采样线程的 finally 释放。</summary>
    public void Dispose() { lock (_sync) _disposed = true; Stop(); }

    private void Run(ManualResetEventSlim stop)
    {
        var clock = TimeProvider.System;
        var origin = clock.GetTimestamp();
        // 短测试周期或调用方缩短采样周期时，不把普通线程调度抖动当成监测中断。
        var tracker = new ProcessTransitionTracker(TimeSpan.FromSeconds(Math.Max(15, _interval.TotalSeconds * 3)));
        var failed = false;
        try
        {
            while (!stop.IsSet)
            {
                try
                {
                    var names = _source.Read();
                    var changes = tracker.Observe(names, DateTimeOffset.Now, clock.GetElapsedTime(origin));
                    failed = false;
                    foreach (var change in changes) Publish(change, stop);
                }
                catch (Exception exception)
                {
                    tracker.Reset();
                    if (!failed)
                    {
                        Publish(new ProcessObservationResetEvent(DateTimeOffset.Now), stop);
                        Report("Read process snapshot; observation baseline reset", exception);
                    }
                    failed = true;
                }
                if (stop.Wait(_interval)) break;
            }
        }
        finally
        {
            lock (_sync) { _thread = null; _stop = null; stop.Dispose(); }
        }
    }

    private void Publish(IMonitorEvent value, ManualResetEventSlim stop)
    {
        if (Changed is not { } handlers) return;
        foreach (EventHandler<IMonitorEvent> handler in handlers.GetInvocationList())
        {
            if (stop.IsSet) break;
            try { handler(this, value); }
            catch (Exception exception) { Report("Process event subscriber", exception); }
        }
    }

    private void Report(string operation, Exception exception)
    {
        if (Error is not { } handlers) return;
        foreach (EventHandler<MonitorErrorEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, new(operation, exception)); } catch { /* 诊断失败不能终止采样或递归报告。 */ }
    }
}
