namespace DesktopNarrator.Analysis;

/// <summary>规则型初筛策略；时间均为单调计时间隔，不冒充“上一条旁白”的时间。</summary>
public sealed record InterestingnessOptions
{
    /// <summary>最终分数达到该值且无阻止原因，才通过初筛；不是概率。</summary>
    public double Threshold { get; init; } = 75;
    /// <summary>两次候选通过的最小间隔；未通过的候选不会延长它。</summary>
    public TimeSpan SelectionInterval { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>同类型、同进程的候选通过间隔，也作为候选重复证据的保留窗口。</summary>
    public TimeSpan TopicInterval { get; init; } = TimeSpan.FromHours(3);
    /// <summary>进程启动类候选至少需要在本次运行的这个窗口内观察到前台使用。</summary>
    public TimeSpan ForegroundEvidenceWindow { get; init; } = TimeSpan.FromHours(24);
    /// <summary>最近一次确认快速切换后暂不通过候选；本阶段直接过滤，不保存待发通知。</summary>
    public TimeSpan BusyQuietPeriod { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>精确名称匹配的辅助进程策略，不使用模糊前缀，以免误伤 GitKraken 等应用。</summary>
    public IReadOnlyList<string> BackgroundTools { get; init; } = Array.AsReadOnly(new[]
    {
        "git", "git-remote-http", "git-remote-https", "git-lfs", "conhost", "msbuild", "vctip"
    });

    /// <summary>构造前拒绝非有限分数和非正时间；引擎会复制名单，后续外部修改不会改变运行中策略。</summary>
    public void Validate()
    {
        if (!double.IsFinite(Threshold) || Threshold is < 0 or > 100 || SelectionInterval <= TimeSpan.Zero ||
            TopicInterval < SelectionInterval || TopicInterval.TotalSeconds / SelectionInterval.TotalSeconds > 512 ||
            ForegroundEvidenceWindow <= TimeSpan.Zero || BusyQuietPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InterestingnessOptions));
        ArgumentNullException.ThrowIfNull(BackgroundTools);
        foreach (var name in BackgroundTools) ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }
}
