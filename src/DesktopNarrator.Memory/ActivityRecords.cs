using System.Text.Json;
using DesktopNarrator.Events;

namespace DesktopNarrator.Memory;

/// <summary>事件采用明确字段白名单，不序列化任意系统对象或完整开发日志。</summary>
public sealed record StoredMonitorEvent(Guid Id, string EventType, DateTimeOffset Timestamp, string? ProcessName, string MetadataJson)
{
    /// <summary>只允许当前阶段已声明的事件；元数据仅含进程名、时间和采样语义。</summary>
    public static StoredMonitorEvent From(IMonitorEvent value) => value switch
    {
        ForegroundChangedEvent focused => new(Guid.NewGuid(), "AppFocused", focused.Timestamp,
            focused.CurrentApplication.ProcessName, JsonSerializer.Serialize(new { PreviousProcess = focused.PreviousApplication?.ProcessName })),
        UserIdleEvent idle => new(Guid.NewGuid(), "UserIdle", idle.Timestamp, null,
            JsonSerializer.Serialize(new { idle.IdleStartedAt })),
        UserReturnedEvent returned => new(Guid.NewGuid(), "UserReturned", returned.Timestamp, null,
            JsonSerializer.Serialize(new { returned.ReturnedAt, IdleDurationSeconds = returned.IdleDuration.TotalSeconds })),
        AppStartedEvent started => new(Guid.NewGuid(), "AppStarted", started.Timestamp, started.ProcessName,
            "{\"Observation\":\"process-group-snapshot\"}"),
        AppClosedEvent closed => new(Guid.NewGuid(), "AppClosed", closed.Timestamp, closed.ProcessName,
            "{\"Observation\":\"process-group-snapshot\"}"),
        ProcessObservationResetEvent reset => new(Guid.NewGuid(), "ProcessObservationReset", reset.Timestamp, null, "{}"),
        _ => throw new ArgumentException("Unsupported monitor event.", nameof(value))
    };
}

/// <summary>一个事务写入单元；一次切换及其结束的会话不可部分保存。</summary>
public sealed record ActivityWrite(StoredMonitorEvent? Event, AppSession? Session, DetectedPattern? Pattern = null,
    PatternEvaluation? Evaluation = null);

/// <summary>所选日期与已结束会话相交的前台总秒数，不等于实际工作时间。</summary>
public sealed record DailyAppTotal(string ProcessName, string FriendlyName, double TotalForegroundSeconds, long SessionCount)
{
    public string DurationText => TimeSpan.FromSeconds(TotalForegroundSeconds).ToString("c");
}

/// <summary>分页历史；总计基于全部匹配会话，不仅是当前页。</summary>
public sealed record DayHistory(DateOnly Date, IReadOnlyList<DailyAppTotal> Totals,
    IReadOnlyList<AppSession> Sessions, long SessionCount, int Offset, int PageSize);
