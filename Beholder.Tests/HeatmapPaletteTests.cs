using Avalonia.Media;
using Beholder.Ui.Controls;

namespace Beholder.Tests;

/// <summary>
/// Verifies the 5-stop heatmap ramp picks the correct brush at each
/// boundary fraction. Uses an explicit palette with five distinct test
/// brushes — the production <see cref="HeatmapPalette.Resolve"/> path
/// falls back to <see cref="Brushes.Gray"/> for every stop in a headless
/// test context (no Avalonia application = no theme dictionary), which
/// would defeat ramp-distinction assertions.
/// </summary>
public sealed class HeatmapPaletteTests {
    private static readonly IBrush Cold = new SolidColorBrush(Colors.Black);
    private static readonly IBrush Low = new SolidColorBrush(Colors.Blue);
    private static readonly IBrush Medium = new SolidColorBrush(Colors.Green);
    private static readonly IBrush High = new SolidColorBrush(Colors.Orange);
    private static readonly IBrush Peak = new SolidColorBrush(Colors.Red);

    private static HeatmapPalette TestPalette() => new(Cold, Low, Medium, High, Peak);

    [Fact]
    public void BrushFor_ZeroBytes_ReturnsCold() {
        var palette = TestPalette();

        // Zero bytes always returns Cold even when the max is non-zero —
        // the country has no traffic to color-rank against.
        Assert.Same(Cold, palette.BrushFor(bytes: 0, maxBytes: 1_000_000));
    }

    [Fact]
    public void BrushFor_MaxBytesZeroOrNegative_ReturnsCold() {
        var palette = TestPalette();

        // When the dataset has no traffic at all, every country renders as
        // Cold rather than divide-by-zero or NaN-color.
        Assert.Same(Cold, palette.BrushFor(bytes: 100, maxBytes: 0));
        Assert.Same(Cold, palette.BrushFor(bytes: 100, maxBytes: -1));
    }

    [Fact]
    public void BrushFor_BelowLowFraction_ReturnsLow() {
        var palette = TestPalette();
        const long max = 1000;

        // 5% of max is below the 20% StopLowFraction → Low brush.
        Assert.Same(Low, palette.BrushFor(bytes: 50, maxBytes: max));
        // 19% still below 20% → Low.
        Assert.Same(Low, palette.BrushFor(bytes: 190, maxBytes: max));
    }

    [Fact]
    public void BrushFor_AtMediumStop_ReturnsMedium() {
        var palette = TestPalette();
        const long max = 1000;

        // BrushFor checks >= in descending order:
        //   >= 0.8 → Peak; >= 0.5 → High; >= 0.2 → Medium; else Low.
        // So 30% sits in the Medium band (0.2 ≤ x < 0.5).
        Assert.Same(Medium, palette.BrushFor(bytes: 300, maxBytes: max));
        Assert.Same(Medium, palette.BrushFor(bytes: 499, maxBytes: max));
        // Exact 20% is the stop entry — also Medium.
        Assert.Same(Medium, palette.BrushFor(bytes: 200, maxBytes: max));
    }

    [Fact]
    public void BrushFor_AtHighStop_ReturnsHigh() {
        var palette = TestPalette();
        const long max = 1000;

        // 50% to 80% is High.
        Assert.Same(High, palette.BrushFor(bytes: 500, maxBytes: max));
        Assert.Same(High, palette.BrushFor(bytes: 700, maxBytes: max));
        Assert.Same(High, palette.BrushFor(bytes: 799, maxBytes: max));
    }

    [Fact]
    public void BrushFor_AtPeakStop_ReturnsPeak() {
        var palette = TestPalette();
        const long max = 1000;

        // 80% and above is Peak.
        Assert.Same(Peak, palette.BrushFor(bytes: 800, maxBytes: max));
        Assert.Same(Peak, palette.BrushFor(bytes: max, maxBytes: max));
    }
}
