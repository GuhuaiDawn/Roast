using System.Text.Json;
using DesktopNarrator.Events;

namespace DesktopNarrator.Analysis;

/// <summary>
/// 单线程纯规则状态机；只消费已确认的事件与外部单调计时，不读 Windows、数据库或真实时钟。
/// 候选表示观察事实，不推测用户意图，也不直接触发旁白。新实例从零观察，不补造离线历史。
/// </summary>
public sealed class PatternDetector
{
    private sealed record Switch(TimeSpan At, DateTimeOffset WallTime, string From, string To);
    private sealed record Closed(TimeSpan At, DateTimeOffset WallTime);
    private sealed record OpenCount(int Count, DateTimeOffset First);
    private readonly PatternOptions _options;
    private readonly TimeZoneInfo _zone;
    private readonly Queue<Switch> _switches = new();
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Closed> _closed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OpenCount> _opens = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _lastAt;
    private string? _foreground;
    private DateTimeOffset _sessionWall;
    private TimeSpan _sessionAt;
    private bool _longEmitted;
    private bool _rapidHigh;
    private DateOnly? _openDay;
    private UserReturnedEvent? _lastReturn;

    public PatternDetector(PatternOptions? options = null, TimeZoneInfo? zone = null)
    {
        _options = options ?? new();
        _options.Validate();
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>处理一项有序输入；elapsed 与 Tick 必须使用同一非递减基点，墙上时间允许回拨。</summary>
    public IReadOnlyList<DetectedPattern> Observe(IMonitorEvent value, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(value);
        // 先校验输入再推进时间和队列；非法输入不能悄悄破坏已有状态。
        switch (value)
        {
            case ForegroundChangedEvent focused:
                ArgumentNullException.ThrowIfNull(focused.CurrentApplication);
                ArgumentException.ThrowIfNullOrWhiteSpace(focused.CurrentApplication.ProcessName);
                break;
            case AppStartedEvent started: ArgumentException.ThrowIfNullOrWhiteSpace(started.ProcessName); break;
            case AppClosedEvent closed: ArgumentException.ThrowIfNullOrWhiteSpace(closed.ProcessName); break;
            case UserReturnedEvent returned:
                if (returned.IdleDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value));
                _ = returned.ReturnedAt - returned.IdleDuration; // 日期范围校验也必须先于状态更新。
                break;
            case UserIdleEvent or ProcessObservationResetEvent: break;
            default: throw new ArgumentException("Unsupported pattern input.", nameof(value));
        }
        var result = Advance(value.Timestamp, elapsed);
        switch (value)
        {
            case ForegroundChangedEvent focused:
                var name = focused.CurrentApplication.ProcessName;
                if (string.Equals(_foreground, name, StringComparison.OrdinalIgnoreCase)) break;
                // 首次观察不是一次切换，不能从 PreviousApplication 猜测启动前的状态。
                if (_foreground is not null)
                {
                    _switches.Enqueue(new(elapsed, focused.Timestamp, _foreground, name));
                    // 仅保留触发阈值所需的最新样本；证据明确为“至少”，内存不随突发量增长。
                    while (_switches.Count > _options.SwitchCount) _switches.Dequeue();
                    if (!_rapidHigh && _switches.Count >= _options.SwitchCount)
                    {
                        _rapidHigh = true;
                        var distinct = _switches.SelectMany(s => new[] { s.From, s.To }).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                        result.Add(Create(PatternType.RapidSwitching, _switches.Peek().WallTime, value.Timestamp, null,
                            $"最近 {_options.SwitchWindow.TotalMinutes:0.##} 分钟至少切换 {_switches.Count} 次；这些切换涉及 {distinct} 个程序。",
                            new { WindowSeconds = _options.SwitchWindow.TotalSeconds, SwitchCountAtLeast = _switches.Count, DistinctApplicationsInSample = distinct }));
                    }
                }
                _foreground = name;
                _sessionAt = elapsed;
                _sessionWall = focused.Timestamp;
                _longEmitted = false;
                break;
            case UserReturnedEvent returned:
                if (returned != _lastReturn && returned.IdleDuration >= _options.LongIdle)
                    result.Add(Create(PatternType.LongIdleReturn, returned.ReturnedAt - returned.IdleDuration, returned.Timestamp, null,
                        $"从 {returned.IdleDuration:c} 的无输入状态返回（不等于确认离开）。",
                        new { IdleDurationSeconds = returned.IdleDuration.TotalSeconds, returned.ReturnedAt }));
                _lastReturn = returned;
                break;
            case AppClosedEvent closed:
                // 基线内已存在的程序也可以先收到关闭事件；重复关闭不能刷新重开窗口。
                _running.Remove(closed.ProcessName);
                _closed.TryAdd(closed.ProcessName, new(elapsed, closed.Timestamp));
                break;
            case AppStartedEvent started:
                if (!_running.Add(started.ProcessName)) break;
                if (_closed.Remove(started.ProcessName, out var previous))
                    result.Add(Create(PatternType.ClosedThenReopened, previous.WallTime, started.Timestamp, started.ProcessName,
                        $"{started.ProcessName} 进程组消失后 {elapsed - previous.At:c} 再次出现（快照观察）。",
                        new { GapSeconds = (elapsed - previous.At).TotalSeconds, Observation = "process-group-snapshot" }));
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(started.Timestamp, _zone).DateTime);
                if (_openDay != day) { _opens.Clear(); _openDay = day; }
                var count = _opens.TryGetValue(started.ProcessName, out var old)
                    ? old with { Count = Math.Min(old.Count + 1, _options.DailyOpenCount + 1) } : new OpenCount(1, started.Timestamp);
                _opens[started.ProcessName] = count;
                if (count.Count == _options.DailyOpenCount)
                    result.Add(Create(PatternType.RepeatedOpen, count.First, started.Timestamp, started.ProcessName,
                        $"本次连续监测中，{day:yyyy-MM-dd} 已观察到 {started.ProcessName} 启动 {count.Count} 次。",
                        new { Date = day, ObservedStarts = count.Count, Scope = "current-continuous-monitoring-run" }));
                break;
            case ProcessObservationResetEvent:
                _running.Clear(); _closed.Clear(); _opens.Clear(); _openDay = null;
                break;
        }
        return result;
    }

    /// <summary>无需切换应用也能检测长会话；调用方低频触发，不为每个 tick 写事件或日志。</summary>
    public IReadOnlyList<DetectedPattern> Tick(DateTimeOffset now, TimeSpan elapsed) => Advance(now, elapsed);

    private List<DetectedPattern> Advance(DateTimeOffset now, TimeSpan elapsed)
    {
        if (elapsed < _lastAt || elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        _lastAt = elapsed;
        while (_switches.TryPeek(out var first) && elapsed - first.At >= _options.SwitchWindow) _switches.Dequeue();
        if (_switches.Count < _options.SwitchCount) _rapidHigh = false;
        foreach (var name in _closed.Where(x => elapsed - x.Value.At > _options.ReopenWindow).Select(x => x.Key).ToArray()) _closed.Remove(name);
        var result = new List<DetectedPattern>();
        if (_foreground is not null && !_longEmitted && elapsed - _sessionAt >= _options.LongSession)
        {
            _longEmitted = true;
            result.Add(Create(PatternType.LongSession, _sessionWall, now, _foreground,
                $"{_foreground} 已连续前台驻留 {elapsed - _sessionAt:c}（包含无输入时段）。",
                new { DurationSeconds = (elapsed - _sessionAt).TotalSeconds, IncludesIdle = true }));
        }
        return result;
    }

    private static DetectedPattern Create(PatternType type, DateTimeOffset start, DateTimeOffset end, string? process, string summary, object evidence)
        => new(Guid.NewGuid(), type, start, end, process, summary, JsonSerializer.Serialize(evidence));
}
