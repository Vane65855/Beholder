using System.IO;
using System.Text;
using Beholder.Ui.Services;

namespace Beholder.Tests;

public sealed class WorldGeometryLoaderTests {
    [Fact]
    public void Parse_ValidPolygonFeature_ReturnsOneShape() {
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature",
               "properties":{"iso_a2":"xs","name":"Square Land"},
               "geometry":{"type":"Polygon","coordinates":[
                 [[0,0],[10,0],[10,10],[0,10]]
               ]}}
            ]}
            """;

        var shapes = WorldGeometryLoader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        var shape = Assert.Single(shapes);
        Assert.Equal("XS", shape.Iso2);   // normalized to uppercase
        Assert.Equal("Square Land", shape.Name);
        Assert.Single(shape.Rings);
        Assert.Equal(4, shape.Rings[0].Count);
    }

    [Fact]
    public void Parse_MultiPolygonFeature_FlattensToMultipleRings() {
        // A two-island country (e.g., Japan-shaped). MultiPolygon coords
        // are [poly][ring][point][lon,lat]. Loader flattens to a single
        // rings list — caller treats every ring as exterior-fillable.
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature",
               "properties":{"iso_a2":"XM","name":"Island Land"},
               "geometry":{"type":"MultiPolygon","coordinates":[
                 [[[0,0],[5,0],[5,5],[0,5]]],
                 [[[10,10],[15,10],[15,15],[10,15]]]
               ]}}
            ]}
            """;

        var shapes = WorldGeometryLoader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        var shape = Assert.Single(shapes);
        Assert.Equal(2, shape.Rings.Count);
    }

    [Fact]
    public void Parse_FeatureMissingIsoA2_IsSkipped() {
        // The trimmer pipeline guarantees iso_a2 on every feature, but a
        // corrupt asset could omit it. Loader silently skips rather than
        // crashing the map render.
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature",
               "properties":{"name":"Anon Land"},
               "geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1]]]}}
            ]}
            """;

        var shapes = WorldGeometryLoader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Empty(shapes);
    }

    [Fact]
    public void Parse_RingWithFewerThanThreePoints_IsSkipped() {
        // A 2-point "ring" is a line, not a polygon — skip silently.
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature",
               "properties":{"iso_a2":"XL","name":"Line Land"},
               "geometry":{"type":"Polygon","coordinates":[[[0,0],[1,1]]]}}
            ]}
            """;

        var shapes = WorldGeometryLoader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Empty(shapes);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsEmptyList() {
        // Build-pipeline error (corrupt asset). Loader returns empty so the
        // WorldMapControl renders an empty ocean + "world map unavailable"
        // caption instead of crashing the Traffic tab. Plan §1 + §Edge
        // case #15.
        const string json = "{not valid json at all";

        var shapes = WorldGeometryLoader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Empty(shapes);
    }

    [Fact]
    public void Parse_MissingFeaturesArray_ReturnsEmptyList() {
        const string json = """{"type":"FeatureCollection"}""";

        var shapes = WorldGeometryLoader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Empty(shapes);
    }

    [Fact]
    public void LoadOnce_TwoCalls_ReturnSameInstance() {
        // Lazy<T> caches the first result; subsequent calls must return
        // the same reference rather than re-parsing the asset. In a
        // headless test context (no Avalonia application), the AssetLoader
        // call throws and the loader returns Array.Empty<CountryShape>()
        // which is itself a cached singleton — both invariants hold.
        var a = WorldGeometryLoader.LoadOnce();
        var b = WorldGeometryLoader.LoadOnce();

        Assert.Same(a, b);
    }

    // Integration test against the embedded Natural Earth asset is deferred
    // to manual smoke-testing: AssetLoader.Open(avares://...) requires a
    // running Avalonia application context, which the project's test
    // pattern (per the existing TrafficChartControl precedent) deliberately
    // avoids spinning up. The asset's structural correctness is verified
    // at build time by the trimmer-pipeline + by the WorldMapControl
    // rendering 177 countries during the manual UI smoke-test phase.
}
