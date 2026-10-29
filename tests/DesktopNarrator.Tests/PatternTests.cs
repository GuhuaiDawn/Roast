using System.Text.Json;
using DesktopNarrator.Analysis;
using DesktopNarrator.Events;

/// <summary>纯时间线验收：数小时的输入立即重放，不修改系统时间或生成真实用户输入。</summary>
internal static class PatternTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 10, 0, 0, TimeSpan.FromHours(8));
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("PatternTests", TimeSpan.FromHours(8), "Test", "Test");
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
    private static ForegroundChangedEvent Focus(string name, double seconds) =>
        new(null, new(1, name, name, Start.AddSeconds(seconds)), Start.AddSeconds(seconds));

    public static void LongSession()
    {
        var detector = new PatternDetector();
        Check(detector.Observe(Focus("Code", 0), At(0)).Count == 0, "Initial focus triggered");
        Check(detector.Tick(Start.AddSeconds(5399), At(5399)).Count == 0, "Long session fired early");
        detector.Observe(Focus("CODE", 5399), At(5399));
        var pattern = detector.Tick(Start.AddMinutes(90), At(5400)).Single();
        Check(pattern.Type == PatternType.LongSession && pattern.ProcessName == "Code", "Duplicate focus reset session");
        Check(detector.Tick(Start.AddMinutes(100), At(6000)).Count == 0, "Long session repeated");
        detector.Observe(Focus("Chrome", 6001), At(6001));
        Check(detector.Tick(Start.AddSeconds(11401), At(11401)).Single().ProcessName == "Chrome", "New session did not rearm");
    }

    public static void LongSessionOnSwitchAndClockChange()
    {
        var detector = new PatternDetector();
        detector.Observe(Focus("Code", 0), At(0));
        var pattern = detector.Observe(Focus("Chrome", -100), At(5400)).Single();
        Check(pattern.Type == PatternType.LongSession && pattern.EndTime < pattern.StartTime, "Wall clock was used for duration");
        using var json = JsonDocument.Parse(pattern.EvidenceJson);
        Check(json.RootElement.GetProperty("DurationSeconds").GetDouble() == 5400, "Monotonic evidence wrong");
    }

    public static void RapidSwitching()
    {
        var detector = new PatternDetector(new() { SwitchCount = 3, SwitchWindow = At(30) });
        detector.Observe(Focus("A", 0), At(0));
        Check(detector.Observe(Focus("B", 1), At(1)).Count == 0, "Initial snapshot counted as switch");
        detector.Observe(Focus("b", 1), At(1));
        detector.Observe(Focus("C", 2), At(2));
        var pattern = detector.Observe(Focus("D", 3), At(3)).Single();
        Check(pattern.Type == PatternType.RapidSwitching, "Rapid switching not detected");
        using var json = JsonDocument.Parse(pattern.EvidenceJson);
        Check(json.RootElement.GetProperty("DistinctApplicationsInSample").GetInt32() == 4, "Application sample wrong");
        Check(detector.Observe(Focus("A", 4), At(4)).Count == 0, "Rapid episode repeated");
        detector.Tick(Start.AddSeconds(35), At(35));
        detector.Observe(Focus("B", 36), At(36));
        detector.Observe(Focus("C", 37), At(37));
        Check(detector.Observe(Focus("D", 38), At(38)).Single().Type == PatternType.RapidSwitching, "Episode never rearmed");
    }

    public static void SwitchWindowBoundary()
    {
        var detector = new PatternDetector(new() { SwitchCount = 3, SwitchWindow = At(30) });
        detector.Observe(Focus("A", 0), At(0));
        detector.Observe(Focus("B", 1), At(1));
        detector.Observe(Focus("C", 2), At(2));
        Check(detector.Observe(Focus("D", 31), At(31)).Count == 0, "Left window boundary was not excluded");
        // 同一时刻的大量输入只产生一次阈值上穿候选，不因计数增加重复报告。
        var count = 0;
        for (var i = 0; i < 10000; i++) count += detector.Observe(Focus(i % 2 == 0 ? "A" : "B", 31), At(31)).Count;
        Check(count == 1, "Burst was repeatedly reported");
    }

    public static void ReopenBoundaryAndDeduplication()
    {
        var detector = new PatternDetector();
        detector.Observe(new AppClosedEvent("Steam", Start), At(0));
        detector.Observe(new AppClosedEvent("steam", Start.AddSeconds(100)), At(100));
        var result = detector.Observe(new AppStartedEvent("STEAM", Start.AddMinutes(10)), At(600));
        Check(result.Single().Type == PatternType.ClosedThenReopened, "Inclusive reopen boundary missed");
        Check(detector.Observe(new AppStartedEvent("steam", Start.AddMinutes(10)), At(600)).Count == 0, "Duplicate start triggered");
        detector.Observe(new AppClosedEvent("steam", Start.AddSeconds(700)), At(700));
        Check(detector.Observe(new AppStartedEvent("steam", Start.AddSeconds(1301)), At(1301)).Count == 0, "Expired close used");
    }

    public static void RepeatedOpenAndDateReset()
    {
        var detector = new PatternDetector(zone: Zone);
        var repeated = new List<DetectedPattern>();
        for (var i = 0; i < 8; i++)
        {
            repeated.AddRange(detector.Observe(new AppStartedEvent("Steam", Start.AddSeconds(i * 2)), At(i * 2)));
            repeated.AddRange(detector.Observe(new AppStartedEvent("STEAM", Start.AddSeconds(i * 2)), At(i * 2)));
            detector.Observe(new AppClosedEvent("Steam", Start.AddSeconds(i * 2 + 1)), At(i * 2 + 1));
        }
        Check(repeated.Count(p => p.Type == PatternType.RepeatedOpen) == 1, "Repeated open counted duplicates or spammed");
        var nextDay = detector.Observe(new AppStartedEvent("Steam", Start.AddDays(1)), At(86400));
        Check(!nextDay.Any(p => p.Type == PatternType.RepeatedOpen), "Daily count did not reset");
    }

    public static void ObservationGap()
    {
        var detector = new PatternDetector(new() { DailyOpenCount = 2 });
        detector.Observe(new AppStartedEvent("Code", Start), At(0));
        detector.Observe(new AppClosedEvent("Code", Start.AddSeconds(1)), At(1));
        detector.Observe(new ProcessObservationResetEvent(Start.AddSeconds(2)), At(2));
        Check(detector.Observe(new AppStartedEvent("Code", Start.AddSeconds(3)), At(3)).Count == 0, "Lifecycle inferred across gap");
    }

    public static void LongIdleAndValidation()
    {
        var detector = new PatternDetector();
        Check(detector.Observe(new UserReturnedEvent(Start, At(1799), Start), At(0)).Count == 0, "Idle fired early");
        var returned = new UserReturnedEvent(Start.AddSeconds(1), At(1800), Start.AddSeconds(2));
        Check(detector.Observe(returned, At(2)).Single().Type == PatternType.LongIdleReturn, "Idle threshold missed");
        Check(detector.Observe(returned, At(3)).Count == 0, "Duplicate idle return repeated");
        Throws<ArgumentOutOfRangeException>(() => detector.Tick(Start, At(1)));
        Throws<ArgumentOutOfRangeException>(() => detector.Observe(new UserReturnedEvent(Start, At(-1), Start), At(100)));
        Check(detector.Tick(Start, At(4)).Count == 0, "Invalid input advanced state");
        Throws<ArgumentOutOfRangeException>(() => new PatternDetector(new() { SwitchCount = 1 }));
    }

    /// <summary>重放比较事实与证据；每次运行新生成的 ID 独立，避免与旧数据库历史冲突。</summary>
    public static void DeterministicReplay()
    {
        IMonitorEvent[] events = [Focus("Code", 0), Focus("Chrome", 5400),
            new UserReturnedEvent(Start.AddHours(2), TimeSpan.FromHours(1), Start.AddHours(2)),
            new AppClosedEvent("Steam", Start.AddHours(2)), new AppStartedEvent("Steam", Start.AddHours(2).AddMinutes(7))];
        double[] times = [0, 5400, 7200, 7200, 7620];
        string[] Replay()
        {
            var detector = new PatternDetector(zone: Zone);
            return events.SelectMany((e, i) => detector.Observe(e, At(times[i])))
                .Select(p => $"{p.Type}|{p.Summary}|{p.EvidenceJson}").ToArray();
        }
        Check(Replay().SequenceEqual(Replay()) && Replay().Length == 3, "Replay facts changed between runs");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
