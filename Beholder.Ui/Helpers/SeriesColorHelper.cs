using System;

namespace Beholder.Ui.Helpers;

/// <summary>
/// Assigns a deterministic series color index (1-12) to a process based on its path.
/// The index maps to the Series01-Series12 design tokens in the theme.
/// </summary>
internal static class SeriesColorHelper {
    private const int SeriesCount = 12;

    /// <summary>
    /// Returns a series index in the range [1, 12] deterministic for the given process path.
    /// </summary>
    public static int GetSeriesIndex(string processPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        var hash = unchecked((uint)processPath.GetHashCode(StringComparison.OrdinalIgnoreCase));
        return (int)(hash % SeriesCount) + 1;
    }

    /// <summary>
    /// Returns the theme resource key for a series index (e.g., "Series01Color").
    /// </summary>
    public static string GetColorResourceKey(int seriesIndex) =>
        $"Series{seriesIndex:D2}Color";

    /// <summary>
    /// Returns the theme resource key for a series brush (e.g., "Series01").
    /// </summary>
    public static string GetBrushResourceKey(int seriesIndex) =>
        $"Series{seriesIndex:D2}";
}
