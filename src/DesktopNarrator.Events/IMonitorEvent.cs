namespace DesktopNarrator.Events;

/// <summary>监测事件的公共时间接口；事件携带事实，不依赖 WPF 或具体展示方式。</summary>
public interface IMonitorEvent
{
    /// <summary>事件的观察时间；具体行为的估计发生时间由事件自身的字段表达。</summary>
    DateTimeOffset Timestamp { get; }
}
