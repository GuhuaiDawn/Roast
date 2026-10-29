using DesktopNarrator.Events;
using DesktopNarrator.Monitor;

// 隐藏原生进程夹具：只等待父测试关闭标准输入，不启动监测、数据库或界面。
if (args.Contains("--process-fixture")) { Console.ReadLine(); return 0; }

// 无第三方测试框架的回归入口：既测试纯状态逻辑，也包含需要 Windows 桌面会话的原生/WPF 测试。
var tests = new (string Name, Action Body)[]
{
    ("First application becomes a transition", FirstApplicationBecomesTransition),
    ("Same process is de-duplicated", SameProcessIsDeduplicated),
    ("Process switch preserves previous and current", ProcessSwitchPreservesApplications),
    ("Process name comparison is case insensitive", ProcessNameComparisonIsCaseInsensitive),
    ("WinEventHook emits the current foreground application", WinEventHookEmitsCurrentApplication),
    ("Idle threshold and duplicate suppression", IdleTests.ThresholdAndDeduplication),
    ("Return duration excludes polling delay", IdleTests.ReturnDuration),
    ("Clock changes do not affect idle duration", IdleTests.ClockChange),
    ("32-bit tick rollover and long-running idle", IdleTests.TickRollover),
    ("Non-incremental input ticks mean activity", IdleTests.NonIncrementalInput),
    ("Polling gaps can contain return and new idle", IdleTests.PollingGap),
    ("Idle arguments are validated", IdleTests.InvalidArguments),
    ("Idle monitor recovers and isolates subscribers", IdleTests.RecoveryAndLifecycle),
    ("Read failure preserves an existing idle period", IdleTests.ReturnAfterReadFailure),
    ("Idle monitor can stop inside a callback", IdleTests.StopInCallback),
    ("Native idle input can be sampled", IdleTests.NativeInput),
    ("Log window stays consistent during bursts and reopen", DeveloperLogTests.BurstsAndReopen),
    ("Log collection updates on its owning UI thread", DeveloperLogTests.BackgroundWriter),
    ("Log append during notification does not reenter the collection", DeveloperLogTests.ReentrantAppend),
    ("Session starts at the first observed application", AppSessionTests.InitialObservation),
    ("Session switching preserves boundaries and identities", AppSessionTests.Switching),
    ("Session duplicate application does not reset its start", AppSessionTests.DuplicateApplication),
    ("Session duration is independent of wall-clock changes", AppSessionTests.WallClockChanges),
    ("Session supports cross-midnight and multi-day durations", AppSessionTests.CrossMidnight),
    ("Session completion is idempotent", AppSessionTests.Completion),
    ("Session rejects invalid input without changing state", AppSessionTests.InvalidInput),
    ("Session supports zero duration and fractional seconds", AppSessionTests.PrecisionAndFallback),
    ("Log session pipeline finishes before UI queue drains", DeveloperLogTests.SessionPipeline),
    ("Log current session survives window reopening", DeveloperLogTests.SessionView),
    ("Session native foreground pipeline can be finalized", DeveloperLogTests.NativeSessionPipeline),
    ("Memory survives reopen with exact values", MemoryTests.RoundTrip),
    ("Memory retries are idempotent", MemoryTests.Idempotency),
    ("Memory transactions roll back the entire batch", MemoryTests.AtomicRollback),
    ("Memory splits sessions at local midnight", MemoryTests.CrossMidnight),
    ("Memory respects daylight saving day lengths", MemoryTests.DaylightSaving),
    ("Memory preserves monotonic duration after clock changes", MemoryTests.ClockAdjustment),
    ("Memory paginates history without truncating totals", MemoryTests.Pagination),
    ("Memory preserves unsupported and corrupt databases", MemoryTests.PreserveUnknownDatabase),
    ("Memory retries after a database lock is released", MemoryTests.LockedDatabaseRecovery),
    ("Memory recovers from unavailable initialization path", MemoryTests.InitializationRecovery),
    ("Memory has bounded queues and shutdown waiting", MemoryTests.QueueAndShutdownLimits),
    ("Memory event records follow the privacy whitelist", MemoryTests.EventPrivacy),
    ("Log session pipeline persists across restart", DeveloperLogTests.PersistedSessionPipeline),
    ("Log saved history supports dates and pagination", DeveloperLogTests.PersistedHistoryView),
    ("Pattern long session thresholds and deduplication", PatternTests.LongSession),
    ("Pattern long session on switch and clock adjustment", PatternTests.LongSessionOnSwitchAndClockChange),
    ("Pattern rapid switching counts and episodes", PatternTests.RapidSwitching),
    ("Pattern switching window boundaries and burst bound", PatternTests.SwitchWindowBoundary),
    ("Pattern reopen boundaries and duplicate lifecycle", PatternTests.ReopenBoundaryAndDeduplication),
    ("Pattern repeated open daily threshold and date reset", PatternTests.RepeatedOpenAndDateReset),
    ("Pattern lifecycle observation gap resets evidence", PatternTests.ObservationGap),
    ("Pattern long idle and invalid input", PatternTests.LongIdleAndValidation),
    ("Pattern deterministic replay", PatternTests.DeterministicReplay),
    ("Process baseline and multi-process groups", ProcessTests.BaselineAndGroups),
    ("Process observation gaps and reset", ProcessTests.GapsAndReset),
    ("Process recovery lifecycle and subscriber isolation", ProcessTests.RecoveryAndSubscriberIsolation),
    ("Process native session snapshot", ProcessTests.NativeSnapshot),
    ("Process native start and close of controlled child", ProcessTests.NativeLifecycle),
    ("Memory upgrades v1 database without changing history", MemoryTests.PatternSchemaMigration),
    ("Memory persists pattern evidence and lifecycle events", MemoryTests.PatternPersistence),
    ("Memory rolls back invalid pattern batches", MemoryTests.PatternAtomicRollback),
    ("Log pattern pipeline and bounded candidate view", DeveloperLogTests.PatternPipelineAndView),
    ("Scoring weighted components and threshold boundary", InterestingnessTests.WeightedScoreAndThreshold),
    ("Scoring background tools and self are filtered", InterestingnessTests.BackgroundAndSelf),
    ("Scoring lifecycle requires recent foreground association", InterestingnessTests.ForegroundAssociation),
    ("Scoring idle and rapid-switch contexts", InterestingnessTests.IdleAndRapidContext),
    ("Scoring global cooldown ignores wall-clock changes", InterestingnessTests.GlobalCooldownAndClockRollback),
    ("Scoring topic cooldown novelty and repetition", InterestingnessTests.TopicCooldownAndNovelty),
    ("Scoring ranks a batch and deduplicates candidate IDs", InterestingnessTests.RankingAndIdempotency),
    ("Scoring invalid evidence and options fail safely", InterestingnessTests.InvalidEvidenceAndOptions),
    ("Scoring observation reset does not bypass cooldown", InterestingnessTests.ResetDoesNotCancelCooldown),
    ("Scoring noise pressure preserves selected topic", InterestingnessTests.NoisePressurePreservesSelectedTopic),
    ("Scoring mixed timeline replay filters most candidates", InterestingnessTests.MixedReplay),
    ("Memory upgrades v2 without fabricating historical scores", MemoryTests.EvaluationSchemaMigration),
    ("Memory preserves passed and filtered scoring snapshots", MemoryTests.EvaluationRoundTrip),
    ("Memory scoring and candidates commit atomically", MemoryTests.EvaluationAtomicity),
    ("Log scoring pipeline reasons and decision view", DeveloperLogTests.ScoringPipelineAndView)
};

var failed = 0;
foreach (var test in tests)
{
    // 仅调试日志界面时可跳过系统监测测试；筛选依赖下方测试名称的 Log 前缀约定。
    if (args.Contains("--log-ui-only") && !test.Name.StartsWith("Log ", StringComparison.Ordinal)) continue;
    if (args.Contains("--memory-only") && !test.Name.StartsWith("Memory ", StringComparison.Ordinal)) continue;
    if (args.Contains("--analysis-only") && !test.Name.StartsWith("Pattern ", StringComparison.Ordinal)) continue;
    if (args.Contains("--process-only") && !test.Name.StartsWith("Process ", StringComparison.Ordinal)) continue;
    if (args.Contains("--scoring-only") && !test.Name.StartsWith("Scoring ", StringComparison.Ordinal)) continue;
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

// 用进程退出码供命令行或后续 CI 判断结果，不只依赖打印的 PASS/FAIL 文本。
return failed == 0 ? 0 : 1;

// 首次采样也必须发事件，且 PreviousApplication 明确为空，不虚构上一个应用。
static void FirstApplicationBecomesTransition()
{
    var tracker = new ForegroundTransitionTracker();
    var application = Application(100, "Code", "Visual Studio Code", 10);

    Assert(tracker.TryCreateTransition(application, out var transition));
    Assert(transition is not null);
    Assert(transition!.PreviousApplication is null);
    Assert(transition.CurrentApplication == application);
}

// 同名应用即使 PID 不同也不重复发切换事件，但最新快照必须保留。
static void SameProcessIsDeduplicated()
{
    var tracker = new ForegroundTransitionTracker();
    Assert(tracker.TryCreateTransition(Application(100, "Chrome", "Chrome", 10), out _));

    var duplicate = Application(101, "Chrome", "Chrome", 20);
    Assert(!tracker.TryCreateTransition(duplicate, out var transition));
    Assert(transition is null);
    Assert(tracker.CurrentApplication == duplicate);
}

// 确认事件同时保留切换前后快照，供下一阶段 Session 聚合使用。
static void ProcessSwitchPreservesApplications()
{
    var tracker = new ForegroundTransitionTracker();
    var first = Application(100, "Code", "Code", 10);
    var second = Application(200, "Chrome", "Chrome", 20);
    tracker.TryCreateTransition(first, out _);

    Assert(tracker.TryCreateTransition(second, out var transition));
    Assert(transition!.PreviousApplication == first);
    Assert(transition.CurrentApplication == second);
}

// Windows 应用名称大小写差异不应制造虚假的前台切换。
static void ProcessNameComparisonIsCaseInsensitive()
{
    var tracker = new ForegroundTransitionTracker();
    tracker.TryCreateTransition(Application(100, "STEAM", "Steam", 10), out _);

    Assert(!tracker.TryCreateTransition(Application(200, "steam", "Steam", 20), out _));
}

// 真实 WinEventHook 冒烟检查：验证启动时能取得初始前台应用，不模拟用户窗口操作。
static void WinEventHookEmitsCurrentApplication()
{
    using var monitor = new ForegroundMonitor();
    using var received = new ManualResetEventSlim(false);
    ForegroundApplication? observedApplication = null;

    monitor.ForegroundChanged += (_, transition) =>
    {
        observedApplication = transition.CurrentApplication;
        received.Set();
    };

    monitor.Start();
    try
    {
        Assert(monitor.IsRunning);
        Assert(received.Wait(TimeSpan.FromSeconds(2)));
        Assert(observedApplication is not null);
        Assert(!string.IsNullOrWhiteSpace(observedApplication!.ProcessName));
    }
    finally
    {
        monitor.Stop();
    }
}

// 纯逻辑测试使用固定纪元生成时间，避免依赖测试执行时的实际系统日期。
static ForegroundApplication Application(uint processId, string processName, string friendlyName, int seconds)
    => new(processId, processName, friendlyName, DateTimeOffset.UnixEpoch.AddSeconds(seconds));

static void Assert(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Assertion failed.");
    }
}
