using Avalonia;
using Beholder.Ui.Controls;
using Beholder.Ui.Models;

namespace Beholder.Tests;

public sealed class WorldMapProjectionTests {
    private static readonly Rect Bounds = new(0, 0, 1000, 500);

    [Fact]
    public void Project_OriginPoint_ReturnsBoundsCenter() {
        var p = WorldMapProjection.Project(new GeoPoint(0, 0), Bounds);

        Assert.Equal(500, p.X, precision: 6);
        Assert.Equal(250, p.Y, precision: 6);
    }

    [Fact]
    public void Project_LongitudeAtMax_ReturnsRightEdge() {
        var p = WorldMapProjection.Project(new GeoPoint(180, 0), Bounds);

        Assert.Equal(1000, p.X, precision: 6);
    }

    [Fact]
    public void Project_LongitudeAtMin_ReturnsLeftEdge() {
        var p = WorldMapProjection.Project(new GeoPoint(-180, 0), Bounds);

        Assert.Equal(0, p.X, precision: 6);
    }

    [Fact]
    public void Project_LatitudeAtMax_ReturnsTopEdge() {
        // +90° latitude is the North Pole; projects to Y=0 (top of canvas).
        var p = WorldMapProjection.Project(new GeoPoint(0, 90), Bounds);

        Assert.Equal(0, p.Y, precision: 6);
    }

    [Fact]
    public void Project_LatitudeAtMin_ReturnsBottomEdge() {
        // −90° latitude is the South Pole; projects to Y=Height (bottom).
        var p = WorldMapProjection.Project(new GeoPoint(0, -90), Bounds);

        Assert.Equal(500, p.Y, precision: 6);
    }

    [Fact]
    public void Project_RoundTripUnproject_PreservesPoint() {
        // Berlin: 52.5°N, 13.4°E. Round-trip through Project then Unproject
        // must return the same lat/lon within floating-point precision.
        var original = new GeoPoint(13.4, 52.5);
        var projected = WorldMapProjection.Project(original, Bounds);
        var roundtrip = WorldMapProjection.Unproject(projected, Bounds);

        Assert.Equal(original.Longitude, roundtrip.Longitude, precision: 6);
        Assert.Equal(original.Latitude, roundtrip.Latitude, precision: 6);
    }
}
