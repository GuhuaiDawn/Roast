using System.Diagnostics;
using System.IO;
using DesktopNarrator.Events;
using DesktopNarrator.Monitor;

/// <summary>只观察进程名与存在状态；真实子进程由测试创建并关闭，不操作用户进程。</summary>
internal static class ProcessTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    public static void BaselineAndGroups()
    {
        var tracker = new ProcessTransitionTracker();
        Check(tracker.Observe(["chrome", "CHROME"], Start, TimeSpan.Zero).Single() is ProcessObservationResetEvent, "Startup synthesized starts");
        Check(tracker.Observe(["Chrome"], Start.AddSeconds(5), TimeSpan.FromSeconds(5)).Count == 0, "Child exit became app close");
        Check(tracker.Observe([], Start.AddSeconds(10), TimeSpan.FromSeconds(10)).Single() is AppClosedEvent, "Last process exit missing");
        Check(tracker.Observe(["chrome", "chrome"], Start.AddSeconds(15), TimeSpan.FromSeconds(15)).Single() is AppStartedEvent, "Group start missing or duplicated");
    }

    public static void GapsAndReset()
    {
        var tracker = new ProcessTransitionTracker();
        tracker.Observe(["A"], Start, TimeSpan.Zero);
        Check(tracker.Observe(["B"], Start.AddHours(1), TimeSpan.FromHours(1)).Single() is ProcessObservationResetEvent, "Sleep gap synthesized changes");
        tracker.Reset();
        Check(tracker.Observe(["C"], Start.AddHours(1), TimeSpan.FromHours(1)).Single() is ProcessObservationResetEvent, "Read failure synthesized changes");
    }

    public static void RecoveryAndSubscriberIsolation()
    {
        using var source = new ScriptedSource();
        using var received = new ManualResetEventSlim();
        using var monitor = new ProcessMonitor(source, TimeSpan.FromMilliseconds(10));
        var errors = 0;
        var changes = new List<IMonitorEvent>();
        monitor.Error += (_, _) => Interlocked.Increment(ref errors);
        monitor.Changed += (_, _) => throw new InvalidOperationException("consumer failure");
        monitor.Changed += (_, value) =>
        {
            lock (changes) changes.Add(value);
            if (value is AppStartedEvent) { monitor.Stop(); received.Set(); }
        };
        monitor.Start(); monitor.Start();
        Check(received.Wait(TimeSpan.FromSeconds(3)), "Monitor did not recover");
        monitor.Stop();
        Check(!monitor.IsRunning && errors >= 2, "Lifecycle/subscriber errors hidden");
        lock (changes)
        {
            Check(!changes.OfType<AppClosedEvent>().Any(), "Failure invented an app close");
            Check(changes.OfType<AppStartedEvent>().Single().ProcessName == "C", "Recovery did not rebaseline");
        }
        // 重启重新基线，不把停机期间的进程变化当作观察事实。
        monitor.Start();
        Check(source.Restarted.Wait(TimeSpan.FromSeconds(2)), "Restart did not read");
        monitor.Stop();
        monitor.Dispose();
        try { monitor.Start(); throw new InvalidOperationException("Disposed monitor restarted"); } catch (ObjectDisposedException) { }
    }

    public static void NativeSnapshot()
    {
        using var current = Process.GetCurrentProcess();
        Check(new WindowsProcessSnapshotSource().Read().Contains(current.ProcessName, StringComparer.OrdinalIgnoreCase), "Current process missing");
    }

    /// <summary>复制自身 apphost 为唯一名称，隐藏启动受控子进程，不依赖用户是否已经打开某应用。</summary>
    public static void NativeLifecycle()
    {
        var original = Path.Combine(AppContext.BaseDirectory, "DesktopNarrator.Tests.exe");
        var name = "NarratorFixture-" + Guid.NewGuid().ToString("N");
        var helper = Path.Combine(AppContext.BaseDirectory, name + ".exe");
        File.Copy(original, helper, overwrite: false);
        Process? child = null;
        using var ready = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var closed = new ManualResetEventSlim();
        using var monitor = new ProcessMonitor(interval: TimeSpan.FromMilliseconds(200));
        monitor.Changed += (_, value) =>
        {
            if (value is ProcessObservationResetEvent) ready.Set();
            if (value is AppStartedEvent start && start.ProcessName == name) started.Set();
            if (value is AppClosedEvent stop && stop.ProcessName == name) closed.Set();
        };
        try
        {
            monitor.Start();
            Check(ready.Wait(TimeSpan.FromSeconds(3)), "Native snapshot baseline failed");
            child = Process.Start(new ProcessStartInfo(helper, "--process-fixture")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
            })!;
            Check(started.Wait(TimeSpan.FromSeconds(5)), "Real process start not observed");
            child.StandardInput.WriteLine("exit");
            Check(child.WaitForExit(3000) && child.ExitCode == 0, "Controlled child did not exit normally");
            Check(closed.Wait(TimeSpan.FromSeconds(5)), "Real process exit not observed");
        }
        finally
        {
            monitor.Stop();
            if (child is not null)
            {
                // 仅兜底结束本测试通过 Process.Start 创建、仍由句柄引用的子进程。
                if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); }
                child.Dispose();
            }
            File.Delete(helper); // 仅删除本测试以随机名称创建的单个 apphost 副本。
        }
    }

    private sealed class ScriptedSource : IProcessSnapshotSource, IDisposable
    {
        private int _reads;
        public ManualResetEventSlim Restarted { get; } = new();
        public IReadOnlyList<string> Read() => Interlocked.Increment(ref _reads) switch
        {
            1 => ["A"],
            2 => throw new IOException("simulated incomplete snapshot"),
            3 => ["B"],
            4 => ["B", "C"],
            _ => Restart()
        };
        private string[] Restart() { Restarted.Set(); return ["B", "C"]; }
        public void Dispose() => Restarted.Dispose();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
