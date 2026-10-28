using DesktopNarrator.Events;
using DesktopNarrator.Monitor;

/// <summary>空闲状态机与线程生命周期回归；大部分场景使用模拟时间，仅 NativeInput 读取系统。</summary>
internal static class IdleTests
{
    private static IdleInputSample Sample(long now, uint input = 0) =>
        new(DateTimeOffset.UnixEpoch.AddMilliseconds(now), now, input);
    private static IdleTransitionTracker Tracker() => new(TimeSpan.FromSeconds(10));
    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Idle assertion failed.");
    }

    /// <summary>检查阈值前一毫秒、恰好达到阈值，以及持续空闲不重复发事件。</summary>
    public static void ThresholdAndDeduplication()
    {
        var tracker = Tracker();
        Check(tracker.Observe(Sample(9_999)).Count == 0);
        var idle = (UserIdleEvent)tracker.Observe(Sample(10_000)).Single();
        Check(idle.IdleStartedAt == DateTimeOffset.UnixEpoch);
        Check(idle.DetectedAt == Sample(10_000).Timestamp);
        Check(tracker.IsIdle && tracker.Observe(Sample(20_000)).Count == 0);
        Check(tracker.CurrentIdleTime == TimeSpan.FromSeconds(20));
    }

    /// <summary>返回时长不能包含等待下一次采样的延迟，并且能再次进入新一轮空闲。</summary>
    public static void ReturnDuration()
    {
        var tracker = Tracker();
        tracker.Observe(Sample(10_000));
        var returned = (UserReturnedEvent)tracker.Observe(Sample(15_000, 14_500)).Single();
        Check(returned.IdleDuration == TimeSpan.FromMilliseconds(14_500));
        Check(returned.ReturnedAt == Sample(14_500).Timestamp);
        Check(!tracker.IsIdle && tracker.Observe(Sample(16_000, 15_500)).Count == 0);
        Check(tracker.Observe(Sample(25_500, 15_500)).Single() is UserIdleEvent);
    }

    /// <summary>模拟系统日期时间向后调整两小时，空闲时长仍应来自运行计时。</summary>
    public static void ClockChange()
    {
        var tracker = Tracker();
        tracker.Observe(Sample(10_000));
        var sample = Sample(20_000, 19_000) with { Timestamp = DateTimeOffset.UnixEpoch.AddHours(-2) };
        var returned = (UserReturnedEvent)tracker.Observe(sample).Single();
        Check(returned.IdleDuration == TimeSpan.FromSeconds(19));
        Check(returned.ReturnedAt == sample.Timestamp.AddSeconds(-1));
    }

    /// <summary>跨越 32 位回绕边界，并在输入未变时继续跨越完整周期，验证 64 位基准保留。</summary>
    public static void TickRollover()
    {
        var tracker = Tracker();
        const long wrap = 1L << 32;
        var input = uint.MaxValue - 4_999;
        Check(tracker.Observe(Sample(wrap + 4_999, input)).Count == 0);
        Check(tracker.Observe(Sample(wrap + 5_000, input)).Single() is UserIdleEvent);
        Check(tracker.Observe(Sample(2 * wrap + 5_000, input)).Count == 0);
        Check(tracker.CurrentIdleTime == TimeSpan.FromMilliseconds(wrap + 10_000));
    }

    /// <summary>变化但倒退的输入计数表示新的活动；略微超前的计数也不能变成数十天空闲。</summary>
    public static void NonIncrementalInput()
    {
        var tracker = Tracker();
        tracker.Observe(Sample(20_000, 10_000));
        var returned = (UserReturnedEvent)tracker.Observe(Sample(21_000, 9_000)).Single();
        Check(returned.IdleDuration == TimeSpan.FromSeconds(11));
        Check(!tracker.IsIdle);
        var future = Tracker();
        Check(future.Observe(Sample(1_000, 1_001)).Count == 0);
    }

    /// <summary>一次较长采样间隙可能包含返回和再次空闲，事件顺序必须正确。</summary>
    public static void PollingGap()
    {
        var tracker = Tracker();
        tracker.Observe(Sample(10_000));
        var events = tracker.Observe(Sample(40_000, 20_000));
        Check(events.Count == 2 && events[0] is UserReturnedEvent && events[1] is UserIdleEvent);
        Check(((UserReturnedEvent)events[0]).IdleDuration == TimeSpan.FromSeconds(20));
    }

    /// <summary>拒绝非正阈值、非正间隔和倒退的运行计时，避免静默生成错误状态。</summary>
    public static void InvalidArguments()
    {
        Throws<ArgumentOutOfRangeException>(() => _ = new IdleMonitor(TimeSpan.Zero));
        Throws<ArgumentOutOfRangeException>(() => _ = new IdleMonitor(pollInterval: TimeSpan.Zero));
        Throws<ArgumentOutOfRangeException>(() => _ = new IdleTransitionTracker(TimeSpan.Zero));
        var tracker = Tracker();
        tracker.Observe(Sample(100));
        Throws<ArgumentException>(() => tracker.Observe(Sample(99)));
    }

    /// <summary>模拟读取失败和订阅者抛异常，验证错误去重、恢复、停止、重启及释放约束。</summary>
    public static void RecoveryAndLifecycle()
    {
        var calls = 0;
        var errors = 0;
        var events = 0;
        using var received = new ManualResetEventSlim();
        var source = new FakeSource(() =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new InvalidOperationException("Simulated read failure");
            return Sample(20_000);
        });
        using var monitor = new IdleMonitor(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10), source);
        monitor.Error += (_, _) => Interlocked.Increment(ref errors);
        monitor.Error += (_, _) => throw new InvalidOperationException("Bad diagnostics consumer");
        monitor.UserIdle += (_, _) => throw new InvalidOperationException("Bad event consumer");
        monitor.UserIdle += (_, _) => { Interlocked.Increment(ref events); received.Set(); };
        monitor.Start();
        monitor.Start();
        Check(received.Wait(TimeSpan.FromSeconds(3)));
        monitor.Stop();
        // 两条诊断分别来自连续读取失败（仅报告一次）和故意抛异常的事件订阅者。
        Check(!monitor.IsRunning && events == 1 && errors == 2);
        var callsAtStop = calls;
        Thread.Sleep(40);
        Check(calls == callsAtStop);
        received.Reset();
        monitor.Start();
        Check(received.Wait(TimeSpan.FromSeconds(3)));
        monitor.Stop();
        Check(events == 2);
        monitor.Dispose();
        monitor.Dispose();
        Throws<ObjectDisposedException>(monitor.Start);
    }

    /// <summary>空闲期间的一次读取失败不能丢失原始起点，否则返回时长会被错误缩短。</summary>
    public static void ReturnAfterReadFailure()
    {
        var calls = 0;
        var errors = 0;
        UserReturnedEvent? result = null;
        using var received = new ManualResetEventSlim();
        var source = new FakeSource(() => Interlocked.Increment(ref calls) switch
        {
            1 => Sample(10_000),
            2 => throw new InvalidOperationException("Temporary read failure during idle"),
            _ => Sample(20_000, 19_500)
        });
        using var monitor = new IdleMonitor(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10), source);
        monitor.Error += (_, _) => Interlocked.Increment(ref errors);
        monitor.UserReturned += (_, value) => { result = value; received.Set(); };
        monitor.Start();
        Check(received.Wait(TimeSpan.FromSeconds(3)));
        monitor.Stop();
        Check(errors == 1 && result?.IdleDuration == TimeSpan.FromMilliseconds(19_500));
    }

    /// <summary>监测线程在自身事件回调中停止时，不得 Join 自己造成死锁。</summary>
    public static void StopInCallback()
    {
        using var received = new ManualResetEventSlim();
        using var monitor = new IdleMonitor(TimeSpan.FromSeconds(10), source: new FakeSource(() => Sample(20_000)));
        monitor.UserIdle += (_, _) => { monitor.Stop(); received.Set(); };
        monitor.Start();
        Check(received.Wait(TimeSpan.FromSeconds(3)));
        monitor.Stop();
        Check(!monitor.IsRunning);
    }

    /// <summary>当前 Windows 会话的原生 API 冒烟检查；仅读时间，不生成或记录键鼠输入。</summary>
    public static void NativeInput()
    {
        var sample = new WindowsIdleInputSource().Read();
        Check(sample.UptimeMilliseconds >= 0);
        Check((DateTimeOffset.Now - sample.Timestamp).Duration() < TimeSpan.FromSeconds(5));
        // 以极小阈值验证真实采样源与后台循环能协作，不要求用户真的等待五分钟。
        using var received = new ManualResetEventSlim();
        using var monitor = new IdleMonitor(TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(20));
        monitor.UserIdle += (_, _) => received.Set();
        monitor.Start();
        Check(received.Wait(TimeSpan.FromSeconds(3)));
        monitor.Stop();
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    // 将样本和异常序列作为函数注入，确保边界测试可重复，不依赖物理输入时机。
    private sealed class FakeSource(Func<IdleInputSample> read) : IIdleInputSource
    {
        public IdleInputSample Read() => read();
    }
}
