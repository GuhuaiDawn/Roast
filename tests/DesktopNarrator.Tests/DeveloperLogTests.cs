using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopNarrator.App;
using DesktopNarrator.Monitor;

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

    /// <summary>用 TimeSpan 的 tick 作为可控计时单位，避免真实 Sleep 和平台计时频率差异。</summary>
    private sealed class SessionClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
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
