namespace DesktopNarrator.Events;

/// <summary>
/// 前台应用的低敏感度快照；不包含窗口标题、内容、文件路径或实际输入。
/// </summary>
/// <param name="ProcessId">采样时的进程 ID；仅用于标识该次观察，不作为应用级去重依据。</param>
/// <param name="ProcessName">进程名称；前台切换逻辑按此字段忽略大小写进行去重。</param>
/// <param name="FriendlyName">可执行文件描述；读取失败时退回进程名称。</param>
/// <param name="ObservedAt">采样时的本地时间（带时区偏移），用于显示与事件时间戳。</param>
public sealed record ForegroundApplication(
    uint ProcessId,
    string ProcessName,
    string FriendlyName,
    DateTimeOffset ObservedAt);
