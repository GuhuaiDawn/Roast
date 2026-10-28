using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopNarrator.App;

/// <summary>封装 Windows Shell 托盘图标；将窗口消息转换为菜单与双击事件。</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const int WmApp = 0x8000;
    private const uint WmLButtonDblClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const int Id = 1;
    // 使用应用自定义消息区间接收 Shell 回调，不与标准窗口消息混用。
    private const int CallbackMessage = WmApp + 20;
    private const int IdiApplication = 32512;

    private readonly IntPtr _windowHandle;
    private readonly HwndSource? _hwndSource;
    private bool _disposed;

    /// <summary>将托盘图标关联到已创建的窗口；创建和释放都应由窗口所属 UI 线程负责。</summary>
    public TrayIcon(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _hwndSource = HwndSource.FromHwnd(windowHandle);
        _hwndSource?.AddHook(WindowMessageHook);

        var data = CreateData();
        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            throw new InvalidOperationException("Unable to create the system tray icon.");
        }
    }

    public event EventHandler? ContextMenuRequested;

    public event EventHandler? LeftDoubleClicked;

    /// <summary>先解除本地消息监听，再通知 Shell 删除图标；允许重复调用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hwndSource?.RemoveHook(WindowMessageHook);
        var data = CreateData();
        _ = Shell_NotifyIcon(NimDelete, ref data);
    }

    /// <summary>只处理本图标的回调消息；handled 告知 WPF 此消息已消费。</summary>
    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != CallbackMessage)
        {
            return IntPtr.Zero;
        }

        var notification = unchecked((uint)lParam.ToInt64());
        if (notification == WmRButtonUp)
        {
            ContextMenuRequested?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        else if (notification == WmLButtonDblClick)
        {
            LeftDoubleClicked?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    // 添加和删除都使用同一窗口句柄与图标 ID，供 Shell 定位本应用的托盘项。
    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = Id,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = CallbackMessage,
        // 加载系统共享图标，不拥有该句柄，因此不使用 DestroyIcon 销毁它。
        IconHandle = LoadIcon(IntPtr.Zero, (IntPtr)IdiApplication),
        Tip = "Desktop Narrator",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    // 字段顺序、字符集和固定字符串长度须匹配原生 NOTIFYICONDATA，不能随意调整。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
}
