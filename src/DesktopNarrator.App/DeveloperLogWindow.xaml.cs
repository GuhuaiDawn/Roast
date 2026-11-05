using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using DesktopNarrator.Memory;

namespace DesktopNarrator.App;

/// <summary>桌面足迹视图；展示本地汇总、时间线和诊断，不负责采样或业务判断。</summary>
public partial class DeveloperLogWindow : Window
{
    private readonly DeveloperLogStore _logStore;
    // 合并连续日志产生的滚动请求，且允许窗口卸载时取消尚未执行的请求。
    private DispatcherOperation? _pendingScroll;
    private bool _subscribed;
    private readonly HistoryQueryService? _historyQueries;
    private CancellationTokenSource? _historyRequest;
    private int _historyOffset;
    private bool _historyLoading;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>只读汇总快照；null 表示未查询成功，不将数据库故障显示成零时长。</summary>
    public HistoryOverview? Overview
    {
        get => (HistoryOverview?)GetValue(OverviewProperty);
        private set => SetValue(OverviewPropertyKey, value);
    }
    private static readonly DependencyPropertyKey OverviewPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Overview), typeof(HistoryOverview), typeof(DeveloperLogWindow), new PropertyMetadata(null));
    public static readonly DependencyProperty OverviewProperty = OverviewPropertyKey.DependencyProperty;

    /// <summary>完整当日汇总生成的扇区；最多六个独立应用加“其他”，与会话分页无关。</summary>
    public IReadOnlyList<AppUsageSlice> Distribution
    {
        get => (IReadOnlyList<AppUsageSlice>)GetValue(DistributionProperty);
        private set => SetValue(DistributionPropertyKey, value);
    }
    private static readonly DependencyPropertyKey DistributionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Distribution), typeof(IReadOnlyList<AppUsageSlice>), typeof(DeveloperLogWindow), new PropertyMetadata(Array.Empty<AppUsageSlice>()));
    public static readonly DependencyProperty DistributionProperty = DistributionPropertyKey.DependencyProperty;

    public DeveloperLogWindow(DeveloperLogStore logStore, HistoryQueryService? historyQueries = null)
    {
        InitializeComponent();
        _logStore = logStore;
        _historyQueries = historyQueries;
        DataContext = logStore;
        RefreshHistoryButton.IsEnabled = historyQueries is not null;
        HistoryDate.IsEnabled = historyQueries is not null;
        HistoryDate.DisplayDateEnd = new DateTime(9998, 12, 31);
        HistoryDate.SelectedDate = DateTime.Today;
        PreviousHistoryButton.IsEnabled = false;
        NextHistoryButton.IsEnabled = false;
        if (historyQueries is null) HistoryStatus.Text = "未配置历史查询；当前前台状态与本次运行诊断仍可查看。";
        // 仅刷新可见的今日首页，不采样系统、不打断翻页，也不向数据库写展示数据。
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += OnRefreshTick;
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
        _refreshTimer.Start();
        if (HistoryTab.IsSelected) _ = LoadHistoryAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    protected override void OnClosed(EventArgs e)
    {
        Detach();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _refreshTimer.Tick -= OnRefreshTick;
        base.OnClosed(e);
    }

    /// <summary>卸载和关闭都可调用；解除订阅，防止日志存储长期持有已关闭的窗口。</summary>
    private void Detach()
    {
        _refreshTimer.Stop();
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
        if (!IsLoaded || !ReferenceEquals(e.Source, LogTabs)) return;
        if (HistoryTab.IsSelected) _ = LoadHistoryAsync();
        if (DebugTab.IsSelected) QueueScroll();
    }

    /// <summary>查询期间不重入；历史日期和非首页由用户手动刷新，防止阅读位置跳动。</summary>
    private void OnRefreshTick(object? sender, EventArgs e)
    {
        if (IsLoaded && IsVisible && HistoryTab.IsSelected && _historyOffset == 0 &&
            HistoryDate.SelectedDate?.Date == DateTime.Today && !_historyLoading)
            _ = LoadHistoryAsync(automatic: true);
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
    private async Task LoadHistoryAsync(bool automatic = false)
    {
        if (_historyQueries is null || !IsLoaded) return;
        _historyRequest?.Cancel();
        _historyRequest?.Dispose();
        _historyRequest = new CancellationTokenSource();
        var token = _historyRequest.Token;
        _historyLoading = true;
        RefreshHistoryButton.IsEnabled = PreviousHistoryButton.IsEnabled = NextHistoryButton.IsEnabled = false;
        HistoryEmptyState.Visibility = Visibility.Collapsed;
        if (!automatic)
        {
            Overview = null;
            Distribution = Array.Empty<AppUsageSlice>();
            SessionHistoryGrid.ItemsSource = null;
        }
        var date = DateOnly.FromDateTime(HistoryDate.SelectedDate ?? DateTime.Today);
        OverviewTitle.Text = date == DateOnly.FromDateTime(DateTime.Today) ? "今日概览" : "当日回顾";
        if (!automatic) HistoryStatus.Text = "";
        HistoryStatus.ToolTip = _historyQueries.DatabasePath;
        try
        {
            var (history, caughtUp) = await _historyQueries.ReadDayAsync(date, TimeZoneInfo.Local, _historyOffset, token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            Overview = HistoryOverview.From(history);
            Distribution = ApplicationDistribution.Build(history.Totals);
            SessionHistoryGrid.ItemsSource = history.Sessions;
            HistoryEmptyState.Visibility = history.SessionCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviousHistoryButton.IsEnabled = _historyOffset > 0;
            NextHistoryButton.IsEnabled = _historyOffset + history.Sessions.Count < history.SessionCount;
            HistoryPageText.Text = $"第 {_historyOffset / 100 + 1} / {Math.Max(1, (history.SessionCount + 99) / 100)} 页";
            HistoryStatus.Text = caughtUp ? "" : "保存尚未完成，最新记录可能暂未出现。";
            HistoryStatus.ToolTip = "跨日卡片只统计当天相交部分；时间线保留整段会话。调时后的日期分配按开始时间与单调时长估计。\n" + _historyQueries.DatabasePath;
        }
        catch (OperationCanceledException) { /* 旧查询作废，不把取消显示为数据库故障。 */ }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested || !IsLoaded) return;
            Overview = null;
            Distribution = Array.Empty<AppUsageSlice>();
            SessionHistoryGrid.ItemsSource = null;
            HistoryStatus.Text = "暂时无法读取本地记录，请稍后刷新；这不代表当天没有活动。";
            HistoryStatus.ToolTip = $"{exception.Message}\n{_historyQueries.DatabasePath}";
            HistoryPageText.Text = "未能读取";
        }
        finally
        {
            if (!token.IsCancellationRequested && IsLoaded)
            {
                _historyLoading = false;
                RefreshHistoryButton.IsEnabled = true;
            }
        }
    }

    private void QueueScroll()
    {
        if (!IsLoaded || !DebugTab.IsSelected || Dispatcher.HasShutdownStarted || _pendingScroll is not null) return;

        // 集合通知可能早于 WPF 更新视图和容器生成器；此时立即滚动曾导致 ItemsControl 不一致。
        // ContextIdle 优先级低于绑定和布局任务，将一批变化合并后再滚动。
        _pendingScroll = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _pendingScroll = null;
            if (!IsLoaded || !DebugTab.IsSelected || !_subscribed || LogList.Items.Count == 0) return;
            // 使用列表当前视图中的最后一项，不假定源集合与视图已处于相同更新阶段。
            LogList.ScrollIntoView(LogList.Items[^1]);
        }));
    }
}
