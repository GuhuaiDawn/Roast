using System.Diagnostics;

namespace DesktopNarrator.Monitor;

/// <summary>只提供当前 Windows 会话的进程名快照；不能把部分读取结果当成完整列表。</summary>
public interface IProcessSnapshotSource
{
    /// <summary>允许同名多项；读取失败必须抛出异常，由上层重新建立基线。</summary>
    IReadOnlyList<string> Read();
}

/// <summary>低敏感度进程枚举：不读命令行、路径、窗口内容、模块或进程内存，不需要安装 WMI 订阅。</summary>
public sealed class WindowsProcessSnapshotSource : IProcessSnapshotSource
{
    /// <inheritdoc />
    public IReadOnlyList<string> Read()
    {
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        var processes = Process.GetProcesses();
        try
        {
            var names = new List<string>();
            foreach (var process in processes)
                if (process.SessionId == session) names.Add(process.ProcessName);
            return names;
        }
        finally
        {
            // 一项读取失败也释放整批 Process 包装对象；不把失败项当成“已经关闭”。
            foreach (var process in processes) process.Dispose();
        }
    }
}
