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

    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesDataProperty =
        AvaloniaProperty.Register<TrafficChartControl, IReadOnlyList<ChartSeries>?>(nameof(SeriesData));

    public IReadOnlyList<ChartSeries>? SeriesData {
        get => GetValue(SeriesDataProperty);
        set => SetValue(SeriesDataProperty, value);
    }

    static TrafficChartControl() {
        AffectsRender<TrafficChartControl>(SeriesDataProperty);
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

        var gridlinePen = new Pen(ResolveBrush("ChartGridline"), 1);
        var axisLabelBrush = ResolveBrush("ChartAxisLabel");

        var series = SeriesData;
        if (series is null || series.Count == 0) {
            DrawAxes(context, chartLeft, chartTop, chartRight, chartBottom, gridlinePen);
            return;
        }

        // Find the maximum sample count across all series
        var maxSamples = 0;
        foreach (var s in series) {
            if (s.Values.Count > maxSamples) maxSamples = s.Values.Count;
        }
        if (maxSamples == 0) {
            DrawAxes(context, chartLeft, chartTop, chartRight, chartBottom, gridlinePen);
            return;
        }

        // Compute per-sample peak across overlaid series (not sum — they're not stacked)
        var peak = new double[maxSamples];
        foreach (var s in series) {
            for (var i = 0; i < s.Values.Count; i++) {
                if (s.Values[i] > peak[i]) peak[i] = s.Values[i];
            }
        }

        var maxValue = 0.0;
        foreach (var v in peak) {
            if (v > maxValue) maxValue = v;
        }
        if (maxValue < 1) maxValue = 1;

        // Round up to a nice value for Y axis
        maxValue = NiceMax(maxValue);

        // Draw gridlines and Y axis labels
        DrawGridlines(context, chartLeft, chartTop, chartRight, chartBottom,
            chartHeight, maxValue, gridlinePen, axisLabelBrush);

        // Draw X axis labels (time)
        DrawTimeLabels(context, chartLeft, chartBottom, chartWidth,
            maxSamples, axisLabelBrush);

        // Draw overlaid area series
        DrawAreas(context, series, chartLeft, chartTop, chartWidth, chartHeight,
            maxSamples, maxValue);

        // Draw border
        DrawAxes(context, chartLeft, chartTop, chartRight, chartBottom, gridlinePen);
    }

    private static void DrawAxes(DrawingContext context, double left, double top,
        double right, double bottom, Pen pen) {
        context.DrawLine(pen, new Point(left, top), new Point(left, bottom));
        context.DrawLine(pen, new Point(left, bottom), new Point(right, bottom));
    }

    private static void DrawGridlines(DrawingContext context, double left, double top,
        double right, double bottom, double height, double maxValue,
        Pen pen, IBrush labelBrush) {
        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal);
        for (var i = 0; i <= MaxYTicks; i++) {
            var ratio = i / (double)MaxYTicks;
            var y = bottom - ratio * height;
            if (i > 0)
                context.DrawLine(pen, new Point(left, y), new Point(right, y));

            var value = (long)(ratio * maxValue);
            var label = ByteFormatter.FormatRate(value);
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, labelBrush);
            context.DrawText(text, new Point(left - text.Width - 6, y - text.Height / 2));
        }
    }

    private static void DrawTimeLabels(DrawingContext context, double left, double bottom,
        double width, int sampleCount, IBrush labelBrush) {
        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal);
        var tickCount = Math.Min(MaxXTicks, sampleCount);
        if (tickCount < 2) return;

        for (var i = 0; i < tickCount; i++) {
            var ratio = i / (double)(tickCount - 1);
            var sampleIndex = (int)(ratio * (sampleCount - 1));
            var secondsAgo = sampleCount - 1 - sampleIndex;
            var label = secondsAgo == 0 ? "now" : $"-{secondsAgo / 60}:{secondsAgo % 60:D2}";
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, labelBrush);
            var x = left + ratio * width - text.Width / 2;
            context.DrawText(text, new Point(x, bottom + 6));
        }
    }

    private static void DrawAreas(DrawingContext context, IReadOnlyList<ChartSeries> seriesList,
        double left, double top, double width, double height,
        int maxSamples, double maxValue) {
        var baselineY = top + height;

        foreach (var series in seriesList) {
            var values = series.Values;
            if (values.Count == 0) continue;

            // Build chart-space points for this series. Right-align: skip leading zeros
            // implicitly by using the same x-step as maxSamples.
            var offset = maxSamples - values.Count;
            var points = new Point[values.Count];
            for (var i = 0; i < values.Count; i++) {
                var sampleIndex = offset + i;
                var x = left + (sampleIndex / (double)(maxSamples - 1)) * width;
                var y = top + height - (values[i] / maxValue) * height;
                points[i] = new Point(x, y);
            }

            // Fill
            var fillGeometry = new StreamGeometry();
            using (var ctx = fillGeometry.Open()) {
                FillSmoothArea(ctx, points, baselineY);
            }
            var fillColor = series.Color;
            var fillBrush = new SolidColorBrush(Color.FromArgb(77, fillColor.R, fillColor.G, fillColor.B));
            context.DrawGeometry(fillBrush, null, fillGeometry);

            // Stroke (top edge only)
            var strokeGeometry = new StreamGeometry();
            using (var ctx = strokeGeometry.Open()) {
                StrokeSmoothPath(ctx, points);
            }
            var strokeBrush = new SolidColorBrush(fillColor);
            context.DrawGeometry(null, new Pen(strokeBrush, 1.5), strokeGeometry);
        }
    }

    /// <summary>
    /// Strokes a Catmull-Rom spline through the given points (converted to cubic Beziers).
    /// Falls back to straight lines when there are fewer than 3 points.
    /// </summary>
    private static void StrokeSmoothPath(StreamGeometryContext ctx, Point[] points) {
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
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ctx.CubicBezierTo(c1, c2, p2);
        }
        ctx.EndFigure(false);
    }

    /// <summary>
    /// Builds a closed filled area below a Catmull-Rom spline, anchored to the baseline.
    /// </summary>
    private static void FillSmoothArea(StreamGeometryContext ctx, Point[] points, double baselineY) {
        if (points.Length == 0) return;

        ctx.BeginFigure(new Point(points[0].X, baselineY), true);
        ctx.LineTo(points[0]);

        if (points.Length >= 3) {
            for (var i = 0; i < points.Length - 1; i++) {
                var p0 = i == 0 ? points[0] : points[i - 1];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = i + 2 < points.Length ? points[i + 2] : points[i + 1];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                ctx.CubicBezierTo(c1, c2, p2);
            }
        } else if (points.Length == 2) {
            ctx.LineTo(points[1]);
        }

        ctx.LineTo(new Point(points[^1].X, baselineY));
        ctx.EndFigure(true);
    }

    private static IBrush ResolveBrush(string tokenName) {
        var app = Avalonia.Application.Current;
        if (app is not null && ResourceNodeExtensions.TryFindResource(app, tokenName, out var res)
            && res is ISolidColorBrush brush)
            return brush;
        return Brushes.Gray;
    }

    private static double NiceMax(double value) {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        double nice;
        if (normalized <= 1) nice = 1;
        else if (normalized <= 2) nice = 2;
        else if (normalized <= 5) nice = 5;
        else nice = 10;
        return nice * magnitude;
    }
}

/// <summary>
/// A single data series for the traffic chart.
/// </summary>
internal sealed record ChartSeries(string Name, IReadOnlyList<long> Values, Color Color);
