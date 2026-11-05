using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DesktopNarrator.Events;
using DesktopNarrator.Memory;

namespace DesktopNarrator.App;

/// <summary>只处理展示：不修改原始记录，不将前台驻留解释为专注、工作或阅读。</summary>
public static class ActivityPresentation
{
    /// <summary>优先使用系统提供的友好名称；只有名称缺失时才使用少量已知别名。</summary>
    public static string AppName(string? process, string? friendly = null)
    {
        if (!string.IsNullOrWhiteSpace(friendly) && !string.Equals(process, friendly, StringComparison.OrdinalIgnoreCase))
            return friendly;
        return process?.ToLowerInvariant() switch
        {
            "devenv" => "Visual Studio",
            "code" => "Visual Studio Code",
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "firefox" => "Firefox",
            "explorer" => "文件资源管理器",
            "desktopnarrator.app" => "Desktop Narrator",
            _ => string.IsNullOrWhiteSpace(process) ? "未知应用" : process
        };
    }

    /// <summary>以中文单位显示完整秒数；不足一秒不冒充零，缺失或非法值不伪装成有效时长。</summary>
    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds) return "时长未知";
        if (seconds == 0) return "0 秒";
        if (seconds < 1) return "不足 1 秒";
        var whole = (long)Math.Floor(seconds);
        var parts = new List<string>();
        if (whole / 86400 > 0) parts.Add($"{whole / 86400} 天");
        if (whole / 3600 % 24 > 0) parts.Add($"{whole / 3600 % 24} 小时");
        if (whole / 60 % 60 > 0) parts.Add($"{whole / 60 % 60} 分钟");
        if (whole % 60 > 0) parts.Add($"{whole % 60} 秒");
        return string.Join(" ", parts);
    }

    /// <summary>时间线保留跨日/跨年信息；原始时区偏移和精确时长另外保留在提示中。</summary>
    public static string SessionRange(AppSession session)
    {
        if (session.EndTime is not { } end) return $"{session.StartTime:HH:mm:ss} → 进行中";
        var format = session.StartTime.Year != end.Year ? "yyyy-MM-dd HH:mm:ss" :
            session.StartTime.Date != end.Date ? "MM-dd HH:mm:ss" : "HH:mm:ss";
        return $"{session.StartTime.ToString(format)} → {end.ToString(format)}";
    }
}

/// <summary>所选日期的已保存汇总；分页只影响时间线，不影响这些卡片或应用占比。</summary>
public sealed record HistoryOverview(double TotalSeconds, int ApplicationCount, long SessionCount)
{
    public string DurationText => ActivityPresentation.Duration(TotalSeconds);
    /// <summary>使用完整当日汇总；翻页不能影响统计卡片或占比。</summary>
    public static HistoryOverview From(DayHistory day) => new(day.Totals.Sum(x => x.TotalForegroundSeconds), day.Totals.Count, day.SessionCount);
}

/// <summary>WPF 单向展示转换；业务模型保持无 WPF 依赖，原始值仍可查询和诊断。</summary>
public sealed class ActivityDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (parameter as string) switch
    {
        // 用名称徽标代替再次读取进程模块，避免仅为图标引入额外权限或采样。
        "Mark" when value is DailyAppTotal total => total.ProcessName.ToLowerInvariant() switch
        {
            "code" or "devenv" => "VS", "chrome" => "GC", "msedge" => "E", "qq" => "QQ",
            _ => StringInfo.GetNextTextElement(ActivityPresentation.AppName(total.ProcessName, total.FriendlyName)).ToUpperInvariant()
        },
        "App" => value switch
        {
            DailyAppTotal total => ActivityPresentation.AppName(total.ProcessName, total.FriendlyName),
            AppSession session => ActivityPresentation.AppName(session.ProcessName, session.FriendlyName),
            _ => ActivityPresentation.AppName(value as string)
        },
        "Range" when value is AppSession session => ActivityPresentation.SessionRange(session),
        "Details" when value is AppSession session => $"进程：{session.ProcessName}\n开始：{session.StartTime:O}\n结束：{session.EndTime:O}\n精确时长：{session.Duration:c}\n包含无输入时段；跨日记录显示整段时长。调时不会改变单调计时得到的时长。",
        "Duration" => value switch
        {
            TimeSpan duration => ActivityPresentation.Duration(duration.TotalSeconds),
            double seconds => ActivityPresentation.Duration(seconds),
            _ => "进行中"
        },
        _ => value
    };

    /// <summary>只用于只读界面，不允许格式化结果反向覆盖原始数据。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>应用时长占当日已保存总量的比例；零总量和绑定未就绪时不显示 NaN 或无穷大。</summary>
public sealed class UsageShareConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not double part || values[1] is not double total ||
            !double.IsFinite(part) || !double.IsFinite(total) || part < 0 || total <= 0) return "—";
        return (Math.Clamp(part / total * 100, 0, 100)).ToString("0.#", culture) + "%";
    }

    /// <summary>占比只是显示结果，不参与反向写入。</summary>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => DependencyProperty.UnsetValue).ToArray();
}
