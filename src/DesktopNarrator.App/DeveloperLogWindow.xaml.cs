using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using DesktopNarrator.Memory;

namespace DesktopNarrator.App;

/// <summary>开发日志视图；只负责绑定与自动滚动，不负责采样或业务状态判断。</summary>
public partial class DeveloperLogWindow : Window
{
    private readonly DeveloperLogStore _logStore;
    // 合并连续日志产生的滚动请求，且允许窗口卸载时取消尚未执行的请求。
    private DispatcherOperation? _pendingScroll;
    private bool _subscribed;
    private readonly HistoryQueryService? _historyQueries;
    private CancellationTokenSource? _historyRequest;
    private int _historyOffset;

    public DeveloperLogWindow(DeveloperLogStore logStore, HistoryQueryService? historyQueries = null)
    {
        InitializeComponent();
        _logStore = logStore;
        _historyQueries = historyQueries;
        DataContext = logStore;
        HistoryTab.IsEnabled = historyQueries is not null;
        HistoryDate.DisplayDateEnd = new DateTime(9998, 12, 31);
        HistoryDate.SelectedDate = DateTime.Today;
        PreviousHistoryButton.IsEnabled = false;
        NextHistoryButton.IsEnabled = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>视图加载后订阅集合；防止重复 Loaded 导致同一窗口重复订阅。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            ((INotifyCollectionChanged)_logStore.Entries).CollectionChanged += OnEntriesChanged;
            _subscribed = true;
        }
        QueueScroll();
        if (HistoryTab.IsSelected) _ = LoadHistoryAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    protected override void OnClosed(EventArgs e)
    {
        Detach();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        base.OnClosed(e);
    }

    /// <summary>卸载和关闭都可调用；解除订阅，防止日志存储长期持有已关闭的窗口。</summary>
    private void Detach()
    {
        if (_subscribed)
        {
            ((INotifyCollectionChanged)_logStore.Entries).CollectionChanged -= OnEntriesChanged;
            _subscribed = false;
        }
        _pendingScroll?.Abort();
        _pendingScroll = null;
        _historyRequest?.Cancel();
        _historyRequest?.Dispose();
        _historyRequest = null;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueScroll();

    private void OnHistorySelected(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && ReferenceEquals(e.Source, LogTabs) && HistoryTab.IsSelected) _ = LoadHistoryAsync();
    }

    private void OnHistoryDateChanged(object sender, SelectionChangedEventArgs e)
    {
        _historyOffset = 0;
        if (IsLoaded && HistoryTab.IsSelected) _ = LoadHistoryAsync();
    }

    private void OnRefreshHistory(object sender, RoutedEventArgs e) { _historyOffset = 0; _ = LoadHistoryAsync(); }
    private void OnPreviousHistory(object sender, RoutedEventArgs e) { _historyOffset = Math.Max(0, _historyOffset - 100); _ = LoadHistoryAsync(); }
    private void OnNextHistory(object sender, RoutedEventArgs e) { _historyOffset += 100; _ = LoadHistoryAsync(); }

    /// <summary>后台查询，结果回到 UI 后再绑定；切换日期或关窗时取消旧请求，防止旧结果覆盖新页面。</summary>
    private async Task LoadHistoryAsync()
    {
        if (_historyQueries is null || !IsLoaded) return;
        _historyRequest?.Cancel();
        _historyRequest?.Dispose();
        _historyRequest = new CancellationTokenSource();
        var token = _historyRequest.Token;
        RefreshHistoryButton.IsEnabled = PreviousHistoryButton.IsEnabled = NextHistoryButton.IsEnabled = false;
        HistoryStatus.Text = "正在读取已保存历史…";
        try
        {
            var date = DateOnly.FromDateTime(HistoryDate.SelectedDate ?? DateTime.Today);
            var (history, caughtUp) = await _historyQueries.ReadDayAsync(date, TimeZoneInfo.Local, _historyOffset, token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            DailyTotalsGrid.ItemsSource = history.Totals;
            SessionHistoryGrid.ItemsSource = history.Sessions;
            PreviousHistoryButton.IsEnabled = _historyOffset > 0;
            NextHistoryButton.IsEnabled = _historyOffset + history.Sessions.Count < history.SessionCount;
            HistoryStatus.Text = $"{date:yyyy-MM-dd} · {history.Totals.Count} 个应用 · {history.SessionCount} 段已保存会话 · 第 {_historyOffset / 100 + 1} 页\n" +
                (caughtUp ? "不含当前未结束会话。" : "写入尚未跟上，仅显示已提交历史。") +
                " 调时后的跨日分配按开始时间与时长估计。\n" + _historyQueries.DatabasePath;
        }
        catch (OperationCanceledException) { /* 旧查询作废，不把取消显示为数据库故障。 */ }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested || !IsLoaded) return;
            DailyTotalsGrid.ItemsSource = null;
            SessionHistoryGrid.ItemsSource = null;
            HistoryStatus.Text = $"历史暂不可用，可稍后刷新：{exception.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested && IsLoaded) RefreshHistoryButton.IsEnabled = true;
        }
    }

    private void QueueScroll()
    {
        if (!IsLoaded || Dispatcher.HasShutdownStarted || _pendingScroll is not null) return;

        // 集合通知可能早于 WPF 更新视图和容器生成器；此时立即滚动曾导致 ItemsControl 不一致。
        // ContextIdle 优先级低于绑定和布局任务，将一批变化合并后再滚动。
        _pendingScroll = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _pendingScroll = null;
            if (!IsLoaded || !_subscribed || LogList.Items.Count == 0) return;
            // 使用列表当前视图中的最后一项，不假定源集合与视图已处于相同更新阶段。
            LogList.ScrollIntoView(LogList.Items[^1]);
        }));
    }
}
