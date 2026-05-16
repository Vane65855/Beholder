using System.Collections.Generic;
using Beholder.Ui.Controls;
using Beholder.Ui.Models;

namespace Beholder.Tests;

public sealed class WorldMapHitTesterTests {
    // A simple convex quadrilateral in geographic coordinates: a square from
    // (0, 0) to (10, 10). Used as a synthetic "country" for tests so the
    // hit-test math is verifiable without depending on the real GeoJSON.
    private static readonly CountryShape FakeSquare = new(
        Iso2: "XS",
        Name: "Square Land",
        Rings: new[] {
            new[] {
                new GeoPoint(0, 0),
                new GeoPoint(10, 0),
                new GeoPoint(10, 10),
                new GeoPoint(0, 10),
            },
        });

    // A second country far away to verify the bounding-box prefilter
    // correctly rejects out-of-bbox points without iterating its vertices.
    private static readonly CountryShape FarAwayTriangle = new(
        Iso2: "XT",
        Name: "Triangle Land",
        Rings: new[] {
            new[] {
                new GeoPoint(100, 100),
                new GeoPoint(110, 100),
                new GeoPoint(105, 110),
            },
        });

    [Fact]
    public void FindCountryAt_PointInsideSquare_ReturnsIso2() {
        var iso = WorldMapHitTester.FindCountryAt(new GeoPoint(5, 5), new[] { FakeSquare });

        Assert.Equal("XS", iso);
    }

    [Fact]
    public void FindCountryAt_PointOutsideAllShapes_ReturnsNull() {
        // (200, 200) is far outside both XS and XT bboxes — the prefilter
        // rejects them without running the ray-cast on either.
        var iso = WorldMapHitTester.FindCountryAt(
            new GeoPoint(200, 200), new[] { FakeSquare, FarAwayTriangle });

        Assert.Null(iso);
    }

    [Fact]
    public void FindCountryAt_PointInBboxButOutsidePolygon_ReturnsNull() {
        // (1, 1) is inside FarAwayTriangle's bbox (100..110, 100..110)? No —
        // it's far outside. Use (108, 101): inside the bbox (100..110,
        // 100..110) but outside the actual triangle (which has its right
        // edge sloping from (110, 100) to (105, 110)).
        var iso = WorldMapHitTester.FindCountryAt(
            new GeoPoint(108, 109), new[] { FarAwayTriangle });

        Assert.Null(iso);
    }

    [Fact]
    public void FindCountryAt_PointInOneOfMultipleShapes_ReturnsCorrectIso2() {
        // Two shapes present; point sits inside the second one only.
        var iso = WorldMapHitTester.FindCountryAt(
            new GeoPoint(105, 102), new[] { FakeSquare, FarAwayTriangle });

        Assert.Equal("XT", iso);
    }

    [Fact]
    public void FindCountryAt_PointInMultiPolygonRing_ReturnsCorrectIso2() {
        // A "country" with two disjoint rings (e.g., a USA-like shape with
        // a mainland and a Hawaii). Point sits inside the second ring.
        var multiRingCountry = new CountryShape(
            Iso2: "XM",
            Name: "Multi-Ring Land",
            Rings: new[] {
                new[] {
                    new GeoPoint(0, 0),
                    new GeoPoint(5, 0),
                    new GeoPoint(5, 5),
                    new GeoPoint(0, 5),
                },
                new[] {
                    new GeoPoint(50, 50),
                    new GeoPoint(55, 50),
                    new GeoPoint(55, 55),
                    new GeoPoint(50, 55),
                },
            });

        var iso = WorldMapHitTester.FindCountryAt(
            new GeoPoint(52, 52), new[] { multiRingCountry });

        Assert.Equal("XM", iso);
    }
}
