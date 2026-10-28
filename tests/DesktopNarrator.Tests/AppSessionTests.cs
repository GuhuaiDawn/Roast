using DesktopNarrator.Events;

/// <summary>使用模拟日期与独立计时验证会话聚合；无需操作系统窗口或实际等待。</summary>
internal static class AppSessionTests
{
    /// <summary>首次事件只开始当前应用，不从 PreviousApplication 反推监测前的会话。</summary>
    public static void InitialObservation()
    {
        var builder = new AppSessionBuilder();
        Check(builder.CurrentSession is null);
        var transition = Event("Code", 120) with { PreviousApplication = Event("Chrome", 0).CurrentApplication };
        Check(builder.Process(transition, TimeSpan.FromSeconds(120)) is null);
        var active = builder.CurrentSession!;
        Check(active.Id != Guid.Empty && active.ProcessName == "Code");
        Check(active.StartTime == transition.Timestamp && active.EndTime is null);
        Check(active.Duration is null && active.DurationSeconds is null);
    }

    /// <summary>模拟 Code 8 分钟、Chrome 40 分钟、再回 Code，核对边界和会话标识。</summary>
    public static void Switching()
    {
        var builder = new AppSessionBuilder();
        builder.Process(Event("Code", 0), TimeSpan.Zero);
        var firstId = builder.CurrentSession!.Id;
        var code = builder.Process(Event("Chrome", 480), TimeSpan.FromMinutes(8))!;
        Check(code.Id == firstId && code.DurationSeconds == 480);
        Check(code.EndTime == builder.CurrentSession!.StartTime);
        var chromeId = builder.CurrentSession.Id;
        var chrome = builder.Process(Event("Code", 2880), TimeSpan.FromMinutes(48))!;
        Check(chrome.Id == chromeId && chrome.ProcessName == "Chrome" && chrome.DurationSeconds == 2400);
        Check(builder.CurrentSession!.Id != firstId && builder.CurrentSession.Id != chromeId);
    }

    /// <summary>同名进程的 PID、大小写和描述变化不应打断同一段会话。</summary>
    public static void DuplicateApplication()
    {
        var builder = new AppSessionBuilder();
        builder.Process(Event("Chrome", 0), TimeSpan.Zero);
        var first = builder.CurrentSession!;
        var duplicate = Event("CHROME", 10, 99, "Updated Chrome");
        Check(builder.Process(duplicate, TimeSpan.FromSeconds(10)) is null);
        Check(builder.CurrentSession!.Id == first.Id && builder.CurrentSession.StartTime == first.StartTime);
        Check(builder.CurrentSession.FriendlyName == "Updated Chrome");
        var ended = builder.Process(Event("Code", 20), TimeSpan.FromSeconds(20))!;
        Check(ended.DurationSeconds == 20);
    }

    /// <summary>日期时间前跳或后跳只影响展示时间，Duration 始终采用单独的计时差值。</summary>
    public static void WallClockChanges()
    {
        var builder = new AppSessionBuilder();
        builder.Process(Event("Code", 3600), TimeSpan.Zero);
        var code = builder.Process(Event("Chrome", 0), TimeSpan.FromSeconds(30))!;
        Check(code.EndTime < code.StartTime && code.DurationSeconds == 30);
        var chrome = builder.Complete(DateTimeOffset.UnixEpoch.AddDays(10), TimeSpan.FromSeconds(50))!;
        Check(chrome.DurationSeconds == 20);
    }

    /// <summary>跨午夜和超过一天的会话保留完整日期与总秒数，不按小时字段截断。</summary>
    public static void CrossMidnight()
    {
        var builder = new AppSessionBuilder();
        var start = Event("Code", 86340);
        builder.Process(start, TimeSpan.Zero);
        var elapsed = TimeSpan.FromDays(1) + TimeSpan.FromMinutes(2);
        var ended = builder.Complete(start.Timestamp + elapsed, elapsed)!;
        Check(ended.Duration == elapsed && ended.DurationSeconds == 86520);
        Check(ended.EndTime!.Value.Date > ended.StartTime.Date);
    }

    /// <summary>正常退出只结束一次当前会话；核心构建器之后可用于新的观察区间。</summary>
    public static void Completion()
    {
        var builder = new AppSessionBuilder();
        Check(builder.Complete(DateTimeOffset.UnixEpoch, TimeSpan.Zero) is null);
        builder.Process(Event("Code", 10), TimeSpan.FromSeconds(10));
        var ended = builder.Complete(DateTimeOffset.UnixEpoch.AddSeconds(25), TimeSpan.FromSeconds(25))!;
        Check(ended.DurationSeconds == 15 && builder.CurrentSession is null);
        Check(builder.Complete(DateTimeOffset.UnixEpoch.AddSeconds(30), TimeSpan.FromSeconds(30)) is null);
        Check(builder.Process(Event("Chrome", 50), TimeSpan.FromSeconds(50)) is null);
        Check(builder.CurrentSession!.StartTime == DateTimeOffset.UnixEpoch.AddSeconds(50));
    }

    /// <summary>坏输入、负计时和乱序计时应被拒绝，且不得污染当前会话或最后有效计时。</summary>
    public static void InvalidInput()
    {
        var builder = new AppSessionBuilder();
        Throws<ArgumentOutOfRangeException>(() => builder.Process(Event("Code", 0), TimeSpan.FromSeconds(-1)));
        Throws<ArgumentNullException>(() => builder.Process(null!, TimeSpan.Zero));
        builder.Process(Event("Code", 10), TimeSpan.FromSeconds(10));
        var active = builder.CurrentSession;
        Throws<ArgumentOutOfRangeException>(() => builder.Process(Event("Chrome", 9), TimeSpan.FromSeconds(9)));
        Throws<ArgumentOutOfRangeException>(() => builder.Complete(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(9)));
        Throws<ArgumentException>(() => builder.Process(Event(" ", 50), TimeSpan.FromSeconds(50)));
        Check(builder.CurrentSession == active);
        Check(builder.Process(Event("Chrome", 11), TimeSpan.FromSeconds(11))!.DurationSeconds == 1);
    }

    /// <summary>允许同一时刻的零时长切换，后续短会话不丢失毫秒精度，描述缺失时有回退。</summary>
    public static void PrecisionAndFallback()
    {
        var builder = new AppSessionBuilder();
        builder.Process(Event("Code", 0, friendly: ""), TimeSpan.Zero);
        Check(builder.CurrentSession!.FriendlyName == "Code");
        Check(builder.Process(Event("Chrome", 0), TimeSpan.Zero)!.Duration == TimeSpan.Zero);
        var ended = builder.Complete(DateTimeOffset.UnixEpoch.AddMilliseconds(1250), TimeSpan.FromMilliseconds(1250))!;
        Check(ended.DurationSeconds == 1.25);
    }

    /// <summary>生成确定性低敏感度应用事件，日期时间与计时参数由各测试独立控制。</summary>
    internal static ForegroundChangedEvent Event(string name, int seconds, uint pid = 1, string? friendly = null)
    {
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        return new(null, new ForegroundApplication(pid, name, friendly ?? name, timestamp), timestamp);
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Session assertion failed.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
