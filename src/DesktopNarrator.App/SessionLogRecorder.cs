using DesktopNarrator.Events;

namespace DesktopNarrator.App;

/// <summary>
/// 会话构建器到日志的适配器：同步聚合监测事件，只把界面更新放入 UI 队列。
/// 避免退出时 UI 队列停止处理，导致已经收到的前台切换没有参与最后会话的收尾。
/// </summary>
public sealed class SessionLogRecorder
{
    private readonly object _sync = new();
    private readonly AppSessionBuilder _builder = new();
    private readonly DeveloperLogStore _logStore;
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private bool _completed;

    /// <summary>默认使用系统单调计时；测试可注入可控时间，无需修改系统日期或生成输入。</summary>
    public SessionLogRecorder(DeveloperLogStore logStore, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(logStore);
        _logStore = logStore;
        _clock = clock ?? TimeProvider.System;
        _origin = _clock.GetTimestamp();
    }

    /// <summary>串行消费前台事件，记录应用切换、会话开始和会话结束；不访问界面控件。</summary>
    public void Record(ForegroundChangedEvent transition)
    {
        lock (_sync)
        {
            // 退出收尾与事件消费共用锁，收尾后的晚到回调不能重新开启会话。
            if (_completed) return;
            var previousId = _builder.CurrentSession?.Id;
            AppSession? ended;
            try { ended = _builder.Process(transition, _clock.GetElapsedTime(_origin)); }
            catch (ArgumentException exception)
            {
                _logStore.Append($"Session event ignored: {exception.Message}", LogCategory.Error);
                return;
            }

            var previous = FormatApplication(transition.PreviousApplication);
            var current = FormatApplication(transition.CurrentApplication);
            _logStore.Append($"[{transition.Timestamp:HH:mm:ss}] ForegroundChanged: {previous} → {current}", LogCategory.Monitor);
            if (ended is not null) WriteCompleted(ended, "应用切换");
            var active = _builder.CurrentSession!;
            if (active.Id != previousId)
                _logStore.Append($"[{active.StartTime:HH:mm:ss}] SessionStarted: {active.ProcessName} [{active.FriendlyName}]", LogCategory.Session);
            _logStore.SetCurrentSession(active);
        }
    }

    /// <summary>
    /// 一次性完成退出收尾；返回最后会话供测试及未来存储层消费。
    /// 调度器退出时日志不保证显示，因此结束摘要也会写入 VS 诊断输出。
    /// </summary>
    public AppSession? Complete(DateTimeOffset endedAt)
    {
        lock (_sync)
        {
            if (_completed) return null;
            var ended = _builder.Complete(endedAt, _clock.GetElapsedTime(_origin));
            _completed = true;
            if (ended is not null) WriteCompleted(ended, "监测结束");
            _logStore.SetCurrentSession(null);
            return ended;
        }
    }

    /// <summary>保留完整日期与跨天时长；摘要明确提示该统计包含无输入时段。</summary>
    private void WriteCompleted(AppSession session, string reason)
    {
        var message = $"SessionEnded: {session.ProcessName} [{session.FriendlyName}] | " +
            $"{session.StartTime:yyyy-MM-dd HH:mm:ss} → {session.EndTime:yyyy-MM-dd HH:mm:ss} | " +
            $"前台驻留={session.Duration:c}（含无输入时段） | {reason}";
        _logStore.Append(message, LogCategory.Session);
        System.Diagnostics.Trace.WriteLine(message);
    }

    private static string FormatApplication(ForegroundApplication? application) => application is null
        ? "（无）" : $"{application.ProcessName} [{application.FriendlyName}]";
}
