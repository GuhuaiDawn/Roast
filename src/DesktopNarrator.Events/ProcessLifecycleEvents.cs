namespace DesktopNarrator.Events;

/// <summary>当前 Windows 会话中，同名进程组从不存在变为存在；时间为快照发现时间，不是精确启动时间。</summary>
public sealed record AppStartedEvent(string ProcessName, DateTimeOffset Timestamp) : IMonitorEvent;

/// <summary>同名进程组从存在变为不存在；只有最后一个同名进程消失才发出，不等于关闭某个窗口。</summary>
public sealed record AppClosedEvent(string ProcessName, DateTimeOffset Timestamp) : IMonitorEvent;

/// <summary>进程观察有缺口或重新建立基线；消费者不得跨过缺口推断关闭/重开和连续启动次数。</summary>
public sealed record ProcessObservationResetEvent(DateTimeOffset Timestamp) : IMonitorEvent;
