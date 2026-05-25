namespace Beholder.Core;

/// <summary>
/// Row-count statistics for a single SQLite table, used by the Settings
/// tab's Data Storage section. Per-table on-disk byte size is deliberately
/// not exposed — querying it requires enabling SQLite's <c>dbstat</c>
/// virtual table, which adds platform complexity for marginal user value.
/// The UI displays row counts per table and the total database file size,
/// which is sufficient for "is this thing eating my disk" diagnosis.
/// </summary>
public sealed record TableStats(string Name, long RowCount);
