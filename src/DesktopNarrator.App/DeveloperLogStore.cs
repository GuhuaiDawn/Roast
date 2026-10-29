using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using DesktopNarrator.Events;

namespace DesktopNarrator.App;

/// <summary>日志来源分类；用于区分系统生命周期、监测事件和异常诊断。</summary>
public enum LogCategory
{
    System,
    Monitor,
    Session,
    Pattern,
    Interestingness,
    Error
}

/// <summary>
/// UI 线程拥有的内存日志存储；生产者只能追加，界面通过只读集合订阅变化。
/// 当前不落盘，也未设置容量上限；不是长期历史数据库。
/// </summary>
public sealed class DeveloperLogStore : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private readonly ObservableCollection<string> _entries = new();
    private readonly ObservableCollection<PatternReview> _patterns = new();
    private long _evaluatedCount;
    private long _selectedCount;
    private string _currentSessionSummary = "当前会话：等待首次前台事件";
    private string _persistenceSummary = "持久化：未配置";

    /// <summary>属性变化和集合变化一样，始终在日志存储所属的 UI 线程通知。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前应用和开始时间；不每秒写日志，结束时才输出最终时长。</summary>
    public string CurrentSessionSummary => _currentSessionSummary;

    /// <summary>数据库状态与当前会话分开展示，避免把内存日志误认为已经落盘。</summary>
    public string PersistenceSummary => _persistenceSummary;

    /// <summary>本次运行累计初筛数量；不是发送次数，也不是数据库提交确认。</summary>
    public string EvaluationSummary => $"本次已评估 {_evaluatedCount} 个 · 通过初筛 {_selectedCount} 个 · 过滤 {_evaluatedCount - _selectedCount} 个 · 尚未发送旁白";

    public DeveloperLogStore()
    {
        // 必须在 UI 线程构造以捕获正确的 Dispatcher；之后允许任意线程提交日志。
        _dispatcher = Dispatcher.CurrentDispatcher;
        Entries = new ReadOnlyObservableCollection<string>(_entries);
        Patterns = new ReadOnlyObservableCollection<PatternReview>(_patterns);
    }

    /// <summary>仅用于 UI 绑定和观察；禁止外部绕过 Append 直接修改集合。</summary>
    public ReadOnlyObservableCollection<string> Entries { get; }

    /// <summary>本次运行最近 200 个候选；完整已接受历史由 SQLite 保存，界面列表不是持久化确认。</summary>
    public ReadOnlyObservableCollection<PatternReview> Patterns { get; }

    /// <summary>沿用日志的异步 UI 调度，避免候选列表重现跨线程或集合通知重入问题。</summary>
    public void AppendPattern(DetectedPattern pattern, PatternEvaluation? evaluation = null)
    {
        var row = new PatternReview(pattern, evaluation);
        Append($"[{pattern.EndTime:HH:mm:ss}] {pattern.Type}: {pattern.Summary}", LogCategory.Pattern);
        if (evaluation is not null) Append($"{pattern.Type} · {row.ScoreText} · {row.DecisionText} · {row.ReasonText}", LogCategory.Interestingness);
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _patterns.Insert(0, row);
            while (_patterns.Count > 200) _patterns.RemoveAt(_patterns.Count - 1);
            if (evaluation is not null)
            {
                _evaluatedCount++;
                if (evaluation.IsSelected) _selectedCount++;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EvaluationSummary)));
            }
        }));
    }

    /// <summary>异步排队追加日志，返回时不保证已显示；调度器关闭后丢弃晚到的日志。</summary>
    public void Append(string message, LogCategory category)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        var entry = $"[{category}] {message}";
        // 即便调用方在 UI 线程也延后执行，避免在 CollectionChanged 通知中嵌套修改集合。
        // 所有真正的 Add 都在同一个 Dispatcher 上执行，后台线程不会直接碰绑定数据。
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _entries.Add(entry)));
    }

    /// <summary>异步更新会话概要；窗口关闭时状态仍在内存中，重新打开即可看到最新值。</summary>
    public void SetCurrentSession(AppSession? session)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        var summary = session is null ? "当前会话：监测已结束" :
            $"当前会话：{session.ProcessName} [{session.FriendlyName}] · 开始 {session.StartTime:yyyy-MM-dd HH:mm:ss}";
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_currentSessionSummary == summary) return;
            _currentSessionSummary = summary;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentSessionSummary)));
        }));
    }

    /// <summary>写入线程只提交状态文本，由 UI 调度器通知绑定。</summary>
    public void SetPersistenceStatus(string message)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _persistenceSummary = message;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PersistenceSummary)));
        }));
    }
}
