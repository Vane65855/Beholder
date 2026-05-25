namespace Beholder.Core;

/// <summary>
/// Aggregate storage-and-integrity snapshot returned by
/// <see cref="IStorageStatsProvider"/>. Backs the Settings tab's Data
/// Storage section in a single round-trip on tab activation and on every
/// refresh-button press.
/// </summary>
/// <param name="DatabasePath">
/// Absolute path of the SQLite database file. Used by the UI's "Open data
/// folder" button to launch the shell-default file explorer at the
/// containing directory.
/// </param>
/// <param name="DatabaseBytesTotal">
/// On-disk size of the database file in bytes, read from
/// <c>FileInfo.Length</c>. Includes WAL and shared-memory data only if the
/// SQLite engine has rolled them into the main file at query time; the
/// number is approximate but sufficient for the "is this getting big?"
/// use case the section is meant to answer.
/// </param>
/// <param name="Tables">Per-table row counts.</param>
/// <param name="ChainStatus">
/// Last chain-verification outcome (from either the periodic monitor or a
/// user-triggered <c>VerifyChain</c> RPC), or null when no verification
/// has run yet this daemon session.
/// </param>
/// <param name="ChainFirstEventAt">
/// Timestamp of the earliest row in <c>event_log</c> — the moment the
/// audit chain began for this installation. Surfaced in the Settings tab's
/// About section as "Watching this machine since DATE (N days)". Null when
/// the chain is empty (fresh install with no events yet).
/// </param>
/// <param name="DaemonStartedAt">
/// Wall-clock time at which the daemon process started, captured by
/// <see cref="IDaemonClock"/>. Settings tab derives "uptime 4h 12m" from
/// this against the current clock.
/// </param>
/// <param name="LanDeviceCount">
/// Count of rows in the <c>lan_device</c> table — duplicated as a top-level
/// field for convenience so the Settings tab's MOTD strip can render
/// "N LAN devices tracked" without a second lookup over <see cref="Tables"/>.
/// </param>
public sealed record StorageStats(
    string DatabasePath,
    long DatabaseBytesTotal,
    IReadOnlyList<TableStats> Tables,
    ChainStatus? ChainStatus,
    DateTimeOffset? ChainFirstEventAt,
    DateTimeOffset DaemonStartedAt,
    long LanDeviceCount
);
