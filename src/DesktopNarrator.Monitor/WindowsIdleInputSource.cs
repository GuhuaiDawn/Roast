using System.Runtime.InteropServices;

namespace DesktopNarrator.Monitor;

/// <summary>读取当前 Windows 会话的最后输入时间；不安装键鼠钩子，也不获取输入内容。</summary>
public sealed class WindowsIdleInputSource : IIdleInputSource
{
    /// <summary>同时提供日期时间与运行毫秒数，供状态机分别处理展示时间和持续时长。</summary>
    public IdleInputSample Read()
    {
        // Win32 结构要求调用方填写大小；字段顺序和宽度必须与 LASTINPUTINFO 一致。
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref input))
        {
            // 此 API 未承诺通过 GetLastError 提供错误码，不读取可能属于其他调用的旧错误。
            throw new InvalidOperationException("GetLastInputInfo failed for the current Windows session.");
        }

        return new IdleInputSample(DateTimeOffset.Now, Environment.TickCount64, input.Time);
    }

    // Time 是 32 位计数，回绕处理放在可测试的 IdleTransitionTracker 中。
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);
}
