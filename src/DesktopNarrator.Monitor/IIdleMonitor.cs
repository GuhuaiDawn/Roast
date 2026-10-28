using DesktopNarrator.Events;

namespace DesktopNarrator.Monitor;

/// <summary>空闲监测契约；仅输出状态转换，不输出每次轮询结果或输入内容。</summary>
public interface IIdleMonitor : IDisposable
{
    /// <summary>达到空闲阈值时在监测线程触发，持续空闲不重复触发。</summary>
    event EventHandler<UserIdleEvent>? UserIdle;
    /// <summary>空闲后观察到新的输入时在监测线程触发。</summary>
    event EventHandler<UserReturnedEvent>? UserReturned;
    /// <summary>采样或回调异常的诊断通道；订阅方不应阻塞或修改监测状态。</summary>
    event EventHandler<MonitorErrorEventArgs>? Error;
    /// <summary>监测线程存在且尚未收到停止请求。</summary>
    bool IsRunning { get; }
    /// <summary>开始新一轮监测；已在运行时重复调用不产生额外线程。</summary>
    void Start();
    /// <summary>请求停止并等待线程结束；在自身事件回调中调用时不等待自身。</summary>
    void Stop();
}
