using Beholder.Ui.Controls;

namespace Beholder.Tests;

/// <summary>
/// Covers the pure pointer-to-fraction geometry behind the traffic chart's
/// click/drag range selection (no Avalonia rendering involved).
/// </summary>
public class ChartSelectionMathTests {
    [Fact]
    public void ToFraction_MapsAcrossPlotWidth() {
        Assert.Equal(0.0, ChartSelectionMath.ToFraction(60, 60, 200));
        Assert.Equal(0.5, ChartSelectionMath.ToFraction(160, 60, 200));
        Assert.Equal(1.0, ChartSelectionMath.ToFraction(260, 60, 200));
    }

    [Fact]
    public void ToFraction_ClampsOutsidePlot() {
        Assert.Equal(0.0, ChartSelectionMath.ToFraction(0, 60, 200));
        Assert.Equal(1.0, ChartSelectionMath.ToFraction(999, 60, 200));
    }

    [Fact]
    public void ToFraction_ZeroWidth_ReturnsZero() {
        Assert.Equal(0.0, ChartSelectionMath.ToFraction(100, 60, 0));
    }

    [Fact]
    public void IsClick_TrueOnlyWithinThreshold() {
        Assert.True(ChartSelectionMath.IsClick(100, 102, 4));
        Assert.False(ChartSelectionMath.IsClick(100, 110, 4));
    }

    [Fact]
    public void DragRange_OrdersStartBeforeEndRegardlessOfDirection() {
        var (start, end) = ChartSelectionMath.DragRange(200, 110, 60, 200);
        Assert.Equal(0.25, start, 3);   // (110-60)/200
        Assert.Equal(0.70, end, 3);     // (200-60)/200
    }

    [Fact]
    public void ClickRange_CentersMinWidthBandOnClick() {
        var (start, end) = ChartSelectionMath.ClickRange(160, 60, 200, 20);
        // click maps to 0.5; ±10px → 150..170 → 0.45..0.55
        Assert.Equal(0.45, start, 3);
        Assert.Equal(0.55, end, 3);
    }

    [Fact]
    public void ClickRange_ClampsAtLeftEdge() {
        var (start, end) = ChartSelectionMath.ClickRange(60, 60, 200, 20);
        Assert.Equal(0.0, start, 3);
        Assert.Equal(0.05, end, 3);
    }
}
