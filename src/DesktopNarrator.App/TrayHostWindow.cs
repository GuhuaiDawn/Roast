using System.Windows;
using System.Windows.Interop;

namespace DesktopNarrator.App;

/// <summary>为托盘图标提供有效 HWND 和消息接收入口的隐藏 WPF 窗口，不是用户主界面。</summary>
internal sealed class TrayHostWindow : Window
{
    public TrayHostWindow()
    {
        Width = 1;
        Height = 1;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Opacity = 0;
        AllowsTransparency = true;
    }

    /// <summary>仅在 SourceInitialized 之后使用；创建 Window 对象本身不保证 HWND 已生成。</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
}
