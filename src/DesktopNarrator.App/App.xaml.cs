using System.Windows;
using DesktopNarrator.Events;
using DesktopNarrator.Monitor;
using DesktopNarrator.Memory;

namespace DesktopNarrator.App;

/// <summary>
/// 应用组装入口：管理托盘、监测器和日志窗口的生命周期，并将监测事件转换为日志。
/// 系统采样留在 Monitor 层、会话聚合留在 Events 层；日志窗口不直接访问 Windows API。
/// </summary>
public partial class App : Application
{
    private readonly DeveloperLogStore _logStore = new();
    private SessionLogRecorder? _sessionLog;
    private PatternLogRecorder? _patterns;
    private ProcessMonitor? _processMonitor;
    private ActivityPersistence? _persistence;
    private HistoryQueryService? _historyQueries;
    private ForegroundMonitor? _foregroundMonitor;
    private IIdleMonitor? _idleMonitor;
    private TimeSpan _idleThreshold = TimeSpan.FromMinutes(5);
    private TrayHostWindow? _trayHost;
    private TrayIcon? _trayIcon;
    private DeveloperLogWindow? _developerLogWindow;
    // 当前仅保留旁白开关状态，尚无旁白管线；切换它不会停止前台或空闲监测。
    private bool _narrationEnabled = true;

    /// <summary>解析本次启动的调试参数，建立用于接收托盘消息的隐藏窗口。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 构造仓储不访问磁盘；初始化和全部写入都由后台队列执行，故障不阻止监测启动。
        var database = new SqliteActivityStore(SqliteActivityStore.DefaultDatabasePath);
        _persistence = new ActivityPersistence(database);
        _persistence.StatusChanged += OnPersistenceStatus;
        _historyQueries = new HistoryQueryService(database, _persistence);
        _persistence.Start();
        _sessionLog = new SessionLogRecorder(_logStore, persistence: _persistence);
        _patterns = new PatternLogRecorder(_logStore, _persistence);
        _patterns.Start();
        _logStore.Append("初筛 rules-v1：阈值 75 分；候选通过间隔 20 分钟，同主题 3 小时；过滤辅助进程、缺少前台关联的启动候选及空闲/快速切换场景。只保留本次运行评分上下文，不等于已发送旁白。", LogCategory.System);
        _logStore.Append("模式规则：前台 ≥90 分钟；5 分钟内 ≥25 次切换；关闭后 ≤10 分钟重开；无输入 ≥30 分钟后返回；本次监测当日启动 ≥6 次。仅记录候选，不生成旁白。", LogCategory.System);
        _logStore.Append($"本地数据库：{database.DatabasePath}（仅保存进程身份与时间，不上传）", LogCategory.System);

        // 临时缩短验收等待时间，不写入配置；非法参数回退到默认 300 秒并留下诊断。
        var thresholdArgument = Array.IndexOf(e.Args, "--idle-seconds");
        if (thresholdArgument >= 0)
        {
            if (thresholdArgument + 1 < e.Args.Length &&
                int.TryParse(e.Args[thresholdArgument + 1], out var seconds) && seconds is >= 1 and <= 86400)
                _idleThreshold = TimeSpan.FromSeconds(seconds);
            else
                _logStore.Append("--idle-seconds 需要 1–86400 的整数；已使用默认 300 秒。", LogCategory.Error);
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _trayHost = new TrayHostWindow();
        _trayHost.SourceInitialized += OnTrayHostInitialized;
        // Show 触发 HWND 创建与 SourceInitialized，随后隐藏；不能只创建对象就取得有效句柄。
        _trayHost.Show();
        _trayHost.Hide();

        _logStore.Append($"Desktop Narrator 已启动。记录前台会话与空闲事件；空闲阈值 {_idleThreshold.TotalSeconds:0} 秒，采样间隔 1 秒；会话时长包含无输入时段。", LogCategory.System);
    }

    /// <summary>先停止事件来源，再释放托盘和承载窗口，避免后台资源遗留。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _processMonitor?.Dispose();
            _idleMonitor?.Dispose();
            _foregroundMonitor?.Dispose();
        }
        finally
        {
            // 先停止生产者，再结束最后会话；即使停止监测失败也尝试收尾。
            // 适配器会拒绝之后晚到的事件，避免退出期间重新打开一段会话。
            _sessionLog?.Complete(DateTimeOffset.Now);
            _patterns?.Dispose();
            // 最后一段会话入队后再排空。超时明确报出，不能把未写数据当成已保存。
            if (_persistence is not null && !_persistence.Shutdown(TimeSpan.FromSeconds(5)))
                System.Diagnostics.Trace.TraceError($"数据库退出未完全保存：待写 {_persistence.PendingCount}，拒绝 {_persistence.RejectedCount}");
            _trayIcon?.Dispose();
            _trayHost?.Close();
            base.OnExit(e);
        }
    }

    /// <summary>HWND 创建后注册托盘回调，分别启动前台、空闲和进程监测器。</summary>
    private void OnTrayHostInitialized(object? sender, EventArgs e)
    {
        if (_trayHost is null)
        {
            return;
        }

        try
        {
            _trayIcon = new TrayIcon(_trayHost.Handle);
            _trayIcon.ContextMenuRequested += OnTrayContextMenuRequested;
            _trayIcon.LeftDoubleClicked += (_, _) => ShowDeveloperLog();

            _foregroundMonitor = new ForegroundMonitor();
            _foregroundMonitor.ForegroundChanged += OnForegroundChanged;
            _foregroundMonitor.Error += OnMonitorError;
            _foregroundMonitor.Start();
        }
        catch (Exception exception)
        {
            _logStore.Append(
                $"[{DateTimeOffset.Now:HH:mm:ss}] Startup error: {exception.Message}",
                LogCategory.Error);
        }

        // 分开处理启动异常，前台监测启动失败不应阻止空闲监测独立工作。
        try
        {
            _idleMonitor = new IdleMonitor(_idleThreshold);
            _idleMonitor.UserIdle += OnUserIdle;
            _idleMonitor.UserReturned += OnUserReturned;
            _idleMonitor.Error += OnMonitorError;
            _idleMonitor.Start();
        }
        catch (Exception exception)
        {
            _logStore.Append($"Idle monitor startup error: {exception.Message}", LogCategory.Error);
        }

        try
        {
            _processMonitor = new ProcessMonitor();
            _processMonitor.Changed += OnProcessChanged;
            _processMonitor.Error += OnMonitorError;
            _processMonitor.Start();
        }
        catch (Exception exception)
        {
            _logStore.Append($"Process monitor startup error: {exception.Message}", LogCategory.Error);
        }
    }

    /// <summary>记录检测时间与估计的最后输入时间，两者之间通常包含整个空闲阈值。</summary>
    private void OnUserIdle(object? sender, UserIdleEvent eventData)
    {
        _persistence?.TryRecord(eventData);
        _patterns?.Record(eventData);
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => _logStore.Append(
            $"[{eventData.DetectedAt:HH:mm:ss}] UserIdle: 最后输入 {eventData.IdleStartedAt:yyyy-MM-dd HH:mm:ss}",
            LogCategory.Monitor));
    }

    /// <summary>显示完整无输入时长，而不是从 UserIdle 事件发出之后才开始计时。</summary>
    private void OnUserReturned(object? sender, UserReturnedEvent eventData)
    {
        _persistence?.TryRecord(eventData);
        _patterns?.Record(eventData);
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => _logStore.Append(
            $"[{eventData.DetectedAt:HH:mm:ss}] UserReturned: 返回 {eventData.ReturnedAt:HH:mm:ss}，IdleDuration={eventData.IdleDuration:c}",
            LogCategory.Monitor));
    }

    /// <summary>同步消费前台事件以保留退出前的状态；日志适配器只排队更新显示，不访问控件。</summary>
    private void OnForegroundChanged(object? sender, ForegroundChangedEvent eventData)
    {
        _sessionLog?.Record(eventData);
        _patterns?.Record(eventData);
    }

    /// <summary>只记录进程组变化；初始化/故障恢复标记明确说明观察缺口，不把它当作应用启动。</summary>
    private void OnProcessChanged(object? sender, IMonitorEvent eventData)
    {
        _persistence?.TryRecord(eventData);
        _patterns?.Record(eventData);
        var text = eventData switch
        {
            AppStartedEvent started => $"AppStarted: {started.ProcessName}（快照发现进程组出现）",
            AppClosedEvent closed => $"AppClosed: {closed.ProcessName}（快照发现同名进程全部消失）",
            _ => "ProcessObservationReset: 重建基线，重开/启动次数重新观察"
        };
        _logStore.Append($"[{eventData.Timestamp:HH:mm:ss}] {text}", LogCategory.Monitor);
    }

    /// <summary>可恢复的监测错误写入开发日志，不向用户弹出打扰性通知。</summary>
    private void OnMonitorError(object? sender, MonitorErrorEventArgs error)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _logStore.Append(
                $"[{DateTimeOffset.Now:HH:mm:ss}] Monitor error: {error.Operation} - {error.Exception.Message}",
                LogCategory.Error);
        });
    }

    /// <summary>在 UI 调度中创建菜单；保持原生托盘消息回调简短。</summary>
    private void OnTrayContextMenuRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var menu = new System.Windows.Controls.ContextMenu();
            var narrationItem = new System.Windows.Controls.MenuItem
            {
                Header = _narrationEnabled ? "暂停旁白" : "开启旁白"
            };
            narrationItem.Click += (_, _) =>
            {
                _narrationEnabled = !_narrationEnabled;
                _logStore.Append(
                    $"[{DateTimeOffset.Now:HH:mm:ss}] 旁白开关：{(_narrationEnabled ? "开启" : "暂停")}（旁白管线尚未接入）",
                    LogCategory.System);
            };

            var logItem = new System.Windows.Controls.MenuItem { Header = "打开 Developer Log" };
            logItem.Click += (_, _) => ShowDeveloperLog();

            var settingsItem = new System.Windows.Controls.MenuItem
            {
                Header = "设置（下一阶段）",
                IsEnabled = false
            };

            var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
            exitItem.Click += (_, _) => Shutdown();

            menu.Items.Add(narrationItem);
            menu.Items.Add(logItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(exitItem);
            menu.IsOpen = true;
        });
    }

    /// <summary>只保留一个日志窗口；关闭后清空引用，下一次打开重新创建界面。</summary>
    private void ShowDeveloperLog()
    {
        if (_developerLogWindow is null)
        {
            _developerLogWindow = new DeveloperLogWindow(_logStore, _historyQueries);
            _developerLogWindow.Closed += (_, _) => _developerLogWindow = null;
        }

        _developerLogWindow.Show();
        _developerLogWindow.Activate();
    }

    /// <summary>持久化状态不走数据库本身，避免数据库故障引发递归记录；后台线程不碰 UI。</summary>
    private void OnPersistenceStatus(object? sender, PersistenceStatus status)
    {
        _logStore.SetPersistenceStatus(status.Message);
        if (!status.Healthy)
        {
            _logStore.Append(status.Message, LogCategory.Error);
            System.Diagnostics.Trace.TraceError(status.Message);
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 出错的 UI 可能就是日志列表；再向同一个绑定集合写错误会造成递归故障。
        // 将完整诊断留在 VS 输出中，不把未知异常一律标记为已处理来掩盖问题。
        System.Diagnostics.Trace.TraceError($"UI error: {e.Exception}");
    }

    /// <summary>进程级未处理异常的最后诊断出口；此时不能依赖 UI 调度队列继续工作。</summary>
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Unhandled error: {exception}");
        }
    }
}
