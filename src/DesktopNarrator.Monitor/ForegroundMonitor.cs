using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopNarrator.Events;

namespace DesktopNarrator.Monitor;

/// <summary>
/// 在专用线程的 Win32 消息循环中监听 EVENT_SYSTEM_FOREGROUND，再输出应用级事件。
/// 不依赖 WPF，不高频轮询，不读取窗口标题或内容；UI 订阅方自行进行线程调度。
/// </summary>
public sealed class ForegroundMonitor : IForegroundMonitor
{
    private const uint EventSystemForeground = 0x0003;
    private const uint PeekMessageNoRemove = 0x0000;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WmQuit = 0x0012;

    private readonly object _sync = new();
    // 必须持有委托引用，保证原生钩子存活期间回调不会被 GC 回收。
    private readonly WinEventDelegate _winEventDelegate;
    private readonly ForegroundTransitionTracker _transitionTracker = new();
    private Thread? _monitorThread;
    private ManualResetEventSlim? _startupCompleted;
    private uint _monitorThreadId;
    private IntPtr _hookHandle;
    private bool _isRunning;
    private bool _disposed;

    public ForegroundMonitor()
    {
        _winEventDelegate = OnWinEvent;
    }

    public event EventHandler<ForegroundChangedEvent>? ForegroundChanged;

    public event EventHandler<MonitorErrorEventArgs>? Error;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _isRunning;
            }
        }
    }

    /// <summary>创建后台消息线程，最多等待 5 秒启动；超时通过 Error 报告，不代表钩子已成功。</summary>
    public void Start()
    {
        ManualResetEventSlim startupCompleted;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning || _monitorThread is not null)
            {
                return;
            }

            startupCompleted = new ManualResetEventSlim(false);
            _startupCompleted = startupCompleted;
            _monitorThread = new Thread(MonitorThreadMain)
            {
                IsBackground = true,
                Name = "DesktopNarrator.ForegroundMonitor"
            };
            _monitorThread.Start();
        }

        if (!startupCompleted.Wait(TimeSpan.FromSeconds(5)))
        {
            RaiseError("Foreground monitor startup", new TimeoutException("WinEventHook startup timed out."));
        }
    }

    /// <summary>向消息线程投递 WM_QUIT；非监测线程调用时最多等待 3 秒，不强杀线程。</summary>
    public void Stop()
    {
        Thread? thread;
        uint threadId;

        lock (_sync)
        {
            if (!_isRunning && _monitorThread is null)
            {
                return;
            }

            thread = _monitorThread;
            threadId = _monitorThreadId;
        }

        if (threadId != 0)
        {
            try
            {
                if (!PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero))
                {
                    RaiseError("PostThreadMessage", new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
            catch (Exception exception)
            {
                RaiseError("PostThreadMessage", exception);
            }
        }

        // 回调可能间接请求停止；不能让线程等待自己退出。
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>请求停止并释放启动同步对象；原生钩子由消息线程的 finally 解除。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        _startupCompleted?.Dispose();
    }

    private void MonitorThreadMain()
    {
        _monitorThreadId = GetCurrentThreadId();

        try
        {
            // 先创建线程消息队列，后续停止操作才能通过 PostThreadMessage 投递退出消息。
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PeekMessageNoRemove);

            // OUTOFCONTEXT 在本进程接收事件，不向被观察应用注入 DLL。
            _hookHandle = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WineventOutOfContext);

            if (_hookHandle == IntPtr.Zero)
            {
                RaiseError("SetWinEventHook", new Win32Exception(Marshal.GetLastWin32Error()));
                return;
            }

            lock (_sync)
            {
                _isRunning = true;
            }

            _startupCompleted?.Set();

            // 启动即补一份当前应用快照，不要求用户先切换窗口才能获得初始状态。
            EmitCurrentForegroundWindow(GetForegroundWindow());

            while (true)
            {
                // GetMessage 阻塞等待消息，不进行忙循环；-1 为失败，0 表示收到 WM_QUIT。
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == -1)
                {
                    RaiseError("GetMessage", new Win32Exception(Marshal.GetLastWin32Error()));
                    break;
                }

                if (result == 0)
                {
                    break;
                }

                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            RaiseError("Foreground monitor loop", exception);
        }
        finally
        {
            // 无论正常退出还是异常，都由安装钩子的线程负责解除钩子并清理运行状态。
            if (_hookHandle != IntPtr.Zero)
            {
                try
                {
                    _ = UnhookWinEvent(_hookHandle);
                }
                catch (Exception exception)
                {
                    RaiseError("UnhookWinEvent", exception);
                }

                _hookHandle = IntPtr.Zero;
            }

            lock (_sync)
            {
                _isRunning = false;
                _monitorThreadId = 0;
                _monitorThread = null;
            }

            _startupCompleted?.Set();
        }
    }

    /// <summary>原生回调入口：过滤非前台事件和无效句柄，再交给带异常保护的读取逻辑。</summary>
    private void OnWinEvent(
        IntPtr hookHandle,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime)
    {
        if (eventType != EventSystemForeground || windowHandle == IntPtr.Zero)
        {
            return;
        }

        EmitCurrentForegroundWindow(windowHandle);
    }

    /// <summary>从 HWND 解析 PID 和应用描述，去重后发布事件；进程随时可能退出或拒绝访问。</summary>
    private void EmitCurrentForegroundWindow(IntPtr windowHandle)
    {
        try
        {
            var foregroundWindow = windowHandle != IntPtr.Zero
                ? windowHandle
                : GetForegroundWindow();

            if (foregroundWindow == IntPtr.Zero)
            {
                return;
            }

            _ = GetWindowThreadProcessId(foregroundWindow, out var processId);
            if (processId == 0)
            {
                return;
            }

            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            var friendlyName = GetFriendlyName(process, processName);
            var application = new ForegroundApplication(
                processId,
                processName,
                friendlyName,
                DateTimeOffset.Now);

            if (_transitionTracker.TryCreateTransition(application, out var transition) &&
                transition is not null)
            {
                try
                {
                    ForegroundChanged?.Invoke(this, transition);
                }
                catch (Exception exception)
                {
                    // 隔离订阅方异常，避免其穿过原生回调边界、终止监测循环。
                    RaiseError("ForegroundChanged subscriber", exception);
                }
            }
        }
        catch (ArgumentException)
        {
            // 查询 PID 和获取 Process 对象之间，目标进程可能已经退出，忽略本次快照。
        }
        catch (Win32Exception exception)
        {
            RaiseError("Read foreground process", exception);
        }
        catch (Exception exception)
        {
            RaiseError("Read foreground window", exception);
        }
    }

    /// <summary>可选的可执行文件描述；权限不足或进程退出时退回进程名，不影响基本识别。</summary>
    private string GetFriendlyName(Process process, string fallback)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo?.FileDescription;
            return string.IsNullOrWhiteSpace(description) ? fallback : description.Trim();
        }
        catch (Win32Exception exception)
        {
            RaiseError("Read process friendly name", exception);
            return fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private void RaiseError(string operation, Exception exception)
    {
        try
        {
            Error?.Invoke(this, new MonitorErrorEventArgs(operation, exception));
        }
        catch
        {
            // 错误报告也可能失败，不能让诊断通道再次中断监测。
        }
    }

    private delegate void WinEventDelegate(
        IntPtr hookHandle,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime);

    // 原生 MSG/POINT 布局；指针字段使用 IntPtr，避免在 64 位进程中被截断。
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr HWnd;
        public uint MessageId;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    // Win32 互操作集中在 Monitor 层；上层只消费事件，不直接调用这些 API。
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr moduleHandle,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hookHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out Message message,
        IntPtr windowHandle,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out Message message,
        IntPtr windowHandle,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
