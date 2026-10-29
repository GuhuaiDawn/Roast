using System.Collections.Concurrent;
using DesktopNarrator.Events;

namespace DesktopNarrator.Memory;

/// <summary>持久化运行状态；“已排队”不等于“已落盘”，异常和拒绝数量必须可见。</summary>
public sealed record PersistenceStatus(bool Healthy, string Message);

/// <summary>
/// 有界后台写入队列；生产者只提交不可变记录，不执行数据库 I/O。
/// 每批最多 64 项，事务失败保留本批重试；退出限时排空，不能无限阻塞程序关闭。
/// </summary>
public sealed class ActivityPersistence : IActivityPersistence, IPatternPersistence, IDisposable
{
    private sealed record Work(ActivityWrite? Write, TaskCompletionSource<bool>? Barrier = null);
    private readonly SqliteActivityStore _store;
    private readonly BlockingCollection<Work> _queue;
    private readonly CancellationTokenSource _abort = new();
    private readonly Thread _thread;
    private readonly object _lifecycle = new();
    private bool _started;
    private bool _stopping;
    private bool _disposed;
    private int _pending;
    private int _rejected;
    private PersistenceStatus _status = new(false, "数据库等待启动");

    public event EventHandler<PersistenceStatus>? StatusChanged;
    public PersistenceStatus Status => Volatile.Read(ref _status);
    public int PendingCount => Volatile.Read(ref _pending);
    public int RejectedCount => Volatile.Read(ref _rejected);

    /// <summary>默认队列容量 4096，另有最多 64 项正在处理；数据库长期故障也不会无限增长内存。</summary>
    public ActivityPersistence(SqliteActivityStore store, int capacity = 4096)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _store = store;
        _queue = new BlockingCollection<Work>(capacity);
        _thread = new Thread(Run) { IsBackground = true, Name = "DesktopNarrator.DatabaseWriter" };
    }

    /// <summary>安装状态订阅后启动；实例仅支持一次生命周期，停止后不能重启。</summary>
    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping) throw new InvalidOperationException("Persistence has stopped.");
            if (_started) return;
            _thread.Start();
            _started = true;
        }
    }

    public bool TryRecord(IMonitorEvent monitorEvent, AppSession? completedSession = null)
    {
        if (completedSession is not null) SqliteActivityStore.ValidateSession(completedSession);
        return Enqueue(new(StoredMonitorEvent.From(monitorEvent), completedSession));
    }

    public bool TrySaveSession(AppSession session)
    {
        SqliteActivityStore.ValidateSession(session);
        return Enqueue(new(null, session));
    }

    /// <summary>与会话共用有界队列，候选入队失败也计入拒绝数，不在监测线程做 I/O。</summary>
    public bool TrySavePattern(DetectedPattern pattern, PatternEvaluation? evaluation = null)
    {
        SqliteActivityStore.ValidatePattern(pattern);
        if (evaluation is not null) SqliteActivityStore.ValidateEvaluation(pattern, evaluation);
        return Enqueue(new(null, null, pattern, evaluation));
    }

    private bool Enqueue(ActivityWrite write)
    {
        Interlocked.Increment(ref _pending);
        if (TryAdd(new(write))) return true;
        Interlocked.Decrement(ref _pending);
        var rejected = Interlocked.Increment(ref _rejected);
        // 只在第一项拒绝时报诊断，避免数据库故障时日志本身无限刷屏；累计数仍可查询。
        if (rejected == 1) Publish(false, "数据库队列已满或已停止：有数据未被接受，不能视为已保存。");
        return false;
    }

    /// <summary>队列屏障；完成表示此前接受的写入已提交，不等待之后的新事件。</summary>
    public Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryAdd(new(null, completion))) completion.TrySetResult(false);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private bool TryAdd(Work work)
    {
        if (Volatile.Read(ref _stopping)) return false;
        try { return _queue.TryAdd(work); }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>先停止接受新任务，再限时排空；返回 false 时 PendingCount/RejectedCount 说明未保存风险。</summary>
    public bool Shutdown(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_lifecycle)
        {
            if (!_stopping) { _stopping = true; _queue.CompleteAdding(); }
            if (!_started) return PendingCount == 0 && RejectedCount == 0;
        }
        // 状态消费者不得在写入线程上同步等待自身关闭。
        if (Thread.CurrentThread == _thread) { _abort.Cancel(); return false; }
        if (!_thread.Join(timeout))
        {
            _abort.Cancel();
            Publish(false, $"退出等待超时：仍有 {PendingCount} 项待写，强制退出可能丢失这些数据。");
            return false;
        }
        return PendingCount == 0 && RejectedCount == 0;
    }

    public void Dispose()
    {
        lock (_lifecycle) { if (_disposed) return; _disposed = true; }
        Shutdown(TimeSpan.FromSeconds(5));
        // 超时后原生调用可能仍在返回途中；不能提前释放它正在使用的队列/信号。
        if (!_thread.IsAlive) { _queue.Dispose(); _abort.Dispose(); }
    }

    private void Run()
    {
        Work? carried = null;
        var initialized = false;
        var failing = false;
        bool Retry(Action action)
        {
            while (!_abort.IsCancellationRequested)
            {
                try
                {
                    if (!initialized) { _store.Initialize(); initialized = true; }
                    action();
                    failing = false;
                    Publish(true, $"数据库可用 · 待写 {PendingCount} 项 · 累计拒绝 {RejectedCount} 项");
                    return true;
                }
                catch (Exception exception)
                {
                    initialized = false;
                    if (!failing) Publish(false, $"数据库暂不可用，保留队列并重试：{exception.Message}");
                    failing = true;
                    if (_abort.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1))) break;
                }
            }
            return false;
        }

        try
        {
            if (!Retry(() => { })) return;
            while (!_abort.IsCancellationRequested)
            {
                Work work;
                if (carried is not null) { work = carried; carried = null; }
                else
                {
                    try { work = _queue.Take(_abort.Token); }
                    catch (InvalidOperationException) { break; } // CompleteAdding 且队列已排空。
                }
                if (work.Barrier is { } barrier) { barrier.TrySetResult(true); continue; }
                var batch = new List<ActivityWrite> { work.Write! };
                while (batch.Count < 64 && _queue.TryTake(out var next))
                {
                    if (next.Barrier is not null) { carried = next; break; }
                    batch.Add(next.Write!);
                }
                if (!Retry(() =>
                {
                    _store.WriteBatch(batch);
                    Interlocked.Add(ref _pending, -batch.Count);
                })) break;
            }
        }
        catch (OperationCanceledException) { /* 退出超时的受控停止，不把取消当成已保存。 */ }
        finally
        {
            carried?.Barrier?.TrySetResult(false);
            while (_queue.TryTake(out var pending)) pending.Barrier?.TrySetResult(false);
            Publish(PendingCount == 0 && RejectedCount == 0,
                $"数据库写入已停止 · 未确认保存 {PendingCount} 项 · 累计拒绝 {RejectedCount} 项");
        }
    }

    private void Publish(bool healthy, string message)
    {
        var status = new PersistenceStatus(healthy, message);
        Volatile.Write(ref _status, status);
        if (StatusChanged is not { } handlers) return;
        foreach (EventHandler<PersistenceStatus> handler in handlers.GetInvocationList())
        {
            try { handler(this, status); }
            catch { /* UI 或诊断消费者失败不能终止写入线程。 */ }
        }
    }
}
