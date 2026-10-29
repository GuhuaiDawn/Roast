namespace DesktopNarrator.Analysis;

/// <summary>第一版规则阈值；可由测试注入，不包含旁白冷却或趣味评分。</summary>
public sealed record PatternOptions
{
    /// <summary>前台驻留阈值，包含无输入时段；每段会话只在达到阈值时触发一次。</summary>
    public TimeSpan LongSession { get; init; } = TimeSpan.FromMinutes(90);
    /// <summary>快速切换统计的滑动窗口，左开右闭。</summary>
    public TimeSpan SwitchWindow { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>窗口内触发所需的切换次数，也是保留样本的容量上限。</summary>
    public int SwitchCount { get; init; } = 25;
    /// <summary>关闭到重新出现的最大观察间隔，包含等于边界的情况。</summary>
    public TimeSpan ReopenWindow { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>返回时所需的完整无输入时长；独立于 IdleMonitor 的进入空闲阈值。</summary>
    public TimeSpan LongIdle { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>本次连续监测、同一当地日期内的进程组启动次数阈值。</summary>
    public int DailyOpenCount { get; init; } = 6;

    /// <summary>拒绝会让窗口无界或永远触发的配置；验证必须在状态建立前完成。</summary>
    public void Validate()
    {
        if (LongSession <= TimeSpan.Zero || SwitchWindow <= TimeSpan.Zero || ReopenWindow <= TimeSpan.Zero ||
            LongIdle <= TimeSpan.Zero || SwitchCount is < 2 or > 1000 || DailyOpenCount is < 2 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(PatternOptions));
    }
}
