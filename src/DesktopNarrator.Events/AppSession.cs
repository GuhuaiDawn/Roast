namespace DesktopNarrator.Events;

/// <summary>
/// 一段连续的应用前台驻留会话，只包含应用身份与时间，不保存窗口内容。
/// 本阶段包含无输入时段，不能把会话时长当成实际工作或输入操作时长。
/// </summary>
/// <param name="Id">会话标识，从开始到结束保持不变。</param>
/// <param name="ProcessName">进程名称；按名称忽略大小写聚合，不按 PID 拆分。</param>
/// <param name="FriendlyName">用于显示的应用描述，缺失时回退到进程名。</param>
/// <param name="StartTime">首次观察到该前台应用的日期时间，不推测监测前的历史。</param>
/// <param name="EndTime">切换或收尾的日期时间；null 表示会话仍在进行。</param>
/// <param name="Duration">独立计时得到的前台驻留时长；未结束时为 null，不受系统日期调整影响。</param>
public sealed record AppSession(
    Guid Id,
    string ProcessName,
    string FriendlyName,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime = null,
    TimeSpan? Duration = null)
{
    /// <summary>供后续统计和存储使用的秒数，保留亚秒精度，不提前取整。</summary>
    public double? DurationSeconds => Duration?.TotalSeconds;
}
