using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopNarrator.App;
using DesktopNarrator.Monitor;
using DesktopNarrator.Memory;
using DesktopNarrator.Events;

/// <summary>真实 WPF 绑定与布局回归，覆盖曾导致 ItemsControl 项源不一致的自动滚动问题。</summary>
internal static class DeveloperLogTests
{
    /// <summary>连续追加并多次关闭/重开窗口，校验项目数量、末项滚动和取消待执行滚动。</summary>
    public static void BurstsAndReopen() => OnSta(() =>
    {
        var store = new DeveloperLogStore();
        store.Append("Before opening", LogCategory.System);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var window = new DeveloperLogWindow(store)
            {
                // 使用真实 WPF 视图和布局，但不显示测试窗口，也不抢占用户焦点。
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000
            };
            try
            {
                window.Show();
                DrainDispatcher();
                var list = (ListBox)window.FindName("LogList");
                for (var i = 0; i < 100; i++)
                {
                    store.Append($"Cycle {cycle}, event {i}", LogCategory.Monitor);
                    if (i % 10 == 0) DrainDispatcher();
                }
                DrainDispatcher();
                list.UpdateLayout();
                Check(list.Items.Count == store.Entries.Count, "ItemsSource count mismatch");
                Check(Equals(list.Items[^1], store.Entries[^1]), "Latest log is missing");
                Check(list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) is ListBoxItem,
                    "Auto-scroll did not realize the last item");
                // 只排空 Background 写入，保留较低优先级的滚动请求，然后关闭窗口。
                store.Append("Immediately before close", LogCategory.System);
                DrainDispatcher(DispatcherPriority.Background);
            }
            finally { window.Close(); }
            store.Append("After close", LogCategory.System);
            DrainDispatcher();
        }
    });

    /// <summary>后台线程可以提交日志，但实际集合通知必须回到存储所属的 UI 线程。</summary>
    public static void BackgroundWriter() => OnSta(() =>
    {
        var ownerThread = Environment.CurrentManagedThreadId;
        var wrongThread = false;
        var store = new DeveloperLogStore();
        ((INotifyCollectionChanged)store.Entries).CollectionChanged += (_, _) =>
            wrongThread |= Environment.CurrentManagedThreadId != ownerThread;
        Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) store.Append($"Background {i}", LogCategory.Error);
        }).GetAwaiter().GetResult();
        DrainDispatcher();
        Check(!wrongThread, "CollectionChanged was raised outside the UI thread");
        Check(store.Entries.Count == 100, "Background logs were lost");
    });

    /// <summary>在集合通知中再次写日志，也应排队执行，不能嵌套触发集合通知。</summary>
    public static void ReentrantAppend() => OnSta(() =>
    {
        var store = new DeveloperLogStore();
        var depth = 0;
        var maximumDepth = 0;
        ((INotifyCollectionChanged)store.Entries).CollectionChanged += (_, _) =>
        {
            depth++;
            maximumDepth = Math.Max(maximumDepth, depth);
            if (store.Entries.Count == 1) store.Append("Nested diagnostic", LogCategory.Error);
            depth--;
        };
        store.Append("First event", LogCategory.Monitor);
        DrainDispatcher();
        Check(store.Entries.Count == 2, "Nested diagnostic was lost");
        Check(maximumDepth == 1, "Collection notifications reentered");
    });

    /// <summary>事件在后台到达后立即收尾，UI 尚未处理任何日志，也必须得到完整且只结束一次的会话。</summary>
    public static void SessionPipeline() => OnSta(() =>
    {
        var store = new DeveloperLogStore();
        var clock = new SessionClock();
        var recorder = new SessionLogRecorder(store, clock);
        var ownerThread = Environment.CurrentManagedThreadId;
        var wrongThread = false;
        store.PropertyChanged += (_, _) => wrongThread |= Environment.CurrentManagedThreadId != ownerThread;
        Task.Run(() =>
        {
            recorder.Record(AppSessionTests.Event("Code", 0));
            clock.Advance(TimeSpan.FromMinutes(8));
            recorder.Record(AppSessionTests.Event("Chrome", 480));
            clock.Advance(TimeSpan.FromMinutes(2));
            recorder.Record(AppSessionTests.Event("CHROME", 600));
            clock.Advance(TimeSpan.FromMinutes(38));
            var ended = recorder.Complete(DateTimeOffset.UnixEpoch.AddMinutes(48));
            Check(ended?.ProcessName == "Chrome" && ended.DurationSeconds == 2400, "Shutdown session lost or shortened");
            Check(recorder.Complete(DateTimeOffset.UnixEpoch.AddMinutes(48)) is null, "Duplicate shutdown result");
            recorder.Record(AppSessionTests.Event("LateApp", 3000));
        }).GetAwaiter().GetResult();
        Check(store.Entries.Count == 0, "Unexpected synchronous UI update");
        DrainDispatcher();
        Check(store.Entries.Count(entry => entry.Contains("SessionStarted:")) == 2, "Wrong number of session starts");
        Check(store.Entries.Count(entry => entry.Contains("SessionEnded:")) == 2, "Wrong number of session completions");
        Check(store.Entries.Any(entry => entry.Contains("00:08:00")), "First session duration missing");
        Check(store.Entries.Any(entry => entry.Contains("00:40:00")), "Final session duration missing");
        Check(!store.Entries.Any(entry => entry.Contains("LateApp")), "Late callback reopened a completed recorder");
        Check(!wrongThread && store.CurrentSessionSummary.Contains("监测已结束"), "Session binding updated on wrong thread/state");
    });

    /// <summary>真实 WPF 绑定能展示当前会话；关闭窗口不停止聚合，再打开显示期间的新应用。</summary>
    public static void SessionView() => OnSta(() =>
    {
        var store = new DeveloperLogStore();
        var clock = new SessionClock();
        var recorder = new SessionLogRecorder(store, clock);
        recorder.Record(AppSessionTests.Event("Code", 0));
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var window = new DeveloperLogWindow(store)
            {
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
            };
            try
            {
                window.Show();
                DrainDispatcher();
                var label = (TextBlock)window.FindName("CurrentSessionText");
                Check(label.Text.Contains(cycle == 0 ? "Code" : "Chrome"), "Current session binding is stale");
            }
            finally { window.Close(); }
            if (cycle == 0)
            {
                clock.Advance(TimeSpan.FromSeconds(12));
                recorder.Record(AppSessionTests.Event("Chrome", 12));
            }
        }
        recorder.Complete(DateTimeOffset.UnixEpoch.AddSeconds(12));
        DrainDispatcher();
    });

    /// <summary>真实前台监测到会话适配器的联动检查；不切换用户窗口，也不生成输入。</summary>
    public static void NativeSessionPipeline() => OnSta(() =>
    {
        var store = new DeveloperLogStore();
        var recorder = new SessionLogRecorder(store);
        using var received = new ManualResetEventSlim();
        using var monitor = new ForegroundMonitor();
        monitor.ForegroundChanged += (_, transition) =>
        {
            recorder.Record(transition);
            received.Set();
        };
        try
        {
            monitor.Start();
            Check(received.Wait(TimeSpan.FromSeconds(3)), "No native foreground event received");
        }
        finally { monitor.Stop(); }
        var final = recorder.Complete(DateTimeOffset.Now);
        Check(final is not null && final.Duration >= TimeSpan.Zero, "Native session was not finalized");
        DrainDispatcher();
        Check(store.Entries.Any(entry => entry.Contains("SessionEnded:")), "Native session did not reach the log");
        Check(store.CurrentSessionSummary.Contains("监测已结束"), "Native pipeline did not stop");
    });

    /// <summary>会话适配器与真实后台数据库联动，退出排空后新仓储能读到会话和空闲事件。</summary>
    public static void PersistedSessionPipeline() => OnSta(() =>
    {
        using var files = new MemoryTests.TestDatabase();
        using var writer = new ActivityPersistence(files.Store);
        var store = new DeveloperLogStore();
        var clock = new SessionClock();
        var recorder = new SessionLogRecorder(store, clock, writer);
        ForegroundChangedEvent Focus(string name, int seconds)
        {
            var time = MemoryTests.Start.AddSeconds(seconds);
            return new(null, new(1, name, name, time), time);
        }
        writer.Start();
        Task.Run(() =>
        {
            recorder.Record(Focus("Code", 0));
            clock.Advance(TimeSpan.FromMinutes(8));
            recorder.Record(Focus("Chrome", 480));
            writer.TryRecord(new UserIdleEvent(MemoryTests.Start, MemoryTests.Start.AddMinutes(5)));
            writer.TryRecord(new UserReturnedEvent(MemoryTests.Start.AddMinutes(6), TimeSpan.FromMinutes(6), MemoryTests.Start.AddMinutes(6)));
            clock.Advance(TimeSpan.FromMinutes(40));
            recorder.Complete(MemoryTests.Start.AddMinutes(48));
            Check(writer.Shutdown(TimeSpan.FromSeconds(5)), "Final session was not drained");
        }).GetAwaiter().GetResult();
        var reopened = new SqliteActivityStore(files.Path);
        var history = reopened.ReadDay(MemoryTests.Day, MemoryTests.Zone);
        Check(history.SessionCount == 2, "Restart lost session history");
        Check(history.Totals.Single(x => x.ProcessName == "Code").TotalForegroundSeconds == 480, "Code total wrong");
        Check(history.Totals.Single(x => x.ProcessName == "Chrome").TotalForegroundSeconds == 2400, "Chrome total wrong");
        Check(reopened.ReadEvents(MemoryTests.Day, MemoryTests.Zone).Count == 4, "Monitor events not persisted");
        DrainDispatcher();
    });

    /// <summary>真实历史页验证异步绑定、分页、切换日期和关窗取消，不依赖手工点击。</summary>
    public static void PersistedHistoryView() => OnSta(() =>
    {
        using var files = new MemoryTests.TestDatabase();
        files.Store.Initialize();
        files.Store.WriteBatch(Enumerable.Range(0, 105).Select(i => new ActivityWrite(null,
            MemoryTests.Session("Code", 1, MemoryTests.Start.AddSeconds(i)))).ToArray());
        var window = new DeveloperLogWindow(new DeveloperLogStore(), new HistoryQueryService(files.Store))
        {
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        try
        {
            window.Show();
            var date = (DatePicker)window.FindName("HistoryDate");
            var tab = (TabItem)window.FindName("HistoryTab");
            var rows = (DataGrid)window.FindName("SessionHistoryGrid");
            var totals = (DataGrid)window.FindName("DailyTotalsGrid");
            var refresh = (Button)window.FindName("RefreshHistoryButton");
            date.SelectedDate = MemoryTests.Day.ToDateTime(TimeOnly.MinValue);
            tab.IsSelected = true;
            PumpUntil(() => refresh.IsEnabled && rows.Items.Count == 100);
            Check(((DailyAppTotal)totals.Items[0]).TotalForegroundSeconds == 105, "UI total truncated to page");
            ((Button)window.FindName("NextHistoryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => refresh.IsEnabled && rows.Items.Count == 5);
            date.SelectedDate = MemoryTests.Day.AddDays(1).ToDateTime(TimeOnly.MinValue);
            PumpUntil(() => refresh.IsEnabled && rows.Items.Count == 0);
            // 连续切换日期使旧任务作废；最终页面不能被慢查询覆盖。
            date.SelectedDate = MemoryTests.Day.AddDays(2).ToDateTime(TimeOnly.MinValue);
            date.SelectedDate = MemoryTests.Day.ToDateTime(TimeOnly.MinValue);
            PumpUntil(() => refresh.IsEnabled && rows.Items.Count == 100);
            // 查询尚在运行时关闭；夹具清理允许原生只读调用短暂收尾，不把取消等同于强行中断。
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        catch (Exception exception)
        {
            var status = ((TextBlock)window.FindName("HistoryStatus")).Text;
            var count = ((DataGrid)window.FindName("SessionHistoryGrid")).Items.Count;
            throw new InvalidOperationException($"{exception.Message}; rows={count}; status={status}", exception);
        }
        finally { window.Close(); }
        // 泵送关闭和绑定消息；IsLoaded 只表示视图状态，不代表后台任务已经完成。
        PumpUntil(() => !window.IsLoaded);
        DrainDispatcher();
    });

    /// <summary>后台事件经规则进入真实候选页和 SQLite；关窗重开、200 条上限与停止后的晚到事件均被覆盖。</summary>
    public static void PatternPipelineAndView() => OnSta(() =>
    {
        using var files = new MemoryTests.TestDatabase();
        using var writer = new ActivityPersistence(files.Store);
        writer.Start();
        var log = new DeveloperLogStore();
        var clock = new SessionClock();
        using var recorder = new PatternLogRecorder(log, writer,
            new() { LongSession = TimeSpan.FromSeconds(1) }, clock);
        Task.Run(() =>
        {
            recorder.Record(new ForegroundChangedEvent(null, new(1, "Code", "Code", MemoryTests.Start), MemoryTests.Start));
            clock.Advance(TimeSpan.FromSeconds(1));
            recorder.Tick(); recorder.Tick();
            recorder.Dispose();
            // 停止后即使再到达一个足够长的返回事件，也不能输出新候选。
            recorder.Record(new UserReturnedEvent(MemoryTests.Start, TimeSpan.FromHours(2), MemoryTests.Start));
        }).GetAwaiter().GetResult();
        Check(writer.Shutdown(TimeSpan.FromSeconds(5)), "Pattern write queue did not drain");
        Check(files.Store.ReadPatterns(MemoryTests.Day, MemoryTests.Zone).Single().Type == PatternType.LongSession, "Pipeline did not persist pattern");
        DrainDispatcher();
        Check(log.Patterns.Count == 1, "Tick/late callback duplicated candidate");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var window = new DeveloperLogWindow(log)
            {
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
            };
            try
            {
                window.Show();
                ((TabItem)window.FindName("PatternsTab")).IsSelected = true;
                DrainDispatcher();
                var list = (DataGrid)window.FindName("PatternList");
                Check(list.Items.Count == log.Patterns.Count, "Candidate view lost records on reopen");
                list.SelectedIndex = 0;
                DrainDispatcher();
                Check(((TextBox)window.FindName("PatternEvidence")).Text.Contains("DurationSeconds"), "Selected evidence not bound");
                if (cycle == 0)
                {
                    var pattern = log.Patterns.Single().Pattern;
                    Task.Run(() =>
                    {
                        for (var i = 0; i < 205; i++) log.AppendPattern(pattern with { Id = Guid.NewGuid() });
                    }).GetAwaiter().GetResult();
                    DrainDispatcher();
                    Check(log.Patterns.Count == 200 && list.Items.Count == 200, "Candidate view bound exceeded");
                }
            }
            finally { window.Close(); }
        }
        Check(!log.Entries.Any(x => x.StartsWith("[Error]")), "Pattern pipeline logged an error");
        DrainDispatcher();
    });

    /// <summary>真实事件经模式检测与评分得到一条过滤、一条通过；核对界面、日志和 SQLite 一致。</summary>
    public static void ScoringPipelineAndView() => OnSta(() =>
    {
        using var files = new MemoryTests.TestDatabase();
        using var writer = new ActivityPersistence(files.Store);
        writer.Start();
        var log = new DeveloperLogStore();
        var clock = new SessionClock();
        using var recorder = new PatternLogRecorder(log, writer, clock: clock);
        Task.Run(() =>
        {
            recorder.Record(new ForegroundChangedEvent(null, new(1, "Code", "Code", MemoryTests.Start), MemoryTests.Start));
            recorder.Record(new AppClosedEvent("git", MemoryTests.Start));
            clock.Advance(TimeSpan.FromSeconds(55));
            recorder.Record(new AppStartedEvent("git", MemoryTests.Start.AddSeconds(55)));
            clock.Advance(TimeSpan.FromSeconds(5345));
            recorder.Tick();
            recorder.Dispose();
        }).GetAwaiter().GetResult();
        Check(writer.Shutdown(TimeSpan.FromSeconds(5)), "Scoring pipeline did not drain");
        var stored = files.Store.ReadEvaluations(MemoryTests.Day, MemoryTests.Zone);
        Check(stored.Count == 2 && stored.Count(x => x.IsSelected) == 1, "Scoring not integrated into persistence");
        DrainDispatcher();
        Check(log.Patterns.Count == 2 && log.EvaluationSummary.Contains("通过初筛 1 个") && log.EvaluationSummary.Contains("过滤 1 个"), "Counters do not distinguish selection/filtering");
        var window = new DeveloperLogWindow(log)
        {
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        try
        {
            window.Show();
            ((TabItem)window.FindName("PatternsTab")).IsSelected = true;
            DrainDispatcher();
            var rows = (DataGrid)window.FindName("PatternList");
            var rejected = log.Patterns.Single(x => x.Pattern.ProcessName == "git");
            var selected = log.Patterns.Single(x => x.Pattern.ProcessName == "Code");
            Check(rejected.DecisionText == "已过滤" && selected.DecisionText == "通过初筛（未发送）", "UI mislabels decisions as narration");
            Check(selected.Evaluation?.Score == 83.5, "UI score differs from rule calculation");
            rows.SelectedItem = rejected;
            ((TabItem)window.FindName("ScoreDetailsTab")).IsSelected = true;
            DrainDispatcher();
            var detail = ((TextBox)window.FindName("PatternEvaluationDetail")).Text;
            Check(detail.Contains("后台辅助进程") && detail.Contains("RecencyPenalty") && detail.Contains("PersonalBaselineAvailable"), "Decision reason/breakdown/context not bound");
            Check(log.Entries.Any(x => x.StartsWith("[Interestingness]")) && !log.Entries.Any(x => x.StartsWith("[Error]")), "Structured scoring log missing or pipeline failed");
        }
        finally { window.Close(); }
        DrainDispatcher();
    });

    /// <summary>泵送 UI 消息直到异步条件满足，避免在 STA 上同步等待查询造成死锁。</summary>
    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || watch.Elapsed > TimeSpan.FromSeconds(8)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(condition(), "Asynchronous WPF history query timed out");
    }

    /// <summary>用 TimeSpan 的 tick 作为可控计时单位，避免真实 Sleep 和平台计时频率差异。</summary>
    private sealed class SessionClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => MemoryTests.Start.ToUniversalTime() + TimeSpan.FromTicks(GetTimestamp());
        public override TimeZoneInfo LocalTimeZone => MemoryTests.Zone;
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    /// <summary>处理到指定优先级的队列标记为止，让测试有机会观察异步绑定与滚动结果。</summary>
    private static void DrainDispatcher(DispatcherPriority priority = DispatcherPriority.ApplicationIdle)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>每项 WPF 测试独占 STA 线程和 Dispatcher；捕获异常后仍将测试判为失败。</summary>
    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            // 与真实 WPF 消息循环一致，确保 async 事件在 await 后回到该 STA 线程。
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            // 这里只为测试收集异常并完成清理，不是生产环境的全局吞异常策略。
            dispatcher.UnhandledException += (_, e) => { failure ??= e.Exception; e.Handled = true; };
            try { test(); }
            catch (Exception exception) { failure ??= exception; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(15)), "WPF regression test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
