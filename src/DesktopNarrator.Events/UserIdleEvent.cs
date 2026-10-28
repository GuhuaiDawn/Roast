namespace DesktopNarrator.Events;

/// <summary>无输入时长达到阈值时发出一次；“空闲”不等于确认用户已经离开。</summary>
/// <param name="IdleStartedAt">估计的最后输入时间，包含达到阈值之前的无输入时段。</param>
/// <param name="DetectedAt">采样发现达到空闲阈值的时间。</param>
public sealed record UserIdleEvent(DateTimeOffset IdleStartedAt, DateTimeOffset DetectedAt) : IMonitorEvent
{
    /// <summary>统一事件时间使用检测时间，避免和空闲开始时间混淆。</summary>
    public DateTimeOffset Timestamp => DetectedAt;
}
