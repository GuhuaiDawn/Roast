namespace DesktopNarrator.Events;

/// <summary>
/// 将有序的前台事件聚合成会话，不依赖 Win32、WPF、数据库或真实时间流逝。
/// 单线程调用；时间由调用方提供，模拟时间线可直接重放，无需实际等待数分钟。
/// </summary>
public sealed class AppSessionBuilder
{
    private TimeSpan? _lastObservedAt;
    private TimeSpan _startedAt;

    /// <summary>当前尚未结束的会话；尚未观察到应用或已经收尾时为 null。</summary>
    public AppSession? CurrentSession { get; private set; }

    /// <summary>消费前台事件；切换应用时返回刚结束的会话，同时建立下一段会话。</summary>
    /// <param name="transition">本次观察的前台应用；不根据 PreviousApplication 虚构此前缺失的会话。</param>
    /// <param name="observedAt">从固定基点开始的非递减计时值，必须与 Complete 使用同一基点。</param>
    /// <returns>首次观察、同名重复事件返回 null；应用切换时返回结束后的旧会话。</returns>
    public AppSession? Process(ForegroundChangedEvent transition, TimeSpan observedAt)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(transition.CurrentApplication);
        var application = transition.CurrentApplication;
        ArgumentException.ThrowIfNullOrWhiteSpace(application.ProcessName);
        ValidateTime(observedAt);

        // 完成全部校验后才改变状态，错误输入不能重置现有会话的起点。
        _lastObservedAt = observedAt;
        var friendlyName = string.IsNullOrWhiteSpace(application.FriendlyName)
            ? application.ProcessName : application.FriendlyName;
        if (CurrentSession is { } active &&
            string.Equals(active.ProcessName, application.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            // 浏览器可能切换到另一个 PID；更新描述，但不改变会话 ID 或开始时间。
            CurrentSession = active with { FriendlyName = friendlyName };
            return null;
        }

        var completed = CurrentSession is { } previous
            ? previous with { EndTime = transition.Timestamp, Duration = observedAt - _startedAt } : null;
        CurrentSession = new AppSession(Guid.NewGuid(), application.ProcessName, friendlyName, transition.Timestamp);
        _startedAt = observedAt;
        return completed;
    }

    /// <summary>显式结束当前会话；没有活动会话时返回 null，重复收尾不会生成重复结果。</summary>
    /// <param name="endedAt">展示用结束时间；系统调时后可能早于开始时间，不用于计算 Duration。</param>
    /// <param name="observedAt">与 Process 相同基点的非递减计时值。</param>
    public AppSession? Complete(DateTimeOffset endedAt, TimeSpan observedAt)
    {
        if (CurrentSession is not { } active) return null;
        ValidateTime(observedAt);
        var completed = active with { EndTime = endedAt, Duration = observedAt - _startedAt };
        _lastObservedAt = observedAt;
        CurrentSession = null;
        return completed;
    }

    /// <summary>允许同一计时刻的快速切换，但拒绝负值和乱序计时，避免负时长。</summary>
    private void ValidateTime(TimeSpan observedAt)
    {
        if (observedAt < TimeSpan.Zero || (_lastObservedAt is { } last && observedAt < last))
            throw new ArgumentOutOfRangeException(nameof(observedAt), "Session observations must use nondecreasing elapsed time.");
    }
}
