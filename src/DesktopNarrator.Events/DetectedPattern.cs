namespace DesktopNarrator.Events;

/// <summary>已由规则确认的事实类型；评分单独记录，不修改原始事实，也不是旁白。</summary>
public enum PatternType { LongSession, RapidSwitching, ClosedThenReopened, LongIdleReturn, RepeatedOpen }

/// <summary>模式候选及最小结构化证据；日期用于展示，EvidenceJson 内的时长秒数来自单调计时。</summary>
public sealed record DetectedPattern(Guid Id, PatternType Type, DateTimeOffset StartTime,
    DateTimeOffset EndTime, string? ProcessName, string Summary, string EvidenceJson);

/// <summary>模式持久化的非阻塞入口；返回 true 仅表示接受入队，不代表事务已经提交。</summary>
public interface IPatternPersistence
{
    /// <summary>排队保存完整候选及可选评分，同一事务提交；不为旧候选编造评分。</summary>
    bool TrySavePattern(DetectedPattern pattern, PatternEvaluation? evaluation = null);
}
