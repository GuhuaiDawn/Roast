using DesktopNarrator.Analysis;
using DesktopNarrator.Events;

namespace DesktopNarrator.App;

/// <summary>串行连接不同监测线程、纯规则、候选日志和异步存储；不在锁内访问控件或磁盘。</summary>
public sealed class PatternLogRecorder : IDisposable
{
    private readonly object _sync = new();
    private readonly PatternDetector _detector;
    private readonly InterestingnessEngine _interestingness;
    private readonly DeveloperLogStore _log;
    private readonly IPatternPersistence? _persistence;
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private Timer? _timer;
    private bool _completed;

    public PatternLogRecorder(DeveloperLogStore log, IPatternPersistence? persistence = null,
        PatternOptions? options = null, TimeProvider? clock = null, InterestingnessOptions? scoringOptions = null)
    {
        _log = log;
        _persistence = persistence;
        _clock = clock ?? TimeProvider.System;
        _origin = _clock.GetTimestamp();
        _detector = new PatternDetector(options, _clock.LocalTimeZone);
        _interestingness = new InterestingnessEngine(scoringOptions);
    }

    /// <summary>每 5 秒检查当前长会话；没有新候选时不追加日志，不落盘原始定时采样。</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>在锁内获取单调时间，避免多来源线程先取时间后竞争锁造成时间逆序。</summary>
    public void Record(IMonitorEvent value)
    {
        lock (_sync)
        {
            if (_completed) return;
            try
            {
                var elapsed = _clock.GetElapsedTime(_origin);
                var patterns = _detector.Observe(value, elapsed);
                // 返回/空闲及前台状态必须先更新，再决定本批候选是否适合进入下一层。
                _interestingness.Observe(value, elapsed);
                Publish(patterns, elapsed);
            }
            catch (Exception exception) { _log.Append($"Pattern input error: {exception.Message}", LogCategory.Error); }
        }
    }

    /// <summary>公开给确定性测试的心跳入口；定时器线程异常必须被隔离，不能终止整个应用。</summary>
    public void Tick()
    {
        lock (_sync)
        {
            if (_completed) return;
            try
            {
                var elapsed = _clock.GetElapsedTime(_origin);
                Publish(_detector.Tick(_clock.GetLocalNow(), elapsed), elapsed);
            }
            catch (Exception exception) { _log.Append($"Pattern tick error: {exception.Message}", LogCategory.Error); }
        }
    }

    /// <summary>停止后拒绝晚到回调；已经在临界区的写入在返回前完成排队，然后才允许数据库排空。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_completed) return;
            Tick();
            _completed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Publish(IReadOnlyList<DetectedPattern> patterns, TimeSpan elapsed)
    {
        var evaluations = _interestingness.EvaluateBatch(patterns, _clock.GetLocalNow(), elapsed);
        var byId = patterns.ToDictionary(pattern => pattern.Id);
        foreach (var evaluation in evaluations)
        {
            var pattern = byId[evaluation.PatternId];
            _persistence?.TrySavePattern(pattern, evaluation);
            _log.AppendPattern(pattern, evaluation);
        }
    }
}
