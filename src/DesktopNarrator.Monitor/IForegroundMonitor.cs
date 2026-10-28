using DesktopNarrator.Events;

namespace DesktopNarrator.Monitor;

/// <summary>前台应用监测的生命周期与事件契约；实现层不得依赖 WPF。</summary>
public interface IForegroundMonitor : IDisposable
{
    /// <summary>通常由监测线程触发；订阅方若更新界面，需要切换到 UI 线程。</summary>
    event EventHandler<ForegroundChangedEvent>? ForegroundChanged;

    /// <summary>监测或订阅回调中的诊断信息；订阅方应快速返回，避免阻塞监测。</summary>
    event EventHandler<MonitorErrorEventArgs>? Error;

    /// <summary>监测钩子是否已经成功启动。</summary>
    bool IsRunning { get; }

    /// <summary>开始监测；重复调用不创建第二个监测线程。</summary>
    void Start();

    /// <summary>请求停止监测；具体等待策略由实现决定。</summary>
    void Stop();
}
