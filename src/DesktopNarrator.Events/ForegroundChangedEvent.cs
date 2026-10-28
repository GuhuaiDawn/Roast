namespace DesktopNarrator.Events;

/// <summary>
/// 应用级前台切换事件；同名进程间的窗口切换不会重复产生此事件。
/// </summary>
/// <param name="PreviousApplication">上一次应用快照；首次观察时为 null。</param>
/// <param name="CurrentApplication">本次获得前台焦点的应用快照。</param>
/// <param name="Timestamp">本次快照的观察时间，不是应用启动时间。</param>
public sealed record ForegroundChangedEvent(
    ForegroundApplication? PreviousApplication,
    ForegroundApplication CurrentApplication,
    DateTimeOffset Timestamp) : IMonitorEvent;
