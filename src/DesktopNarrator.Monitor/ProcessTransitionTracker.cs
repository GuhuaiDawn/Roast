using DesktopNarrator.Events;

namespace DesktopNarrator.Monitor;

/// <summary>比较相邻完整快照的同名进程组；首次快照和长采样缺口仅建立基线，不虚构启动/退出。</summary>
public sealed class ProcessTransitionTracker
{
    private HashSet<string>? _previous;
    private TimeSpan _lastAt;
    private readonly TimeSpan _maximumGap;

    public ProcessTransitionTracker(TimeSpan? maximumGap = null)
    {
        _maximumGap = maximumGap ?? TimeSpan.FromSeconds(15);
        if (_maximumGap <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumGap));
    }

    /// <summary>名称大小写不敏感；多个同名进程仍只表示一个在运行的应用组。</summary>
    public IReadOnlyList<IMonitorEvent> Observe(IReadOnlyList<string> names, DateTimeOffset timestamp, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (elapsed < _lastAt || elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        foreach (var name in names) ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var current = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var result = new List<IMonitorEvent>();
        if (_previous is null || elapsed - _lastAt > _maximumGap)
            result.Add(new ProcessObservationResetEvent(timestamp));
        else
        {
            // 稳定输出顺序便于重放；这里只表示两次快照间观察到的变化，不能确定真实发生顺序。
            foreach (var name in _previous.Except(current, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                result.Add(new AppClosedEvent(name, timestamp));
            foreach (var name in current.Except(_previous, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                result.Add(new AppStartedEvent(name, timestamp));
        }
        _previous = current;
        _lastAt = elapsed;
        return result;
    }

    /// <summary>失败后下次成功快照只重建基线；不将不可观测期间的变化计入统计。</summary>
    public void Reset() => _previous = null;
}
