using System.Globalization;
using DesktopNarrator.App;
using DesktopNarrator.Events;
using DesktopNarrator.Memory;
using System.Windows;
using System.Windows.Media;

/// <summary>展示层回归；数据模型、评分与原始精度不因本地化格式发生变化。</summary>
internal static class PresentationTests
{
    /// <summary>覆盖不足一秒、整分、跨天、非法值；不把四舍五入的时长当作原始计时。</summary>
    public static void Durations()
    {
        Equal("0 秒", ActivityPresentation.Duration(0));
        Equal("不足 1 秒", ActivityPresentation.Duration(.25));
        Equal("59 秒", ActivityPresentation.Duration(59.999));
        Equal("1 分钟", ActivityPresentation.Duration(60));
        Equal("1 小时 30 分钟", ActivityPresentation.Duration(5400));
        Equal("1 天 2 小时 3 分钟 4 秒", ActivityPresentation.Duration(93784));
        foreach (var invalid in new[] { -1, double.NaN, double.PositiveInfinity, double.MaxValue })
            Equal("时长未知", ActivityPresentation.Duration(invalid));
    }

    /// <summary>友好名称优先、未知应用不猜名称；跨日跨年时间范围不能只显示小时分钟。</summary>
    public static void NamesAndRanges()
    {
        Equal("Visual Studio Code", ActivityPresentation.AppName("Code"));
        Equal("自定义应用", ActivityPresentation.AppName("Code", "自定义应用"));
        Equal("UnknownTool", ActivityPresentation.AppName("UnknownTool"));
        Equal("未知应用", ActivityPresentation.AppName(null));
        var session = MemoryTests.Session("Code", 1200, new(2026, 10, 28, 23, 50, 0, TimeSpan.FromHours(8)));
        Equal("10-28 23:50:00 → 10-29 00:10:00", ActivityPresentation.SessionRange(session));
        var year = MemoryTests.Session("Code", 1200, new(2026, 12, 31, 23, 50, 0, TimeSpan.FromHours(8)));
        Equal("2026-12-31 23:50:00 → 2027-01-01 00:10:00", ActivityPresentation.SessionRange(year));
        var raw = new ActivityDisplayConverter().Convert(session, typeof(string), "Details", CultureInfo.InvariantCulture).ToString()!;
        Check(raw.Contains("+08:00") && raw.Contains("00:20:00"), "Raw offset/precision missing from tooltip");
    }

    /// <summary>中文事实仍基于数值证据，不把快照出现解释为主动操作或实际阅读。</summary>
    public static void PatternFacts()
    {
        PatternReview Row(PatternType type, string json) => new(new(Guid.NewGuid(), type, MemoryTests.Start,
            MemoryTests.Start, "Code", "原始摘要", json), null);
        var longSession = Row(PatternType.LongSession, "{\"DurationSeconds\":5400}");
        Equal("长时间停留", longSession.TypeLabel);
        Check(longSession.ReadableSummary.Contains("1 小时 30 分钟") && longSession.ReadableSummary.Contains("无输入"), "Long-session qualification lost");
        Check(Row(PatternType.ClosedThenReopened, "{\"GapSeconds\":55}").ReadableSummary.Contains("不一定是主动重开"), "Snapshot overclaimed intent");
        Check(Row(PatternType.LongIdleReturn, "{\"IdleDurationSeconds\":1800}").ReadableSummary.Contains("不代表确认离开"), "Idle overclaimed physical absence");
        Check(Row(PatternType.RapidSwitching, "{\"WindowSeconds\":300,\"SwitchCountAtLeast\":25,\"DistinctApplicationsInSample\":9}").ReadableSummary.Contains("至少切换了 25 次"), "Bounded sample changed to exact count");
        Check(Row(PatternType.RepeatedOpen, "{\"ObservedStarts\":6}").ReadableSummary.Contains("本次连续监测"), "Run scope missing");
        foreach (var invalid in new[] { "not json", "[]", "{}", "{\"DurationSeconds\":\"5400\"}", "{\"DurationSeconds\":-1}" })
            Check(Row(PatternType.LongSession, invalid).ReadableSummary.Contains("证据不完整"), "Invalid evidence was rendered as fact");
        Equal("未评估", longSession.DisplayDecision);
        Check(longSession.ReadableDetails.Contains("没有发送 AI 旁白"), "Unscored record implies delivery");
    }

    /// <summary>统计卡片与占比始终取完整当日结果，零总量不产生无穷值。</summary>
    public static void SummaryAndShares()
    {
        var day = new DayHistory(MemoryTests.Day,
            [new("Code", "Code", 5400, 1), new("Chrome", "Chrome", 2400, 1), new("QQ", "QQ", 1200, 1)],
            [], 3, 100, 100);
        var overview = HistoryOverview.From(day);
        Check(overview.TotalSeconds == 9000 && overview.SessionCount == 3 && overview.ApplicationCount == 3, "Overview incorrectly used current page");
        Equal("2 小时 30 分钟", overview.DurationText);
        var converter = new UsageShareConverter();
        Equal("60%", converter.Convert([5400d, 9000d], typeof(string), "", CultureInfo.InvariantCulture).ToString()!);
        Equal("26.7%", converter.Convert([2400d, 9000d], typeof(string), "", CultureInfo.InvariantCulture).ToString()!);
        Equal("—", converter.Convert([0d, 0d], typeof(string), "", CultureInfo.InvariantCulture).ToString()!);
    }

    /// <summary>分布包含全部正时长；大量小应用仅合并显示，不丢弃占比或精确秒数。</summary>
    public static void PieDistribution()
    {
        var normal = ApplicationDistribution.Build([new("Code", "Code", 5400, 1), new("Chrome", "Chrome", 2400, 1), new("QQ", "QQ", 1200, 1)]);
        Check(normal.Count == 3 && normal[0].Seconds == 5400 && normal[0].Share == .6, "Pie does not reflect day totals");
        Check(normal.Select(x => x.Color).Distinct().Count() == 3, "Colors cannot distinguish sectors");
        var many = ApplicationDistribution.Build(Enumerable.Range(1, 10).Select(i => new DailyAppTotal($"App{i}", $"App{i}", i, 1)).ToArray());
        Check(many.Count == 7 && many[^1].Label == "其他（4 个应用）" && many.Sum(x => x.Seconds) == 55, "Small applications lost during aggregation");
        Check(Math.Abs(many.Sum(x => x.Share) - 1) < 1e-12 && many[^1].Details.Contains("App1"), "Other group does not preserve proportions/details");
        Check(ApplicationDistribution.Build([new("Zero", "Zero", 0, 1), new("Bad", "Bad", double.NaN, 1)]).Count == 0, "Zero/invalid day fabricated a pie");
        var single = ApplicationDistribution.Build([new("Code", "Code", 12, 1)]).Single();
        Check(single.Share == 1 && single.ShareText == "100%", "Single application not full circle");
    }

    /// <summary>检查真实扇形面积、方向、大圆弧和 100% 整圆，防止圆弧首尾重合导致空白。</summary>
    public static void PieGeometry()
    {
        var full = ApplicationPieChart.CreateSector(new Point(0, 0), 10, -90, 1);
        Check(full is EllipseGeometry && Math.Abs(full.GetArea() - Math.PI * 100) < .01, "100% sector rendered empty");
        var quarter = ApplicationPieChart.CreateSector(new Point(0, 0), 10, -90, .25);
        Check(quarter.FillContains(new Point(5, -5)) && !quarter.FillContains(new Point(-5, -5)), "Sector direction incorrect");
        Check(Math.Abs(quarter.GetArea() / full.GetArea() - .25) < .01, "Sector area not proportional to share");
        var large = ApplicationPieChart.CreateSector(new Point(0, 0), 10, -90, .75);
        Check(large.FillContains(new Point(-5, 5)) && !large.FillContains(new Point(-5, -5)), "Large arc lost major sector");
        Check(ApplicationPieChart.CreateSector(new Point(), 10, 0, 0).IsEmpty(), "Zero share creates a visible slice");
        Check(ApplicationPieChart.CreateSector(new Point(), 0, 0, .5).IsEmpty(), "Invalid radius is not empty");
    }

    private static void Equal(string expected, string actual) => Check(expected == actual, $"Expected '{expected}', got '{actual}'");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
