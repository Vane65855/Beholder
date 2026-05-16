using System.Collections.Generic;
using Avalonia;
using Beholder.Ui.Models;

namespace Beholder.Ui.Controls;

/// <summary>
/// Pure point-in-country hit-tester for the world map's hover handler.
/// Two-stage filter: (1) lat/lon bounding-box prefilter rejects most
/// candidates in O(N) with one comparison per country; (2) point-in-
/// polygon ray-cast confirms the survivors. At ~177 countries × 1–8
/// rings each, the whole pass is well under a frame on any modern machine.
/// </summary>
/// <remarks>
/// Operates entirely in geographic coordinates so the same logic works
/// for any projection — the caller unprojects the screen point once and
/// hands a <see cref="GeoPoint"/> in. Lookup happens at every PointerMoved
/// tick, so the prefilter pays off.
/// </remarks>
internal static class WorldMapHitTester {
    /// <summary>
    /// Returns the ISO_A2 code of the country containing
    /// <paramref name="point"/>, or <c>null</c> if the point is in the
    /// ocean or outside the geographic range.
    /// </summary>
    public static string? FindCountryAt(GeoPoint point, IReadOnlyList<CountryShape> shapes) {
        for (var i = 0; i < shapes.Count; i++) {
            var shape = shapes[i];
            // Per-ring bbox + point-in-polygon. A multi-polygon country
            // is detected as soon as ANY ring claims the point.
            for (var r = 0; r < shape.Rings.Count; r++) {
                var ring = shape.Rings[r];
                if (RingContains(ring, point)) return shape.Iso2;
            }
        }
        return null;
    }

    /// <summary>
    /// Standard horizontal ray-cast: cast a ray from the test point along
    /// +longitude and count edge crossings. Odd = inside, even = outside.
    /// Includes the bounding-box prefilter inline so each ring is rejected
    /// without iterating its vertices when the point is clearly outside.
    /// </summary>
    internal static bool RingContains(IReadOnlyList<GeoPoint> ring, GeoPoint point) {
        if (ring.Count < 3) return false;

        // Bbox prefilter — single pass, fast bail on the common case.
        double minLon = double.MaxValue, maxLon = double.MinValue;
        double minLat = double.MaxValue, maxLat = double.MinValue;
        for (var i = 0; i < ring.Count; i++) {
            var p = ring[i];
            if (p.Longitude < minLon) minLon = p.Longitude;
            if (p.Longitude > maxLon) maxLon = p.Longitude;
            if (p.Latitude < minLat) minLat = p.Latitude;
            if (p.Latitude > maxLat) maxLat = p.Latitude;
        }
        if (point.Longitude < minLon || point.Longitude > maxLon
            || point.Latitude < minLat || point.Latitude > maxLat) {
            return false;
        }

        // Ray-cast: for each edge, test if it straddles the point's
        // latitude AND crosses the +longitude ray from the point.
        var inside = false;
        var n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++) {
            var pi = ring[i];
            var pj = ring[j];
            // Does the edge (pj→pi) straddle point.Latitude?
            if ((pi.Latitude > point.Latitude) != (pj.Latitude > point.Latitude)) {
                // Where does the edge cross the horizontal line at point.Latitude?
                var slope = (pj.Longitude - pi.Longitude) / (pj.Latitude - pi.Latitude);
                var crossingLon = pi.Longitude + slope * (point.Latitude - pi.Latitude);
                if (point.Longitude < crossingLon) inside = !inside;
            }
        }
        return inside;
    }
}
