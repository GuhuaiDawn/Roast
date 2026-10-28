using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace DesktopNarrator.App;

/// <summary>开发日志视图；只负责绑定与自动滚动，不负责采样或业务状态判断。</summary>
public partial class DeveloperLogWindow : Window
{
    private readonly DeveloperLogStore _logStore;
    // 合并连续日志产生的滚动请求，且允许窗口卸载时取消尚未执行的请求。
    private DispatcherOperation? _pendingScroll;
    private bool _subscribed;

    public DeveloperLogWindow(DeveloperLogStore logStore)
    {
        InitializeComponent();
        _logStore = logStore;
        DataContext = logStore;
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
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueScroll();

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
