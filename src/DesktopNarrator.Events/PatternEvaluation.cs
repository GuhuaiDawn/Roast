namespace DesktopNarrator.Events;

/// <summary>五项 0–1 规则分量及两项 0–100 分扣分；不是统计概率或已学习的个人画像。</summary>
public sealed record InterestingnessBreakdown(double Abnormality, double PersonalRelevance, double Novelty,
    double ContextSuitability, double HumorPotential, double RecencyPenalty, double RepetitionPenalty);

/// <summary>某个候选的初筛快照；通过仅表示可交给未来反馈层，不代表已发送旁白或适合立即弹窗。</summary>
public sealed record PatternEvaluation(Guid PatternId, DateTimeOffset EvaluatedAt, string PolicyVersion,
    double Score, double Threshold, bool IsSelected, InterestingnessBreakdown Breakdown,
    IReadOnlyList<string> Reasons, double GlobalCooldownRemainingSeconds, double TopicCooldownRemainingSeconds,
    string ContextJson);
