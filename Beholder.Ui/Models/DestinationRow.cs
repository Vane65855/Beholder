namespace Beholder.Ui.Models;

/// <summary>
/// A single row in the world-map hover tooltip's top-3 destinations list.
/// UI-side value type so the <c>WorldMapControl</c>'s
/// <c>HoveredCountryDestinations</c> StyledProperty doesn't pull in the
/// proto or Core type directly — keeps the control's binding surface
/// focused on what the tooltip actually renders.
/// </summary>
/// <param name="Label">
/// Hostname when DNS resolved it (via the Phase 5.4.4 four-layer ladder),
/// otherwise the raw IP. Same fallback pattern the COLS view uses.
/// </param>
/// <param name="TotalBytes">
/// Sum of in + out bytes for this destination over the current range +
/// process filter + country filter. Drives both the ORDER BY on the
/// daemon side and the display value next to <see cref="Label"/>.
/// </param>
internal sealed record DestinationRow(string Label, long TotalBytes);
