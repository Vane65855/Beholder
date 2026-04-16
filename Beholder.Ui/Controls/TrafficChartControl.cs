using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Beholder.Ui.Helpers;

namespace Beholder.Ui.Controls;

/// <summary>
/// Custom-drawn overlaid area chart for the Traffic tab.
/// Renders directly via Avalonia's <see cref="DrawingContext"/> — no third-party chart library.
/// Per ARCHITECTURE.md: "The traffic graph is a custom Canvas-drawn control."
/// Series are overlaid (not stacked) so download + upload can share the same 0 baseline.
/// </summary>
internal sealed class TrafficChartControl : Control {
    private const double LeftMargin = 60;
    private const double BottomMargin = 28;
    private const double TopMargin = 12;
    private const double RightMargin = 12;
    private const int MaxYTicks = 5;
    private const int MaxXTicks = 6;

    // Typeface is content-independent. Cached statically so FormattedText
    // construction at each tick avoids the FontManager resolution inside.
    private static readonly Typeface s_axisLabelTypeface = new(
        "Segoe UI", FontStyle.Normal, FontWeight.Normal);

    // Theme-resolved brushes/pens. Null until first use; cleared on
    // ResourcesChanged so a future theme swap re-resolves on the next render.
    private IBrush? _gridlineBrush;
    private IBrush? _axisLabelBrush;
    private Pen? _gridlinePen;

    private IBrush GridlineBrush => _gridlineBrush ??= ResolveBrush("ChartGridline");
    private IBrush AxisLabelBrush => _axisLabelBrush ??= ResolveBrush("ChartAxisLabel");
    private Pen GridlinePen => _gridlinePen ??= new Pen(GridlineBrush, 1);

    // Per-series-color cache. Practical cardinality is 2 (Download teal,
    // Upload purple); bounded by the 12-entry series palette if future phases
    // introduce per-process colors.
    private readonly Dictionary<Color, SeriesResources> _seriesCache = new();

    // Reusable geometry buffers. Grown on demand; never shrunk. Shared across
    // series within a single render — safe because each series's span view
    // is bounded to that series's values.Count.
    private Point[] _pointsBuffer = [];
    private double[] _peakBuffer = [];

    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesDataProperty =
        AvaloniaProperty.Register<TrafficChartControl, IReadOnlyList<ChartSeries>?>(nameof(SeriesData));

    /// <summary>
    /// Total wall-clock duration the chart data represents. Used by <see cref="DrawTimeLabels"/>
    /// to compute correct labels. Defaults to null, which assumes 1 sample = 1 second
    /// (matching the 5-minute live mode). For historical queries, set this to the
    /// queried range's total span (e.g., 24 hours for a "Last 24 Hours" view).
    /// </summary>
    public static readonly StyledProperty<TimeSpan?> DataSpanProperty =
        AvaloniaProperty.Register<TrafficChartControl, TimeSpan?>(nameof(DataSpan));

    public IReadOnlyList<ChartSeries>? SeriesData {
        get => GetValue(SeriesDataProperty);
        set => SetValue(SeriesDataProperty, value);
    }

    public TimeSpan? DataSpan {
        get => GetValue(DataSpanProperty);
        set => SetValue(DataSpanProperty, value);
    }

    static TrafficChartControl() {
        AffectsRender<TrafficChartControl>(SeriesDataProperty, DataSpanProperty);
    }

    public override void Render(DrawingContext context) {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width < 1 || bounds.Height < 1) return;

        var chartLeft = LeftMargin;
        var chartTop = TopMargin;
        var chartRight = bounds.Width - RightMargin;
        var chartBottom = bounds.Height - BottomMargin;
        var chartWidth = chartRight - chartLeft;
        var chartHeight = chartBottom - chartTop;

        if (chartWidth < 10 || chartHeight < 10) return;

        var series = SeriesData;
        if (series is null || series.Count == 0) {
            DrawAxes(context, chartLeft, chartTop, chartRight, chartBottom, GridlinePen);
            return;
        }

        // Find the maximum sample count across all series
        var maxSamples = 0;
        foreach (var s in series) {
            if (s.Values.Count > maxSamples) maxSamples = s.Values.Count;
        }
        if (maxSamples < 2) {
            // A single sample produces NaN x-coordinates in DrawAreas' sample-index
            // math (0 / (1 − 1) = NaN), which then corrupts the StreamGeometry passed
            // to DrawGeometry. Upstream callers pad single-point responses to a spike
            // array (see TrafficTabViewModel.LoadHistoricalRangeAsync), but this guard
            // is the defense-in-depth safety net for any path that bypasses padding.
            // Consistent with DrawTimeLabels' own tickCount < 2 early-return.
            DrawAxes(context, chartLeft, chartTop, chartRight, chartBottom, GridlinePen);
            return;
        }

        // Compute per-sample peak across overlaid series (not sum — they're not stacked).
        // _peakBuffer is reused across renders; grow if the series widened, and clear
        // the active slice because a reused buffer may hold stale values.
        if (_peakBuffer.Length < maxSamples) _peakBuffer = new double[maxSamples];
        Array.Clear(_peakBuffer, 0, maxSamples);
        foreach (var s in series) {
            for (var i = 0; i < s.Values.Count; i++) {
                if (s.Values[i] > _peakBuffer[i]) _peakBuffer[i] = s.Values[i];
            }
        }

        var maxValue = 0.0;
        for (var i = 0; i < maxSamples; i++) {
            if (_peakBuffer[i] > maxValue) maxValue = _peakBuffer[i];
        }
        if (maxValue < 1) maxValue = 1;

        // Round up to a nice value for Y axis
        maxValue = NiceMax(maxValue);

        // Draw gridlines and Y axis labels
        DrawGridlines(context, chartLeft, chartTop, chartRight, chartBottom,
            chartHeight, maxValue, GridlinePen, AxisLabelBrush);

        // Draw X axis labels (time)
        DrawTimeLabels(context, chartLeft, chartBottom, chartWidth,
            maxSamples, DataSpan, AxisLabelBrush);

        // Draw overlaid area series
        DrawAreas(context, series, chartLeft, chartTop, chartWidth, chartHeight,
            maxSamples, maxValue);

        // Draw border
        DrawAxes(context, chartLeft, chartTop, chartRight, chartBottom, GridlinePen);
    }

    private static void DrawAxes(DrawingContext context, double left, double top,
        double right, double bottom, Pen pen) {
        context.DrawLine(pen, new Point(left, top), new Point(left, bottom));
        context.DrawLine(pen, new Point(left, bottom), new Point(right, bottom));
    }

    private static void DrawGridlines(DrawingContext context, double left, double top,
        double right, double bottom, double height, double maxValue,
        Pen pen, IBrush labelBrush) {
        for (var i = 0; i <= MaxYTicks; i++) {
            var ratio = i / (double)MaxYTicks;
            var y = bottom - ratio * height;
            if (i > 0)
                context.DrawLine(pen, new Point(left, y), new Point(right, y));

            var value = (long)(ratio * maxValue);
            var label = ByteFormatter.FormatRate(value);
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, s_axisLabelTypeface, 10, labelBrush);
            context.DrawText(text, new Point(left - text.Width - 6, y - text.Height / 2));
        }
    }

    private static void DrawTimeLabels(DrawingContext context, double left, double bottom,
        double width, int sampleCount, TimeSpan? dataSpan, IBrush labelBrush) {
        var tickCount = Math.Min(MaxXTicks, sampleCount);
        if (tickCount < 2) return;

        // Seconds per sample: if DataSpan is set, use it; otherwise assume
        // 1 sample = 1 second (live 5-minute mode).
        var totalSeconds = dataSpan?.TotalSeconds ?? sampleCount;

        for (var i = 0; i < tickCount; i++) {
            var ratio = i / (double)(tickCount - 1);
            var secondsAgo = (1.0 - ratio) * totalSeconds;
            var label = FormatTimeLabel(secondsAgo, totalSeconds);
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, s_axisLabelTypeface, 10, labelBrush);
            var x = left + ratio * width - text.Width / 2;
            context.DrawText(text, new Point(x, bottom + 6));
        }
    }

    private static string FormatTimeLabel(double secondsAgo, double totalSeconds) {
        if (secondsAgo < 1) return "now";

        // Adapt formatting to the total chart span
        if (totalSeconds <= 600) {
            // ≤ 10 min: "-M:SS"
            var m = (int)(secondsAgo / 60);
            var s = (int)(secondsAgo % 60);
            return $"-{m}:{s:D2}";
        }
        if (totalSeconds <= 86400) {
            // ≤ 24 hours: "-Hh Mm" or "-H:MM"
            var h = (int)(secondsAgo / 3600);
            var m = (int)(secondsAgo % 3600 / 60);
            if (h == 0) return $"-{m}m";
            if (m == 0) return $"-{h}h";
            return $"-{h}h{m:D2}m";
        }
        // > 24 hours: "-Nd" or "-Nd Hh"
        var days = (int)(secondsAgo / 86400);
        var hours = (int)(secondsAgo % 86400 / 3600);
        if (hours == 0) return $"-{days}d";
        return $"-{days}d {hours}h";
    }

    private void DrawAreas(DrawingContext context, IReadOnlyList<ChartSeries> seriesList,
        double left, double top, double width, double height,
        int maxSamples, double maxValue) {
        var baselineY = top + height;

        // Reusable points buffer, shared across series within this render.
        // Grow on demand; maxSamples is the upper bound across all series.
        if (_pointsBuffer.Length < maxSamples) _pointsBuffer = new Point[maxSamples];

        foreach (var series in seriesList) {
            var values = series.Values;
            if (values.Count == 0) continue;

            // Build chart-space points for this series. Right-align: skip leading zeros
            // implicitly by using the same x-step as maxSamples.
            var offset = maxSamples - values.Count;
            for (var i = 0; i < values.Count; i++) {
                var sampleIndex = offset + i;
                var x = left + (sampleIndex / (double)(maxSamples - 1)) * width;
                var y = top + height - (values[i] / maxValue) * height;
                _pointsBuffer[i] = new Point(x, y);
            }
            var points = new ReadOnlySpan<Point>(_pointsBuffer, 0, values.Count);

            var resources = GetSeriesResources(series.Color);

            // Fill
            var fillGeometry = new StreamGeometry();
            using (var ctx = fillGeometry.Open()) {
                FillSmoothArea(ctx, points, top, baselineY);
            }
            context.DrawGeometry(resources.Fill, null, fillGeometry);

            // Stroke (top edge only)
            var strokeGeometry = new StreamGeometry();
            using (var ctx = strokeGeometry.Open()) {
                StrokeSmoothPath(ctx, points, top, baselineY);
            }
            context.DrawGeometry(null, resources.Pen, strokeGeometry);
        }
    }

    /// <summary>
    /// Strokes a Catmull-Rom spline through the given points (converted to cubic Beziers).
    /// Falls back to straight lines when there are fewer than 3 points. Control point Y
    /// values are clamped to <c>[top, baselineY]</c> to prevent the spline from
    /// overshooting the data envelope — see <see cref="ClampY"/>.
    /// </summary>
    private static void StrokeSmoothPath(StreamGeometryContext ctx, ReadOnlySpan<Point> points,
        double top, double baselineY) {
        if (points.Length == 0) return;
        ctx.BeginFigure(points[0], false);
        if (points.Length == 1) {
            ctx.EndFigure(false);
            return;
        }
        if (points.Length == 2) {
            ctx.LineTo(points[1]);
            ctx.EndFigure(false);
            return;
        }

        for (var i = 0; i < points.Length - 1; i++) {
            var p0 = i == 0 ? points[0] : points[i - 1];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i + 2 < points.Length ? points[i + 2] : points[i + 1];
            var c1 = new Point(
                p1.X + (p2.X - p0.X) / 6,
                ClampY(p1.Y + (p2.Y - p0.Y) / 6, top, baselineY));
            var c2 = new Point(
                p2.X - (p3.X - p1.X) / 6,
                ClampY(p2.Y - (p3.Y - p1.Y) / 6, top, baselineY));
            ctx.CubicBezierTo(c1, c2, p2);
        }
        ctx.EndFigure(false);
    }

    /// <summary>
    /// Builds a closed filled area below a Catmull-Rom spline, anchored to the baseline.
    /// Control point Y values are clamped to <c>[top, baselineY]</c> — see
    /// <see cref="ClampY"/>.
    /// </summary>
    private static void FillSmoothArea(StreamGeometryContext ctx, ReadOnlySpan<Point> points,
        double top, double baselineY) {
        if (points.Length == 0) return;

        ctx.BeginFigure(new Point(points[0].X, baselineY), true);
        ctx.LineTo(points[0]);

        if (points.Length >= 3) {
            for (var i = 0; i < points.Length - 1; i++) {
                var p0 = i == 0 ? points[0] : points[i - 1];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = i + 2 < points.Length ? points[i + 2] : points[i + 1];
                var c1 = new Point(
                    p1.X + (p2.X - p0.X) / 6,
                    ClampY(p1.Y + (p2.Y - p0.Y) / 6, top, baselineY));
                var c2 = new Point(
                    p2.X - (p3.X - p1.X) / 6,
                    ClampY(p2.Y - (p3.Y - p1.Y) / 6, top, baselineY));
                ctx.CubicBezierTo(c1, c2, p2);
            }
        } else if (points.Length == 2) {
            ctx.LineTo(points[1]);
        }

        ctx.LineTo(new Point(points[^1].X, baselineY));
        ctx.EndFigure(true);
    }

    /// <summary>
    /// Clamps a Y coordinate to the chart's drawable range. In screen space,
    /// <paramref name="top"/> is the smaller Y (visually higher — max traffic)
    /// and <paramref name="baseline"/> is the larger Y (visually lower — zero
    /// traffic). Used to prevent Catmull-Rom → Bezier control points from
    /// projecting past the data envelope on sharp transitions, which would
    /// otherwise cause the spline to dip below the 0 B/s baseline (or bulge
    /// above the chart maximum).
    /// </summary>
    private static double ClampY(double y, double top, double baseline) =>
        Math.Clamp(y, top, baseline);

    private static IBrush ResolveBrush(string tokenName) {
        var app = Avalonia.Application.Current;
        if (app is not null && ResourceNodeExtensions.TryFindResource(app, tokenName, out var res)
            && res is ISolidColorBrush brush)
            return brush;
        return Brushes.Gray;
    }

    private SeriesResources GetSeriesResources(Color color) {
        if (_seriesCache.TryGetValue(color, out var cached)) return cached;
        var stroke = new SolidColorBrush(color);
        var fillColor = Color.FromArgb(77, color.R, color.G, color.B);
        var fill = new SolidColorBrush(fillColor);
        var pen = new Pen(stroke, 1.5);
        cached = new SeriesResources(fill, stroke, pen);
        _seriesCache[color] = cached;
        return cached;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        ResourcesChanged += OnResourcesChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        ResourcesChanged -= OnResourcesChanged;
    }

    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e) {
        // Theme or resource dictionary changed — cached brushes/pens may now
        // reference stale colors. Clear and let the next Render re-resolve.
        _gridlineBrush = null;
        _axisLabelBrush = null;
        _gridlinePen = null;
        _seriesCache.Clear();
        InvalidateVisual();
    }

    private readonly record struct SeriesResources(IBrush Fill, IBrush Stroke, Pen Pen);

    /// <summary>
    /// Rounds <paramref name="value"/> UP to the nearest "nice" axis maximum.
    /// The nice set is {1, 1.5, 2, 3, 5, 7, 10} × 10^N. A finer-grained set
    /// than the classic {1, 2, 5, 10} prevents 2× Y-axis jumps when a peak
    /// bucket's value nudges across a 10^N boundary between queries — without
    /// the intermediate 1.5/3/7 steps, a tiny value drift from 9.9e9 to 1.0e10
    /// would flip the Y-axis from 1×10^10 to 2×10^10, which is visually jarring
    /// even though the underlying data barely changed.
    /// </summary>
    private static double NiceMax(double value) {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        double nice;
        if (normalized <= 1) nice = 1;
        else if (normalized <= 1.5) nice = 1.5;
        else if (normalized <= 2) nice = 2;
        else if (normalized <= 3) nice = 3;
        else if (normalized <= 5) nice = 5;
        else if (normalized <= 7) nice = 7;
        else nice = 10;
        return nice * magnitude;
    }
}

/// <summary>
/// A single data series for the traffic chart.
/// </summary>
internal sealed record ChartSeries(string Name, IReadOnlyList<long> Values, Color Color);
