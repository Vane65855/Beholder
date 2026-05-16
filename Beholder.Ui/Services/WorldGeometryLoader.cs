using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Platform;
using Beholder.Ui.Models;

namespace Beholder.Ui.Services;

/// <summary>
/// One-shot loader for the embedded Natural Earth 110m world-countries
/// GeoJSON asset. Exposes <see cref="LoadOnce"/> which parses on first call
/// and caches the result for the lifetime of the process via
/// <see cref="Lazy{T}"/> — thread-safe by default, no manual locking, and
/// satisfies CLAUDE.md's "no mutable static state" rule because the cache
/// is set-once + immutable.
/// </summary>
/// <remarks>
/// <para>
/// On malformed JSON (asset corruption — a build-pipeline error, not a
/// user-facing one) the loader returns an empty list and writes a Debug
/// trace; the <c>WorldMapControl</c> then renders an empty ocean with a
/// small "world map unavailable" caption rather than crashing the
/// Traffic tab.
/// </para>
/// <para>
/// The internal <see cref="Parse"/> method takes a raw <see cref="Stream"/>
/// so tests can inject malformed JSON directly without writing a temp
/// file.
/// </para>
/// </remarks>
internal static class WorldGeometryLoader {
    private const string AssetUri = "avares://Beholder.Ui/Assets/world-countries-110m.geojson";

    private static readonly Lazy<IReadOnlyList<CountryShape>> Cache =
        new(LoadFromAsset, isThreadSafe: true);

    public static IReadOnlyList<CountryShape> LoadOnce() => Cache.Value;

    private static IReadOnlyList<CountryShape> LoadFromAsset() {
        try {
            using var stream = AssetLoader.Open(new Uri(AssetUri));
            return Parse(stream);
        } catch (Exception ex) {
            Debug.WriteLine($"WorldGeometryLoader: asset open failed: {ex.Message}");
            return Array.Empty<CountryShape>();
        }
    }

    /// <summary>
    /// Parses a Natural Earth-shaped GeoJSON FeatureCollection from
    /// <paramref name="stream"/>. Returns an empty list on malformed JSON.
    /// Internal so tests can drive it with arbitrary fixtures.
    /// </summary>
    internal static IReadOnlyList<CountryShape> Parse(Stream stream) {
        ArgumentNullException.ThrowIfNull(stream);
        try {
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("features", out var features)
                || features.ValueKind != JsonValueKind.Array) {
                Debug.WriteLine("WorldGeometryLoader: missing or non-array 'features'");
                return Array.Empty<CountryShape>();
            }

            var shapes = new List<CountryShape>(features.GetArrayLength());
            foreach (var feature in features.EnumerateArray()) {
                var shape = TryParseFeature(feature);
                if (shape is not null) shapes.Add(shape);
            }
            return shapes;
        } catch (JsonException ex) {
            Debug.WriteLine($"WorldGeometryLoader: JSON parse failed: {ex.Message}");
            return Array.Empty<CountryShape>();
        }
    }

    private static CountryShape? TryParseFeature(JsonElement feature) {
        if (!feature.TryGetProperty("properties", out var props)) return null;
        if (!feature.TryGetProperty("geometry", out var geom)) return null;
        if (!props.TryGetProperty("iso_a2", out var isoEl)) return null;
        if (!geom.TryGetProperty("type", out var typeEl)) return null;
        if (!geom.TryGetProperty("coordinates", out var coordsEl)) return null;

        var iso = isoEl.GetString();
        if (string.IsNullOrEmpty(iso)) return null;

        var name = props.TryGetProperty("name", out var nameEl)
            ? nameEl.GetString() ?? iso
            : iso;

        var type = typeEl.GetString();
        // GeoJSON Polygon = [ring][point][lon,lat]; MultiPolygon adds an
        // outer level: [poly][ring][point][lon,lat]. Flatten both to a
        // single list of rings since the caller treats every ring as
        // exterior-fillable (Natural Earth at 110m has no holes).
        var rings = type switch {
            "Polygon" => ParsePolygon(coordsEl),
            "MultiPolygon" => ParseMultiPolygon(coordsEl),
            _ => null,
        };
        if (rings is null || rings.Count == 0) return null;

        return new CountryShape(iso.ToUpperInvariant(), name, rings);
    }

    private static IReadOnlyList<IReadOnlyList<GeoPoint>>? ParsePolygon(JsonElement coords) {
        if (coords.ValueKind != JsonValueKind.Array) return null;
        var rings = new List<IReadOnlyList<GeoPoint>>(coords.GetArrayLength());
        foreach (var ring in coords.EnumerateArray()) {
            var points = ParseRing(ring);
            if (points is not null) rings.Add(points);
        }
        return rings;
    }

    private static IReadOnlyList<IReadOnlyList<GeoPoint>>? ParseMultiPolygon(JsonElement coords) {
        if (coords.ValueKind != JsonValueKind.Array) return null;
        var rings = new List<IReadOnlyList<GeoPoint>>();
        foreach (var polygon in coords.EnumerateArray()) {
            var polyRings = ParsePolygon(polygon);
            if (polyRings is not null) rings.AddRange(polyRings);
        }
        return rings;
    }

    private static IReadOnlyList<GeoPoint>? ParseRing(JsonElement ring) {
        if (ring.ValueKind != JsonValueKind.Array) return null;
        var points = new List<GeoPoint>(ring.GetArrayLength());
        foreach (var pt in ring.EnumerateArray()) {
            if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
            var lon = pt[0].GetDouble();
            var lat = pt[1].GetDouble();
            points.Add(new GeoPoint(lon, lat));
        }
        return points.Count >= 3 ? points : null;   // need at least a triangle
    }
}
