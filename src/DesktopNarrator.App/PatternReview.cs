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

    public string TypeLabel => Pattern.Type switch
    {
        PatternType.LongSession => "长时间停留",
        PatternType.RapidSwitching => "频繁切换",
        PatternType.ClosedThenReopened => "短时间内重开",
        PatternType.LongIdleReturn => "恢复输入",
        PatternType.RepeatedOpen => "多次启动",
        _ => "其他观察"
    };
    public string DisplayDecision => Evaluation is null ? "未评估" : Evaluation.IsSelected ? "通过初筛 · 未发送" : "暂不采用";
    public string DecisionColor => Evaluation is null ? "#AFBDCF" : Evaluation.IsSelected ? "#8EDCC3" : "#EBC58B";

    /// <summary>从结构化证据生成可读事实；缺失时明确回退，不从摘要猜数字或推断用户意图。</summary>
    public string ReadableSummary
    {
        get
        {
            if (Pattern.EvidenceJson is null || Pattern.EvidenceJson.Length > 16384) return "证据不完整，请查看原始记录。";
            try
            {
                using var document = JsonDocument.Parse(Pattern.EvidenceJson);
                var root = document.RootElement;
                double Number(string key)
                {
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var element) ||
                        !element.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0)
                        throw new FormatException();
                    return number;
                }
                int Count(string key)
                {
                    var number = Number(key);
                    if (number != Math.Truncate(number) || number > int.MaxValue) throw new FormatException();
                    return (int)number;
                }
                var app = ActivityPresentation.AppName(Pattern.ProcessName);
                return Pattern.Type switch
                {
                    PatternType.LongSession => $"{app} 已连续在前台停留 {ActivityPresentation.Duration(Number("DurationSeconds"))}，包含无输入时段。",
                    PatternType.RapidSwitching => $"最近 {ActivityPresentation.Duration(Number("WindowSeconds"))} 至少切换了 {Count("SwitchCountAtLeast")} 次应用，涉及 {Count("DistinctApplicationsInSample")} 个程序。",
                    PatternType.ClosedThenReopened => $"{app} 的进程消失后，间隔 {ActivityPresentation.Duration(Number("GapSeconds"))} 再次出现；不一定是主动重开。",
                    PatternType.LongIdleReturn => $"连续 {ActivityPresentation.Duration(Number("IdleDurationSeconds"))} 没有输入后，检测到输入恢复；不代表确认离开或返回座位。",
                    PatternType.RepeatedOpen => $"本次连续监测的同一天内，已观察到 {app} 启动 {Count("ObservedStarts")} 次。",
                    _ => "未知观察类型，请查看原始记录。"
                };
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
            {
                return "证据不完整，请查看原始记录。";
            }
        }
    }

    /// <summary>默认详情采用中文解释；技术 JSON 仍在独立页保留，评分不变也不冒充模型结论。</summary>
    public string ReadableDetails => $"观察到什么\n{ReadableSummary}\n\n初筛结论：{DisplayDecision}\n{ReasonText}\n\n" +
        (Evaluation is { } value
            ? $"规则得分 {value.Score:0.##} 分 / 通过线 {value.Threshold:0.##} 分（不是概率）\n" +
              $"规则强度 {value.Breakdown.Abnormality:0.##} · 前台关联 {value.Breakdown.PersonalRelevance:0.##} · 新颖性 {value.Breakdown.Novelty:0.##}\n" +
              $"当前状态 {value.Breakdown.ContextSuitability:0.##} · 表达潜力 {value.Breakdown.HumorPotential:0.##}（上述分量范围 0–1）\n" +
              $"近期扣分 {value.Breakdown.RecencyPenalty:0.##} 分 · 重复扣分 {value.Breakdown.RepetitionPenalty:0.##} 分\n" +
              $"评估时距离下一次允许通过还需 {ActivityPresentation.Duration(value.GlobalCooldownRemainingSeconds)}；同主题还需 {ActivityPresentation.Duration(value.TopicCooldownRemainingSeconds)}。\n\n"
            : "尚无评分记录。\n\n") +
        "这只是观察事实，不评价你的效率或意图。当前没有个人基线、全屏检测，也没有发送 AI 旁白。";

    /// <summary>展示单次决策时的分量、剩余间隔和上下文；不是实时倒计时或概率解释。</summary>
    public string EvaluationDetails => Evaluation is { } value
        ? $"{DecisionText} · 规则版本 {value.PolicyVersion}\n{ReasonText}\n" +
          $"评估时通过间隔剩余 {value.GlobalCooldownRemainingSeconds:0} 秒；同主题剩余 {value.TopicCooldownRemainingSeconds:0} 秒。\n" +
          "分数为规则启发值，不是概率；当前没有个人基线/旁白历史，全屏状态未检测。\n" +
          JsonSerializer.Serialize(value.Breakdown, new JsonSerializerOptions { WriteIndented = true }) + "\n" + value.ContextJson
        : "此候选未评分。";
}
