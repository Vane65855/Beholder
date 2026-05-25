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
public sealed record StorageStats(
    string DatabasePath,
    long DatabaseBytesTotal,
    IReadOnlyList<TableStats> Tables,
    ChainStatus? ChainStatus
);
