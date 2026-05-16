using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Beholder.Ui.Controls;

/// <summary>
/// 5-stop heatmap color ramp for the world-map per-country fill. The fraction
/// stops (<see cref="StopLowFraction"/> / <see cref="StopMediumFraction"/> /
/// <see cref="StopHighFraction"/>) are named constants per CODING_STANDARDS.md
/// banned-pattern table ("Magic numbers / strings — Named constants or enums").
/// </summary>
/// <remarks>
/// The five brushes come from theme tokens defined in UI_DESIGN.md §2 Data
/// Visualization (HeatmapCold / Low / Medium / High / Peak). Brushes are
/// cached after first resolution; the cache is invalidated by callers when
/// the theme changes (mirrors TrafficChartControl's ResourcesChanged
/// behavior).
/// </remarks>
internal sealed class HeatmapPalette {
    public const double StopLowFraction = 0.20;
    public const double StopMediumFraction = 0.50;
    public const double StopHighFraction = 0.80;

    private readonly IBrush _cold;
    private readonly IBrush _low;
    private readonly IBrush _medium;
    private readonly IBrush _high;
    private readonly IBrush _peak;

    /// <summary>
    /// Constructs a palette with explicit brushes for each stop. Public so
    /// tests can build a palette with distinct test brushes without
    /// depending on Avalonia's runtime resource resolution (which falls
    /// back to <see cref="Brushes.Gray"/> for every stop in a headless
    /// test context, defeating ramp-distinction assertions). Production
    /// callers use <see cref="Resolve"/> which pulls from theme tokens.
    /// </summary>
    internal HeatmapPalette(IBrush cold, IBrush low, IBrush medium, IBrush high, IBrush peak) {
        _cold = cold;
        _low = low;
        _medium = medium;
        _high = high;
        _peak = peak;
    }

    /// <summary>
    /// Resolves the 5 heatmap brushes from the active theme dictionary at
    /// the time of call. Returns a populated palette ready for repeated
    /// <see cref="BrushFor"/> calls during a single render pass.
    /// </summary>
    public static HeatmapPalette Resolve() {
        return new HeatmapPalette(
            cold: ResolveBrush("HeatmapCold"),
            low: ResolveBrush("HeatmapLow"),
            medium: ResolveBrush("HeatmapMedium"),
            high: ResolveBrush("HeatmapHigh"),
            peak: ResolveBrush("HeatmapPeak"));
    }

    /// <summary>
    /// Returns the appropriate heatmap brush for <paramref name="bytes"/>
    /// given a normalization ceiling of <paramref name="maxBytes"/>. A
    /// <c>bytes</c> value of 0 returns <c>HeatmapCold</c> regardless of the
    /// max; if <c>maxBytes &lt;= 0</c> every country renders as Cold (no
    /// traffic to color-rank against).
    /// </summary>
    public IBrush BrushFor(long bytes, long maxBytes) {
        if (bytes <= 0 || maxBytes <= 0) return _cold;
        var fraction = (double)bytes / maxBytes;
        if (fraction >= StopHighFraction) return _peak;
        if (fraction >= StopMediumFraction) return _high;
        if (fraction >= StopLowFraction) return _medium;
        return _low;
    }

    private static IBrush ResolveBrush(string tokenName) {
        var app = Application.Current;
        if (app is not null
            && ResourceNodeExtensions.TryFindResource(app, tokenName, out var res)
            && res is ISolidColorBrush brush) {
            return brush;
        }
        return Brushes.Gray;
    }
}
