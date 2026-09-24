using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CraftStats;

/// <summary>
/// Dependency-free line chart of daily uptime. Renders axis, area fill, polyline and hover
/// highlight directly in OnRender with theme-aware brushes (Fluent tokens with neutral fallbacks).
/// </summary>
public sealed class DailyUptimeChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<DailyUptimePoint>),
        typeof(DailyUptimeChart),
        new FrameworkPropertyMetadata(Array.Empty<DailyUptimePoint>(), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface TextTypeface = new(new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly List<Point> _pointCenters = new();
    private int _hoverIndex = -1;

    public DailyUptimeChart()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        MouseMove += OnChartMouseMove;
        MouseLeave += (_, _) => SetHoverIndex(-1);
    }

    public IReadOnlyList<DailyUptimePoint> Points
    {
        get => (IReadOnlyList<DailyUptimePoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    private void OnChartMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        var nearest = -1;
        var nearestDistance = 26.0;
        for (var i = 0; i < _pointCenters.Count; i++)
        {
            var dx = _pointCenters[i].X - position.X;
            var dy = _pointCenters[i].Y - position.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = i;
            }
        }
        SetHoverIndex(nearest);
    }

    private void SetHoverIndex(int index)
    {
        if (_hoverIndex == index) return;
        _hoverIndex = index;
        UpdateToolTip();
        InvalidateVisual();
    }

    private void UpdateToolTip()
    {
        var points = Points;
        if (_hoverIndex < 0 || points is not { Count: > 0 } || _hoverIndex >= points.Count)
        {
            ToolTip = null;
            return;
        }

        var point = points[_hoverIndex];
        ToolTip = new ToolTip
        {
            Content = new TextBlock
            {
                Inlines =
                {
                    new Bold(new Run(point.Date.ToString("yyyy/M/d ddd"))),
                    new Run(Environment.NewLine + "开机时长：" + DurationFormat.Format(point.Duration))
                }
            }
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points;
        _pointCenters.Clear();

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var textBrush = GetBrush("TextFillColorSecondaryBrush", Color.FromRgb(0x80, 0x80, 0x80));
        var accentBrush = GetBrush("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x78, 0xD4));

        if (points is not { Count: > 0 } || points.All(p => p.Duration <= TimeSpan.Zero))
        {
            var empty = CreateText("暂无开机时长数据", 12, textBrush, dpi);
            dc.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
            return;
        }

        const double leftMargin = 46.0, rightMargin = 14.0, topMargin = 14.0, bottomMargin = 26.0;
        if (width <= leftMargin + rightMargin + 40 || height <= topMargin + bottomMargin + 20) return;
        var plot = new Rect(leftMargin, topMargin, width - leftMargin - rightMargin, height - topMargin - bottomMargin);

        var count = points.Count;
        var maxMinutes = (long)Math.Ceiling(points.Max(p => p.Duration.TotalMinutes));
        var stepMinutes = PickStepMinutes(maxMinutes);
        var topMinutes = stepMinutes * 4;

        double X(int i) => count == 1 ? plot.Left + plot.Width / 2 : plot.Left + plot.Width * i / (count - 1);
        double Y(TimeSpan duration)
        {
            var fraction = topMinutes <= 0 ? 0 : duration.TotalMinutes / topMinutes;
            return plot.Bottom - plot.Height * Math.Clamp(fraction, 0, 1);
        }

        dc.PushOpacity(0.35);
        var gridPen = new Pen(textBrush, 1);
        for (var i = 1; i <= 4; i++)
        {
            var y = Math.Round(plot.Bottom - plot.Height * i / 4.0) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        dc.Pop();

        for (var i = 0; i <= 4; i++)
        {
            var y = Math.Round(plot.Bottom - plot.Height * i / 4.0) + 0.5;
            var label = CreateText(FormatAxisMinutes(topMinutes * i / 4), 10, textBrush, dpi);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }

        var bottomY = Math.Round(plot.Bottom) + 0.5;
        dc.PushOpacity(0.6);
        dc.DrawLine(new Pen(textBrush, 1), new Point(plot.Left, bottomY), new Point(plot.Right, bottomY));
        dc.Pop();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(X(0), plot.Bottom), true, false);
            for (var i = 0; i < count; i++)
                ctx.LineTo(new Point(X(i), Y(points[i].Duration)), false, false);
            ctx.LineTo(new Point(X(count - 1), plot.Bottom), false, false);
            ctx.Close();
        }
        area.Freeze();
        dc.PushOpacity(0.14);
        dc.DrawGeometry(accentBrush, null, area);
        dc.Pop();

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(0), Y(points[0].Duration)), false, false);
            for (var i = 1; i < count; i++)
                ctx.LineTo(new Point(X(i), Y(points[i].Duration)), true, false);
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(accentBrush, 2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, line);

        for (var i = 0; i < count; i++)
        {
            var center = new Point(X(i), Y(points[i].Duration));
            _pointCenters.Add(center);
            dc.DrawEllipse(accentBrush, null, center, 2.6, 2.6);
        }

        var labelStep = Math.Max(1, (int)Math.Ceiling(count / 8.0));
        var lastLabelRight = double.MinValue;
        for (var i = 0; i < count; i++)
        {
            if (i % labelStep != 0) continue;
            var date = points[i].Date;
            var label = CreateText($"{date.Month}/{date.Day}", 10, textBrush, dpi);
            var x = Math.Min(Math.Max(X(i) - label.Width / 2, plot.Left - 12), plot.Right - label.Width);
            if (x < lastLabelRight + 6) continue;
            dc.DrawText(label, new Point(x, plot.Bottom + 6));
            lastLabelRight = x + label.Width;
        }

        if (_hoverIndex >= 0 && _hoverIndex < count)
        {
            var center = _pointCenters[_hoverIndex];
            var guidePen = new Pen(textBrush, 1) { DashStyle = DashStyles.Dash };
            dc.PushOpacity(0.55);
            dc.DrawLine(guidePen, new Point(center.X, plot.Top), new Point(center.X, plot.Bottom));
            dc.Pop();
            dc.DrawEllipse(accentBrush, new Pen(textBrush, 1.5), center, 4.2, 4.2);
        }
    }

    // choose the largest axis step (in minutes) whose 4x top fits the data with headroom,
    // so gridline labels stay whole minutes or whole/half hours
    private static long PickStepMinutes(long maxMinutes)
    {
        if (maxMinutes <= 0) maxMinutes = 1;
        long[] steps = { 5, 10, 15, 30, 60, 120, 180, 240, 360, 480, 720, 1440, 2880, 5760 };
        foreach (var step in steps)
        {
            if (step * 4 >= maxMinutes * 1.02)
                return step;
        }
        return steps[^1];
    }

    private static string FormatAxisMinutes(long minutes)
        => minutes >= 60 ? $"{minutes / 60.0:0.#}h" : $"{minutes}m";

    private FormattedText CreateText(string text, double size, Brush brush, double pixelsPerDip)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextTypeface, size, brush, pixelsPerDip);

    private Brush GetBrush(string resourceKey, Color fallback)
    {
        if (TryFindResource(resourceKey) is Brush brush)
            return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
