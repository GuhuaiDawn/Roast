using System.Text.Json;
using DesktopNarrator.Events;

namespace DesktopNarrator.Analysis;

/// <summary>
/// 单线程、无 I/O 的候选初筛。观察窗口与冷却只属于本次运行，不假装已读取个人历史或旁白历史。
/// 同批候选先按基础分排名，再按通过间隔筛选；被过滤的事实仍返回给日志和持久化层。
/// </summary>
public sealed class InterestingnessEngine
{
    public const string PolicyVersion = "rules-v1";
    private sealed class Topic
    {
        public TimeSpan LastSeen;
        public TimeSpan? LastSelected;
        public Queue<TimeSpan> Occurrences { get; } = new();
    }
    private sealed record Draft(DetectedPattern Pattern, string Key, double Strength, double Relevance,
        double Novelty, double Context, double Humor, double RepeatPenalty, IReadOnlyList<string> Blocks, string ContextJson)
    {
        public double BaseScore => 100 * (.30 * Strength + .25 * Relevance + .20 * Novelty + .15 * Context + .10 * Humor) - RepeatPenalty;
    }
    private readonly InterestingnessOptions _options;
    private readonly HashSet<string> _backgroundTools;
    private readonly Dictionary<string, TimeSpan> _foregroundSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Topic> _topics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<TimeSpan> _switches = new();
    private readonly HashSet<Guid> _evaluated = new();
    private readonly Queue<Guid> _evaluationOrder = new();
    private string? _foreground;
    private bool _idle;
    private TimeSpan _lastAt;
    private TimeSpan? _lastBusyAt;
    private TimeSpan? _lastSelectedAt;

    public InterestingnessEngine(InterestingnessOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
        _backgroundTools = new(_options.BackgroundTools.Select(Normalize), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>先观察当前输入状态，再评估该输入产生的候选；进程观察重置不清除已通过候选的冷却。</summary>
    public void Observe(IMonitorEvent value, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is ForegroundChangedEvent focused) ArgumentException.ThrowIfNullOrWhiteSpace(focused.CurrentApplication?.ProcessName);
        Advance(elapsed);
        switch (value)
        {
            case ForegroundChangedEvent focus:
                var name = Normalize(focus.CurrentApplication.ProcessName);
                if (_foreground is not null && !string.Equals(_foreground, name, StringComparison.OrdinalIgnoreCase))
                {
                    _switches.Enqueue(elapsed);
                    while (_switches.Count > 25) _switches.Dequeue();
                    if (_switches.Count == 25) _lastBusyAt = elapsed;
                }
                _foreground = name;
                _foregroundSeen[name] = elapsed;
                // 保存最近 256 个名称即可提供关联证据，不把整个运行历史无限放入内存。
                if (_foregroundSeen.Count > 256) _foregroundSeen.Remove(_foregroundSeen.MinBy(x => x.Value).Key);
                break;
            case UserIdleEvent: _idle = true; break;
            case UserReturnedEvent: _idle = false; break;
        }
    }

    /// <summary>
    /// 同一单调时间批量评估；返回有评分的候选，最近 4096 个已处理 ID 的重复输入直接忽略。
    /// 通过结果只消费“初筛通过间隔”，绝不宣称消费了真实旁白冷却。
    /// </summary>
    public IReadOnlyList<PatternEvaluation> EvaluateBatch(IReadOnlyList<DetectedPattern> patterns, DateTimeOffset now, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (var pattern in patterns)
            if (pattern is null || pattern.Id == Guid.Empty || !Enum.IsDefined(pattern.Type)) throw new ArgumentException("Invalid candidate identity.", nameof(patterns));
        Advance(elapsed);
        var pending = patterns.DistinctBy(p => p.Id).Where(p => !_evaluated.Contains(p.Id)).Select(p => BuildDraft(p, elapsed)).ToArray();
        var result = new List<PatternEvaluation>();
        // 稳定次序：分数相同保留输入顺序，不用随机 ID 决定优先级。
        foreach (var draft in pending.OrderByDescending(d => d.BaseScore))
        {
            var globalRemaining = Remaining(_lastSelectedAt, _options.SelectionInterval, elapsed);
            _topics.TryGetValue(draft.Key, out var topic);
            var topicRemaining = Remaining(topic?.LastSelected, _options.TopicInterval, elapsed);
            var recencyPenalty = 20 * globalRemaining / _options.SelectionInterval.TotalSeconds;
            var score = Math.Round(Math.Clamp(draft.BaseScore - recencyPenalty, 0, 100), 2);
            var reasons = new List<string>(draft.Blocks);
            if (topicRemaining > 0) reasons.Add($"同主题初筛间隔未到：剩余 {topicRemaining:0} 秒");
            if (globalRemaining > 0) reasons.Add($"候选通过间隔未到：剩余 {globalRemaining:0} 秒（不是已发送旁白）");
            if (score < _options.Threshold) reasons.Add($"规则分 {score:0.##} 低于阈值 {_options.Threshold:0.##}");
            var selected = reasons.Count == 0;
            if (selected) reasons.Add("达到阈值且通过当前初筛；尚未执行全屏检测、反馈调度或发送旁白");
            result.Add(new(draft.Pattern.Id, now, PolicyVersion, score, _options.Threshold, selected,
                new(draft.Strength, draft.Relevance, draft.Novelty, draft.Context, draft.Humor, recencyPenalty, draft.RepeatPenalty),
                reasons.AsReadOnly(), globalRemaining, topicRemaining, draft.ContextJson));
            topic ??= new Topic();
            topic.LastSeen = elapsed;
            topic.Occurrences.Enqueue(elapsed);
            while (topic.Occurrences.Count > 16) topic.Occurrences.Dequeue();
            if (selected) { topic.LastSelected = elapsed; _lastSelectedAt = elapsed; }
            _topics[draft.Key] = topic;
            _evaluated.Add(draft.Pattern.Id);
            _evaluationOrder.Enqueue(draft.Pattern.Id);
            while (_evaluationOrder.Count > 4096) _evaluated.Remove(_evaluationOrder.Dequeue());
        }
        // 噪声不能把仍在冷却中的已通过主题挤掉，否则大量辅助进程会意外取消防重复保护。
        // 配置限制同时处于主题冷却的通过项最多 513 个，故 1024 容量内始终有可淘汰项。
        while (_topics.Count > 1024)
            _topics.Remove(_topics.Where(x => Remaining(x.Value.LastSelected, _options.TopicInterval, elapsed) == 0)
                .MinBy(x => x.Value.LastSeen).Key);
        return result;
    }

    private Draft BuildDraft(DetectedPattern pattern, TimeSpan elapsed)
    {
        var process = Normalize(pattern.ProcessName ?? "");
        var key = $"{pattern.Type}:{process}";
        var seen = _foregroundSeen.ContainsKey(process);
        var lifecycle = pattern.Type is PatternType.ClosedThenReopened or PatternType.RepeatedOpen;
        var busy = Remaining(_lastBusyAt, _options.BusyQuietPeriod, elapsed) > 0;
        var reasons = new List<string>();
        var valid = TryStrength(pattern, out var strength, out var humor);
        if (!valid) reasons.Add("结构化证据缺失或无效，保守过滤");
        if (process.Equals("DesktopNarrator.App", StringComparison.OrdinalIgnoreCase)) reasons.Add("本程序自身的候选不参与旁白初筛");
        if (_backgroundTools.Contains(process)) reasons.Add("命中后台辅助进程策略；不将进程出现解释为用户主动操作");
        if (lifecycle && !seen) reasons.Add("缺少本次运行近期前台关联，启动类证据不足");
        if (_idle) reasons.Add("当前处于无输入状态，暂不通过候选");
        if (busy) reasons.Add("近期仍在快速切换应用，当前候选直接过滤，不排队等待");
        var relevance = pattern.Type switch
        {
            PatternType.LongSession => 1,
            PatternType.LongIdleReturn => .7,
            PatternType.RapidSwitching => .8,
            _ => seen ? .8 : .1
        };
        _topics.TryGetValue(key, out var topic);
        var novelty = topic is null ? 1 : .35;
        var repeatPenalty = Math.Min(20, (topic?.Occurrences.Count ?? 0) * 5);
        if (!valid) strength = relevance = novelty = humor = 0;
        var context = _idle || busy || !valid ? 0 : 1;
        var snapshot = JsonSerializer.Serialize(new
        {
            Scope = "current-run-only", PersonalBaselineAvailable = false, NarrationHistoryAvailable = false,
            ForegroundAssociationObserved = seen, IsIdle = _idle, RecentRapidSwitching = busy,
            FullScreenChecked = false, ScoringMeaning = "heuristic-not-probability"
        });
        return new(pattern, key, strength, relevance, novelty, context, humor, repeatPenalty, reasons.AsReadOnly(), snapshot);
    }

    /// <summary>只读取已声明的数字证据，绝不从摘要文本猜时长或猜用户意图；非法 JSON 不使监测崩溃。</summary>
    private static bool TryStrength(DetectedPattern pattern, out double strength, out double humor)
    {
        strength = humor = 0;
        if (pattern.EvidenceJson is null || pattern.EvidenceJson.Length > 16384) return false;
        try
        {
            using var document = JsonDocument.Parse(pattern.EvidenceJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var property = pattern.Type switch
            {
                PatternType.LongSession => "DurationSeconds",
                PatternType.LongIdleReturn => "IdleDurationSeconds",
                PatternType.ClosedThenReopened => "GapSeconds",
                PatternType.RepeatedOpen => "ObservedStarts",
                _ => "SwitchCountAtLeast"
            };
            if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number ||
                !element.TryGetDouble(out var amount) || !double.IsFinite(amount) || amount < 0) return false;
            if (pattern.Type is PatternType.LongSession or PatternType.ClosedThenReopened or PatternType.RepeatedOpen &&
                string.IsNullOrWhiteSpace(pattern.ProcessName)) return false;
            (strength, humor) = pattern.Type switch
            {
                PatternType.LongSession when amount > 0 => (Math.Clamp(amount / 5400 * .65, 0, 1), .4),
                PatternType.LongIdleReturn when amount > 0 => (Math.Clamp(amount / 1800 * .65, 0, 1), .6),
                PatternType.ClosedThenReopened when amount <= 600 => (1 - amount / 600 * .6, .75),
                PatternType.RepeatedOpen when amount >= 1 && amount == Math.Truncate(amount) => (Math.Clamp(amount / 6 * .65, 0, 1), .6),
                PatternType.RapidSwitching when amount >= 1 && amount == Math.Truncate(amount) => (Math.Clamp(amount / 25 * .65, 0, 1), .5),
                _ => (-1, 0)
            };
            return strength >= 0;
        }
        catch (JsonException) { return false; }
    }

    private void Advance(TimeSpan elapsed)
    {
        if (elapsed < _lastAt || elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        _lastAt = elapsed;
        if (_foreground is not null) _foregroundSeen[_foreground] = elapsed;
        while (_switches.TryPeek(out var first) && elapsed - first >= TimeSpan.FromMinutes(5)) _switches.Dequeue();
        foreach (var name in _foregroundSeen.Where(x => elapsed - x.Value > _options.ForegroundEvidenceWindow).Select(x => x.Key).ToArray()) _foregroundSeen.Remove(name);
        foreach (var topic in _topics.Values)
            while (topic.Occurrences.TryPeek(out var at) && elapsed - at >= _options.TopicInterval) topic.Occurrences.Dequeue();
        foreach (var key in _topics.Where(x => elapsed - x.Value.LastSeen >= _options.TopicInterval).Select(x => x.Key).ToArray()) _topics.Remove(key);
    }

    private static double Remaining(TimeSpan? last, TimeSpan interval, TimeSpan now) => last is { } value ? Math.Max(0, (interval - (now - value)).TotalSeconds) : 0;
    private static string Normalize(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
