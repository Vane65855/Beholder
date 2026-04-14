using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Beholder.Ui.Helpers;

namespace Beholder.Ui.Controls;

/// <summary>
/// Custom-drawn stacked area chart for the Traffic tab.
/// Renders directly via Avalonia's <see cref="DrawingContext"/> — no third-party chart library.
/// Per ARCHITECTURE.md: "The traffic graph is a custom Canvas-drawn control."
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

        // Find the maximum sample count and max stacked value
        var maxSamples = 0;
        foreach (var s in series) {
            if (s.Values.Count > maxSamples) maxSamples = s.Values.Count;
        }
        if (maxSamples == 0) return;

        // Compute stacked totals per sample
        var stacked = new double[maxSamples];
        foreach (var s in series) {
            for (var i = 0; i < s.Values.Count; i++)
                stacked[i] += s.Values[i];
        }

        var maxValue = 0.0;
        foreach (var v in stacked) {
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

        // Draw stacked area series (bottom to top)
        DrawStackedAreas(context, series, chartLeft, chartTop, chartWidth, chartHeight,
            maxSamples, maxValue);

        // Draw border
        DrawAxes(context, chartLeft, chartTop, chartRight, chartBottom, gridlinePen);
    }

    private void DrawAxes(DrawingContext context, double left, double top,
        double right, double bottom, Pen pen) {
        context.DrawLine(pen, new Point(left, top), new Point(left, bottom));
        context.DrawLine(pen, new Point(left, bottom), new Point(right, bottom));
    }

    private void DrawGridlines(DrawingContext context, double left, double top,
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

    private void DrawStackedAreas(DrawingContext context, IReadOnlyList<ChartSeries> seriesList,
        double left, double top, double width, double height,
        int maxSamples, double maxValue) {
        // Build cumulative stacks from bottom series to top
        var prevBaseline = new double[maxSamples];

        for (var s = seriesList.Count - 1; s >= 0; s--) {
            var series = seriesList[s];
            var values = series.Values;
            var currentTop = new double[maxSamples];

            for (var i = 0; i < maxSamples; i++) {
                var val = i < values.Count ? values[i] : 0;
                currentTop[i] = prevBaseline[i] + val;
            }

            // Build path for the area
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open()) {
                // Top line (left to right)
                var firstX = left;
                var firstY = top + height - (currentTop[0] / maxValue) * height;
                ctx.BeginFigure(new Point(firstX, firstY), true);

                for (var i = 1; i < maxSamples; i++) {
                    var x = left + (i / (double)(maxSamples - 1)) * width;
                    var y = top + height - (currentTop[i] / maxValue) * height;
                    ctx.LineTo(new Point(x, y));
                }

                // Bottom line (right to left, along baseline)
                for (var i = maxSamples - 1; i >= 0; i--) {
                    var x = left + (i / (double)(maxSamples - 1)) * width;
                    var y = top + height - (prevBaseline[i] / maxValue) * height;
                    ctx.LineTo(new Point(x, y));
                }

                ctx.EndFigure(true);
            }

            // Fill
            var fillColor = series.Color;
            var fillBrush = new SolidColorBrush(Color.FromArgb(77, fillColor.R, fillColor.G, fillColor.B));
            context.DrawGeometry(fillBrush, null, geometry);

            // Stroke
            var strokeGeometry = new StreamGeometry();
            using (var ctx = strokeGeometry.Open()) {
                var fx = left;
                var fy = top + height - (currentTop[0] / maxValue) * height;
                ctx.BeginFigure(new Point(fx, fy), false);
                for (var i = 1; i < maxSamples; i++) {
                    var x = left + (i / (double)(maxSamples - 1)) * width;
                    var y = top + height - (currentTop[i] / maxValue) * height;
                    ctx.LineTo(new Point(x, y));
                }
                ctx.EndFigure(false);
            }
            var strokeBrush = new SolidColorBrush(fillColor);
            context.DrawGeometry(null, new Pen(strokeBrush, 1.5), strokeGeometry);

            // This series becomes the baseline for the next
            Array.Copy(currentTop, prevBaseline, maxSamples);
        }
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
