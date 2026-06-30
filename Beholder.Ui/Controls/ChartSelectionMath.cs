using System;

namespace Beholder.Ui.Controls;

/// <summary>
/// Pure geometry for translating pointer X positions on the traffic chart into
/// normalized [0,1] selection fractions. Free of Avalonia types so the
/// click/drag math is unit-testable without a rendered control.
/// </summary>
internal static class ChartSelectionMath {
    /// <summary>Normalized [0,1] position of <paramref name="x"/> across the plot width.</summary>
    internal static double ToFraction(double x, double plotLeft, double plotWidth) =>
        plotWidth <= 0 ? 0 : Math.Clamp((x - plotLeft) / plotWidth, 0.0, 1.0);

    /// <summary>True when press and release are close enough to count as a click, not a drag.</summary>
    internal static bool IsClick(double startX, double endX, double thresholdPx) =>
        Math.Abs(endX - startX) < thresholdPx;

    /// <summary>
    /// Selection fractions for a drag from <paramref name="startX"/> to
    /// <paramref name="endX"/>, ordered so Start ≤ End.
    /// </summary>
    internal static (double Start, double End) DragRange(
        double startX, double endX, double plotLeft, double plotWidth) =>
        Order(ToFraction(startX, plotLeft, plotWidth), ToFraction(endX, plotLeft, plotWidth));

    /// <summary>
    /// Selection fractions for a click: a minimum-width band centred on
    /// <paramref name="clickX"/>. A fixed pixel width spans more wall-clock time
    /// on a longer timeframe, so the minimum range scales with the view.
    /// Clamped to [0,1].
    /// </summary>
    internal static (double Start, double End) ClickRange(
        double clickX, double plotLeft, double plotWidth, double minWidthPx) {
        var half = minWidthPx / 2.0;
        return Order(
            ToFraction(clickX - half, plotLeft, plotWidth),
            ToFraction(clickX + half, plotLeft, plotWidth));
    }

    private static (double Start, double End) Order(double a, double b) =>
        a <= b ? (a, b) : (b, a);
}
