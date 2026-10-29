using System.Text.Json;
using DesktopNarrator.Analysis;
using DesktopNarrator.Events;

/// <summary>纯初筛测试：固定时钟、明确输入事实，不调用系统监测、数据库或模型。</summary>
internal static class InterestingnessTests
{
    internal static readonly DateTimeOffset Start = MemoryTests.Start;
    internal static DetectedPattern Candidate(PatternType type = PatternType.LongSession, string? process = "Code", double amount = 5400)
    {
        var field = type switch
        {
            PatternType.LongSession => "DurationSeconds", PatternType.LongIdleReturn => "IdleDurationSeconds",
            PatternType.ClosedThenReopened => "GapSeconds", PatternType.RepeatedOpen => "ObservedStarts", _ => "SwitchCountAtLeast"
        };
        return new(Guid.NewGuid(), type, Start, Start, process, "已确认事实", JsonSerializer.Serialize(new Dictionary<string, double> { [field] = amount }));
    }
    private static ForegroundChangedEvent Focus(string name) => new(null, new(1, name, name, Start), Start);
    private static PatternEvaluation Evaluate(InterestingnessEngine engine, DetectedPattern pattern, double seconds = 0, DateTimeOffset? wall = null)
        => engine.EvaluateBatch([pattern], wall ?? Start.AddSeconds(seconds), TimeSpan.FromSeconds(seconds)).Single();
    private static bool Reason(PatternEvaluation value, string text) => value.Reasons.Any(x => x.Contains(text, StringComparison.Ordinal));

    public static void WeightedScoreAndThreshold()
    {
        var value = Evaluate(new(), Candidate());
        Check(value.IsSelected && value.Score == 83.5 && value.Threshold == 75, "Weighted cold-start score wrong");
        Check(value.Breakdown.Abnormality == .65 && value.Breakdown.PersonalRelevance == 1, "Component meaning changed");
        using var context = JsonDocument.Parse(value.ContextJson);
        Check(!context.RootElement.GetProperty("PersonalBaselineAvailable").GetBoolean(), "Invented personal baseline");
        Check(!context.RootElement.GetProperty("FullScreenChecked").GetBoolean(), "Invented fullscreen check");
        Check(Evaluate(new(new() { Threshold = 83.5 }), Candidate()).IsSelected, "Inclusive threshold missed");
        Check(!Evaluate(new(new() { Threshold = 83.51 }), Candidate()).IsSelected, "Below-threshold candidate passed");
    }

    public static void BackgroundAndSelf()
    {
        foreach (var name in new[] { "git", "GIT.EXE", "git-remote-https", "msbuild" })
        {
            var engine = new InterestingnessEngine();
            engine.Observe(Focus(name), TimeSpan.Zero);
            var value = Evaluate(engine, Candidate(PatternType.ClosedThenReopened, name, 55));
            Check(!value.IsSelected && Reason(value, "后台辅助进程"), "Tool noise passed");
        }
        var desktop = Evaluate(new(), Candidate(process: "desktopnarrator.app.exe"));
        Check(!desktop.IsSelected && Reason(desktop, "自身"), "Self-observation passed");
        var application = new InterestingnessEngine();
        application.Observe(Focus("GitKraken"), TimeSpan.Zero);
        Check(Evaluate(application, Candidate(PatternType.ClosedThenReopened, "gitkraken.exe", 55)).IsSelected, "Prefix matching hid a real application");
    }

    public static void ForegroundAssociation()
    {
        var unseen = Evaluate(new(), Candidate(PatternType.RepeatedOpen, "Steam", 6));
        Check(!unseen.IsSelected && Reason(unseen, "前台关联"), "Unseen lifecycle accepted");
        var engine = new InterestingnessEngine();
        engine.Observe(Focus("Steam"), TimeSpan.Zero);
        engine.Observe(Focus("Code"), TimeSpan.FromSeconds(1));
        var seen = Evaluate(engine, Candidate(PatternType.RepeatedOpen, "STEAM.exe", 6), 2);
        Check(seen.IsSelected && seen.Score == 80.5, "Known application association missing");
        var expired = Evaluate(engine, Candidate(PatternType.RepeatedOpen, "Steam", 6), 86402);
        Check(!expired.IsSelected && Reason(expired, "前台关联"), "Expired association retained");
    }

    public static void IdleAndRapidContext()
    {
        var idle = new InterestingnessEngine();
        idle.Observe(new UserIdleEvent(Start, Start), TimeSpan.Zero);
        Check(!Evaluate(idle, Candidate()).IsSelected, "Idle candidate passed");
        idle.Observe(new UserReturnedEvent(Start, TimeSpan.FromHours(1), Start), TimeSpan.FromSeconds(1));
        Check(Evaluate(idle, Candidate(PatternType.LongIdleReturn, null, 3600), 1).IsSelected, "Return did not restore context");
        var switching = new InterestingnessEngine();
        for (var i = 0; i <= 25; i++) switching.Observe(Focus(i % 2 == 0 ? "A" : "B"), TimeSpan.FromSeconds(i));
        var busy = Evaluate(switching, Candidate(PatternType.RapidSwitching, null, 25), 25);
        Check(!busy.IsSelected && Reason(busy, "快速切换"), "Busy context accepted");
        Check(!Evaluate(switching, Candidate(process: "C"), 54.999).IsSelected, "Quiet boundary fired early");
        Check(Evaluate(switching, Candidate(process: "D"), 55).IsSelected, "Quiet boundary failed to reopen");
    }

    public static void GlobalCooldownAndClockRollback()
    {
        var engine = new InterestingnessEngine();
        Check(Evaluate(engine, Candidate()).IsSelected, "First candidate not selected");
        var blocked = Evaluate(engine, Candidate(process: "Chrome"), 1199, Start.AddHours(-5));
        Check(!blocked.IsSelected && Math.Abs(blocked.GlobalCooldownRemainingSeconds - 1) < .0001, "Wall-clock adjustment altered cooldown");
        var next = Evaluate(engine, Candidate(process: "Steam"), 1200, Start.AddHours(-6));
        Check(next.IsSelected && next.GlobalCooldownRemainingSeconds == 0, "Rejected candidate extended global cooldown");
    }

    public static void TopicCooldownAndNovelty()
    {
        var engine = new InterestingnessEngine();
        Evaluate(engine, Candidate());
        var second = Evaluate(engine, Candidate(process: "CODE.EXE"), 1200);
        Check(!second.IsSelected && Reason(second, "同主题") && second.TopicCooldownRemainingSeconds == 9600, "Topic normalization/cooldown wrong");
        Check(second.Breakdown.Novelty == .35 && second.Breakdown.RepetitionPenalty == 5, "Repeated candidate was not penalized");
        // 冷却按通过时间计；新颖性按最近候选计，故等最近候选也超过窗口后再验收通过。
        Check(Evaluate(engine, Candidate(), 12000).IsSelected, "Topic did not recover after quiet window");
    }

    public static void RankingAndIdempotency()
    {
        var engine = new InterestingnessEngine();
        var low = Candidate(PatternType.LongIdleReturn, null, 1800); // 78 分。
        var high = Candidate(); // 83.5 分，虽然输入顺序靠后仍优先。
        var values = engine.EvaluateBatch([low, high, high], Start, TimeSpan.Zero);
        Check(values.Count == 2 && values[0].PatternId == high.Id && values[0].IsSelected && !values[1].IsSelected, "Batch ranking/duplicate handling wrong");
        Check(engine.EvaluateBatch([high], Start, TimeSpan.Zero).Count == 0, "Same ID evaluated twice");
        Check(Evaluate(engine, Candidate(process: "Steam"), 1200).IsSelected, "Duplicate consumed extra cooldown");
    }

    public static void InvalidEvidenceAndOptions()
    {
        var engine = new InterestingnessEngine();
        foreach (var json in new[] { "{", "[]", "{}", "{\"DurationSeconds\":\"5400\"}", "{\"DurationSeconds\":-1}", "{\"DurationSeconds\":1e999}", "{\"DurationSeconds\":0}" })
        {
            var value = Evaluate(engine, Candidate() with { EvidenceJson = json });
            Check(!value.IsSelected && value.Score == 0 && Reason(value, "证据"), "Invalid evidence was guessed/accepted");
        }
        Check(Evaluate(engine, Candidate(process: "Fresh")).IsSelected, "Invalid evidence consumed cooldown");
        Throws<ArgumentOutOfRangeException>(() => engine.EvaluateBatch([], Start, TimeSpan.FromSeconds(-1)));
        Throws<ArgumentOutOfRangeException>(() => new InterestingnessEngine(new() { Threshold = double.NaN }));
        Throws<ArgumentOutOfRangeException>(() => new InterestingnessEngine(new() { TopicInterval = TimeSpan.FromSeconds(1) }));
    }

    public static void ResetDoesNotCancelCooldown()
    {
        var engine = new InterestingnessEngine();
        Evaluate(engine, Candidate());
        engine.Observe(new ProcessObservationResetEvent(Start), TimeSpan.FromSeconds(1));
        Check(!Evaluate(engine, Candidate(process: "Chrome"), 1).IsSelected, "Process reset bypassed selection interval");
        // 重启语义明确：新实例没有上一运行的评分上下文，不伪造跨运行的旁白记录。
        Check(Evaluate(new(), Candidate()).IsSelected, "New run inherited hidden state");
    }

    public static void NoisePressurePreservesSelectedTopic()
    {
        var engine = new InterestingnessEngine();
        Evaluate(engine, Candidate());
        var noise = Enumerable.Range(0, 1100).Select(i => Candidate(PatternType.RepeatedOpen, $"helper-{i}", 6)).ToArray();
        Check(engine.EvaluateBatch(noise, Start, TimeSpan.FromSeconds(1)).All(x => !x.IsSelected), "Background burst accepted");
        var value = Evaluate(engine, Candidate(), 1200);
        Check(!value.IsSelected && value.TopicCooldownRemainingSeconds == 9600, "Noise evicted active topic cooldown");
    }

    /// <summary>合成混合时间线仅证明该样例多数被过滤，不宣称真实用户的固定通过率。</summary>
    public static void MixedReplay()
    {
        static string[] Replay()
        {
            var engine = new InterestingnessEngine();
            var rows = new List<PatternEvaluation>();
            for (var i = 0; i < 40; i++) rows.Add(Evaluate(engine, Candidate(PatternType.ClosedThenReopened, "git", 55), i));
            for (var i = 40; i < 80; i++) rows.Add(Evaluate(engine, Candidate(PatternType.RepeatedOpen, "unseen-helper", 6), i));
            rows.Add(Evaluate(engine, Candidate(), 80));
            for (var i = 81; i < 100; i++) rows.Add(Evaluate(engine, Candidate(), i));
            Check(rows.Count(x => x.IsSelected) == 1, "Mixed replay admitted noise");
            return rows.Select(x => $"{x.Score}|{x.IsSelected}|{string.Join(';', x.Reasons)}").ToArray();
        }
        Check(Replay().SequenceEqual(Replay()), "Replay decisions are nondeterministic");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
