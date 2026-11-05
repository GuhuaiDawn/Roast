using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNarrator.Memory;

namespace DesktopNarrator.App;

/// <summary>一个扇区及其图例；Share 范围为 0–1，Seconds 为当日已保存前台秒数。</summary>
public sealed record AppUsageSlice(string Label, double Seconds, double Share, string Color, string Details)
{
    public string DurationText => ActivityPresentation.Duration(Seconds);
    public string ShareText => $"{Share * 100:0.#}%";
}

/// <summary>用完整当日总计生成分布；最多六个独立应用，其余合并，不丢弃小项时长。</summary>
public static class ApplicationDistribution
{
    private static readonly string[] Colors = ["#B6A5FF", "#72D8BD", "#78B8F5", "#F0C879", "#EC9FBE", "#A7C77D", "#78869D"];

    /// <summary>过滤无效/零时长，仅用于显示，不改写原始历史；全零或无效总量返回空分布。</summary>
    public static IReadOnlyList<AppUsageSlice> Build(IReadOnlyList<DailyAppTotal> totals)
    {
        var ordered = totals.Where(x => double.IsFinite(x.TotalForegroundSeconds) && x.TotalForegroundSeconds > 0)
            .OrderByDescending(x => x.TotalForegroundSeconds).ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase).ToArray();
        var total = ordered.Sum(x => x.TotalForegroundSeconds);
        if (total <= 0 || !double.IsFinite(total)) return [];
        var slices = new List<AppUsageSlice>();
        for (var i = 0; i < Math.Min(6, ordered.Length); i++)
        {
            var app = ordered[i];
            Add(ActivityPresentation.AppName(app.ProcessName, app.FriendlyName), app.TotalForegroundSeconds, Colors[i],
                $"进程：{app.ProcessName}");
        }
        if (ordered.Length > 6)
        {
            var others = ordered.Skip(6).ToArray();
            Add($"其他（{others.Length} 个应用）", others.Sum(x => x.TotalForegroundSeconds), Colors[6],
                string.Join("\n", others.Select(x => $"{ActivityPresentation.AppName(x.ProcessName, x.FriendlyName)}：{ActivityPresentation.Duration(x.TotalForegroundSeconds)}")));
        }
        return slices.AsReadOnly();

        void Add(string label, double seconds, string color, string details) => slices.Add(new(label, seconds, seconds / total, color,
            $"{label}\n{ActivityPresentation.Duration(seconds)} · {seconds / total:P1}\n{details}"));
    }
}

/// <summary>纯 WPF 矢量扇形图；只绘制展示快照，不采样系统、不访问数据库。图例同时提供文本信息。</summary>
public sealed class ApplicationPieChart : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(nameof(Slices),
        typeof(IReadOnlyList<AppUsageSlice>), typeof(ApplicationPieChart),
        new FrameworkPropertyMetadata(Array.Empty<AppUsageSlice>(), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>绑定后整体替换的只读快照；图例和图形使用同一份扇区，避免占比与颜色错位。</summary>
    public IReadOnlyList<AppUsageSlice> Slices
    {
        get => (IReadOnlyList<AppUsageSlice>)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    /// <summary>圆形面积按当日前台时长分配；无数据时只显示轮廓，不制造默认比例。</summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 3);
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (Slices.Count == 0)
        {
            drawingContext.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(65, 78, 98)), 2), center, radius, radius);
            var label = new FormattedText("暂无时长", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 14, new SolidColorBrush(Color.FromRgb(175, 189, 207)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
            return;
        }
        var start = -90d;
        foreach (var slice in Slices)
        {
            var geometry = CreateSector(center, radius, start, slice.Share);
            var fill = (Brush)new BrushConverter().ConvertFromString(slice.Color)!;
            drawingContext.DrawGeometry(fill, new Pen(new SolidColorBrush(Color.FromRgb(30, 38, 50)), 1), geometry);
            start += slice.Share * 360;
        }
    }

    /// <summary>单应用占满时使用整圆；避免首尾重合的 360° 圆弧被 WPF 解释成空路径。</summary>
    public static Geometry CreateSector(Point center, double radius, double startDegrees, double share)
    {
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(startDegrees) || !double.IsFinite(share) || share <= 0 || share > 1)
            return Geometry.Empty;
        if (share == 1) return new EllipseGeometry(center, radius, radius);
        var geometry = new StreamGeometry();
        Point At(double degrees) => new(center.X + radius * Math.Cos(degrees * Math.PI / 180), center.Y + radius * Math.Sin(degrees * Math.PI / 180));
        using (var context = geometry.Open())
        {
            context.BeginFigure(center, isFilled: true, isClosed: true);
            context.LineTo(At(startDegrees), isStroked: true, isSmoothJoin: false);
            context.ArcTo(At(startDegrees + share * 360), new Size(radius, radius), 0, share > .5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>悬停实际扇区查看对应应用；扇区外清除提示，键盘用户可阅读下方同内容图例。</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this) - new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 3;
        ToolTip = null;
        if (radius <= 0 || point.Length > radius) return;
        var angle = (Math.Atan2(point.Y, point.X) * 180 / Math.PI + 450) % 360;
        var end = 0d;
        foreach (var slice in Slices)
        {
            end += slice.Share * 360;
            if (angle < end) { ToolTip = slice.Details; break; }
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); ToolTip = null; }
}
