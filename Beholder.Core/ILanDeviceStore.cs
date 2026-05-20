namespace Beholder.Core;

/// <summary>
/// Persistence layer for devices discovered on the local subnet by the Phase 9
/// LAN scanner. Identity is keyed on MAC per ADR 009; IP is a mutable attribute
/// that the store tracks but doesn't enforce uniqueness on (two rows may briefly
/// share an IP during MAC churn — the scanner uses
/// <see cref="GetByIpAsync"/> + a follow-up compare to detect MAC changes for
/// chain-event emission).
/// </summary>
public interface ILanDeviceStore {
    /// <summary>
    /// Returns the device with the given <paramref name="mac"/>, or <c>null</c>
    /// if no row exists. MAC matching is exact on the stored canonical form
    /// (lowercase hex with colons, e.g. <c>aa:bb:cc:dd:ee:ff</c>).
    /// </summary>
    Task<LanDevice?> GetByMacAsync(string mac, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a device currently associated with the given <paramref name="ip"/>,
    /// or <c>null</c> if no row exists. Multiple devices may transiently share an
    /// IP (DHCP reassignment between two MACs); this method returns one of them
    /// (implementation-defined which). Used by the scanner to detect MAC changes
    /// for a known IP — for that path the caller wants any one of the rows and
    /// then performs an explicit equality check on the MAC.
    /// </summary>
    Task<LanDevice?> GetByIpAsync(string ip, CancellationToken cancellationToken);

    /// <summary>
    /// Returns devices matching <paramref name="query"/>, ordered by
    /// <see cref="LanDevice.LastSeen"/> descending so the most recently observed
    /// devices appear first.
    /// </summary>
    Task<IReadOnlyList<LanDevice>> ListAsync(LanDeviceQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new device row keyed on <see cref="LanDevice.Mac"/> or, when a
    /// row with the same MAC already exists, updates its <c>ip</c>, <c>vendor</c>,
    /// <c>hostname</c>, and <c>last_seen_unix_ns</c> columns. The original
    /// <see cref="LanDevice.FirstSeen"/> is preserved across upserts — only the
    /// initial insert records it. The <see cref="LanDevice.Label"/> column is
    /// likewise preserved on conflict: scanner re-observations carry <c>null</c>
    /// for the label (the scanner has no notion of user labels), but the
    /// implementation's <c>ON CONFLICT</c> clause deliberately omits the column
    /// so a user-set label persists across re-observations.
    /// </summary>
    Task UpsertAsync(LanDevice device, CancellationToken cancellationToken);

    /// <summary>
    /// Sets or clears the user-supplied cosmetic label for the device identified
    /// by <paramref name="mac"/>. Passing <see langword="null"/> or
    /// whitespace-only clears the label. If no device with the given MAC exists,
    /// the call is a no-op (no exception). Phase 9.5 / ADR 009: labels are
    /// cosmetic UI state, not chain-audited.
    /// </summary>
    Task SetLabelAsync(string mac, string? label, CancellationToken cancellationToken);
}
