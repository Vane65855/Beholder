using System.Collections.Generic;

namespace Beholder.Ui.Models;

/// <summary>
/// The vector shape of a single country, parsed once from the embedded
/// Natural Earth GeoJSON asset. <see cref="Rings"/> is a list of polygon
/// rings — most countries have one (single landmass), but island nations
/// and countries with multiple landmasses (USA with Alaska + Hawaii,
/// Russia spanning the dateline) have several. The first ring is the
/// exterior; remaining rings are interior holes if any, but Natural Earth
/// at 110m resolution has no holes so callers can treat every ring as
/// exterior for fill / hit-test purposes.
/// </summary>
/// <remarks>
/// <see cref="Iso2"/> is normalized to uppercase. <see cref="Name"/> is the
/// English name from Natural Earth's <c>NAME</c> property — used as-is in
/// the tooltip; i18n is deferred per UI_QUALITY_STANDARDS §9.
/// </remarks>
internal sealed record CountryShape(
    string Iso2,
    string Name,
    IReadOnlyList<IReadOnlyList<GeoPoint>> Rings);
