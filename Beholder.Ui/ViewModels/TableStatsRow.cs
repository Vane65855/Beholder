using System;
using System.Collections.Generic;
using System.Globalization;
using Beholder.Protocol.Local;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// One row in the Settings tab's Data Storage section table. Wraps a wire
/// <see cref="TableStats"/> with display-formatting + a visual-grouping
/// flag distinguishing the rollup-cascade traffic tiers from the other
/// schema tables.
/// </summary>
/// <remarks>
/// Immutable per row — the daemon re-issues the whole list on each
/// <c>GetStorageStats</c> refresh, so there's nothing to mutate observably.
/// </remarks>
internal sealed class TableStatsRow {
    /// <summary>
    /// Names of the five SQLite tables that participate in the rollup
    /// cascade. Used by <see cref="IsTrafficTier"/> to drive the visual
    /// grouping (the UI renders these as a clustered group at the top of
    /// the storage table, separated from the flat-schema tables below).
    /// </summary>
    private static readonly HashSet<string> TrafficTierNames = new(System.StringComparer.Ordinal) {
        "traffic_raw",
        "traffic_buckets_10s",
        "traffic_buckets_1m",
        "traffic_buckets_10m",
        "traffic_buckets_1h",
    };

    public string Name { get; }
    public long RowCount { get; }
    public string RowCountFormatted { get; }
    public bool IsTrafficTier { get; }

    private TableStatsRow(string name, long rowCount) {
        Name = name;
        RowCount = rowCount;
        RowCountFormatted = rowCount.ToString("N0", CultureInfo.InvariantCulture);
        IsTrafficTier = TrafficTierNames.Contains(name);
    }

    public static TableStatsRow FromProto(TableStats proto) {
        ArgumentNullException.ThrowIfNull(proto);
        return new TableStatsRow(proto.Name, proto.RowCount);
    }
}
