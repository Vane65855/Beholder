namespace Beholder.Core;

/// <summary>
/// Filter and pagination parameters for <see cref="ILanDeviceStore.ListAsync"/>.
/// Bundled into a record per <c>docs/PRINCIPLES.md</c> §Functions "group related
/// parameters into a record" so the store's list-method signature stays at two
/// arguments (query + cancellation token). Mirrors the
/// <see cref="DestinationsQuery"/> precedent established in Phase 8 polish.
/// </summary>
/// <param name="SeenSince">
/// When non-null, only devices whose <see cref="LanDevice.LastSeen"/> is at or
/// after this timestamp are returned. Null disables the time filter and returns
/// every device.
/// </param>
/// <param name="Limit">
/// Maximum number of rows to return. Zero or negative disables the limit and
/// returns every matching row. Rows are ordered by <see cref="LanDevice.LastSeen"/>
/// descending, so a limit returns the most recently seen devices.
/// </param>
public sealed record LanDeviceQuery(
    DateTimeOffset? SeenSince,
    int Limit);
