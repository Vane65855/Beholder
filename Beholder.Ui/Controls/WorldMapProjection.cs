using Avalonia;
using Beholder.Ui.Models;

namespace Beholder.Ui.Controls;

/// <summary>
/// Equirectangular (plate carrée) projection: longitude maps linearly to X,
/// latitude maps linearly to Y. The simplest possible projection — no
/// trigonometry, no distortion math, looks like the standard "flat" world
/// maps every network monitor ships. Polar distortion is mild because
/// polar traffic is effectively zero.
/// </summary>
/// <remarks>
/// All functions are pure and trivially testable. If a future polish pass
/// wants Mercator or Equal Earth, this is the single seam to swap — the
/// loader stores lat/lon, the control calls <see cref="Project"/> per
/// vertex per render, and <see cref="Unproject"/> is the inverse used by
/// the hover hit-tester.
/// </remarks>
internal static class WorldMapProjection {
    /// <summary>
    /// Projects a geographic coordinate to a screen point inside
    /// <paramref name="bounds"/>. Longitude −180..+180 → bounds.X..X+Width;
    /// latitude +90..−90 → bounds.Y..Y+Height (Y is flipped so positive
    /// latitude maps to lower Y, matching screen coordinates).
    /// </summary>
    public static Point Project(GeoPoint p, Rect bounds) => new(
        bounds.X + (p.Longitude + 180.0) / 360.0 * bounds.Width,
        bounds.Y + (90.0 - p.Latitude) / 180.0 * bounds.Height);

    /// <summary>
    /// Inverse of <see cref="Project"/>: turns a screen point back into
    /// lat/lon. Used by the hit-tester so the bounding-box prefilter can
    /// stay in geographic coordinates (avoids projecting every country
    /// twice per hover event).
    /// </summary>
    public static GeoPoint Unproject(Point screen, Rect bounds) {
        var longitude = (screen.X - bounds.X) / bounds.Width * 360.0 - 180.0;
        var latitude = 90.0 - (screen.Y - bounds.Y) / bounds.Height * 180.0;
        return new GeoPoint(longitude, latitude);
    }
}
