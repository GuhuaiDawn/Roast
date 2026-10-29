using System.Text.Json;
using DesktopNarrator.Events;

namespace DesktopNarrator.App;

/// <summary>候选页只读行模型；原始事实与初筛判断分开，历史未评分数据不会被显示为零分或已通过。</summary>
public sealed record PatternReview(DetectedPattern Pattern, PatternEvaluation? Evaluation)
{
    public DateTimeOffset EndTime => Pattern.EndTime;
    public PatternType Type => Pattern.Type;
    public string Summary => Pattern.Summary;
    public string EvidenceJson => Pattern.EvidenceJson;
    public string ScoreText => Evaluation is { } value ? $"{value.Score:0.##} / {value.Threshold:0.##}" : "未评分";
    public string DecisionText => Evaluation is null ? "未评分" : Evaluation.IsSelected ? "通过初筛（未发送）" : "已过滤";
    public string ReasonText => Evaluation is { } value ? string.Join("；", value.Reasons) : "没有评分记录";

    /// <summary>展示单次决策时的分量、剩余间隔和上下文；不是实时倒计时或概率解释。</summary>
    public string EvaluationDetails => Evaluation is { } value
        ? $"{DecisionText} · 规则版本 {value.PolicyVersion}\n{ReasonText}\n" +
          $"评估时通过间隔剩余 {value.GlobalCooldownRemainingSeconds:0} 秒；同主题剩余 {value.TopicCooldownRemainingSeconds:0} 秒。\n" +
          "分数为规则启发值，不是概率；当前没有个人基线/旁白历史，全屏状态未检测。\n" +
          JsonSerializer.Serialize(value.Breakdown, new JsonSerializerOptions { WriteIndented = true }) + "\n" + value.ContextJson
        : "此候选未评分。";
}
