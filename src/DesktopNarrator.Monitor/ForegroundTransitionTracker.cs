using DesktopNarrator.Events;

namespace DesktopNarrator.Monitor;

/// <summary>
/// 将应用快照转换为应用级切换事件；保留少量状态，不调用 Win32，便于独立测试。
/// 按单线程顺序调用，内部不提供并发保护。
/// </summary>
public sealed class ForegroundTransitionTracker
{
    private ForegroundApplication? _lastApplication;

    /// <summary>首次观察或进程名称发生变化时返回 true，并输出包含前后快照的事件。</summary>
    public bool TryCreateTransition(
        ForegroundApplication currentApplication,
        out ForegroundChangedEvent? transition)
    {
        ArgumentNullException.ThrowIfNull(currentApplication);

        // 同一应用可能有多个 PID，例如浏览器多进程；只按名称判断应用是否切换。
        if (_lastApplication is not null &&
            string.Equals(
                _lastApplication.ProcessName,
                currentApplication.ProcessName,
                StringComparison.OrdinalIgnoreCase))
        {
            // 即使不发事件，也刷新快照，避免下一次切换携带过时的进程信息。
            _lastApplication = currentApplication;
            transition = null;
            return false;
        }

        transition = new ForegroundChangedEvent(
            _lastApplication,
            currentApplication,
            currentApplication.ObservedAt);
        _lastApplication = currentApplication;
        return true;
    }

    /// <summary>最近观察到的应用；尚未采样或重置后为 null。</summary>
    public ForegroundApplication? CurrentApplication => _lastApplication;

    /// <summary>清除前次观察，使下一次采样被视为初始状态。</summary>
    public void Reset() => _lastApplication = null;
}
