namespace DesktopNarrator.Events;

/// <summary>已进入空闲状态后再次观察到输入时发出的事件。</summary>
/// <param name="ReturnedAt">本次采样中的最近输入时间估计；采样间隙内的首次输入可能更早。</param>
/// <param name="IdleDuration">完整无输入时长，包含阈值等待期，不包含返回后等待采样的延迟。</param>
/// <param name="DetectedAt">监测器实际发现返回的时间。</param>
public sealed record UserReturnedEvent(
    DateTimeOffset ReturnedAt, TimeSpan IdleDuration, DateTimeOffset DetectedAt) : IMonitorEvent
{
    /// <summary>用于事件排序和日志显示的检测时间。</summary>
    public DateTimeOffset Timestamp => DetectedAt;
}
