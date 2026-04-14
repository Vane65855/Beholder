using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Beholder.Ui.Helpers;

/// <summary>
/// Resolves Avalonia theme color resources by key name.
/// </summary>
internal static class ThemeColorHelper {
    /// <summary>
    /// Resolves a Color resource key (e.g., "ChartOutboundStrokeColor") to an Avalonia Color.
    /// Returns <see cref="Colors.White"/> if the resource is not found.
    /// </summary>
    public static Color Resolve(string colorResourceKey) {
        var app = Application.Current;
        if (app is not null && ResourceNodeExtensions.TryFindResource(app, colorResourceKey, out var obj)
            && obj is Color color)
            return color;
        return Colors.White;
    }

    /// <summary>
    /// Resolves a series index (1-12) to an Avalonia Color via the Series{NN}Color resource.
    /// </summary>
    public static Color ResolveSeriesColor(int seriesIndex) =>
        Resolve(SeriesColorHelper.GetColorResourceKey(seriesIndex));
}
