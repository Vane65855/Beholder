namespace Beholder.Ui.Models;

/// <summary>
/// A single geographic coordinate in decimal degrees. <c>Longitude</c> ranges
/// −180..+180 (east positive); <c>Latitude</c> ranges −90..+90 (north
/// positive). Stored unprojected so the projection can change later without
/// re-processing the source GeoJSON.
/// </summary>
internal readonly record struct GeoPoint(double Longitude, double Latitude);
