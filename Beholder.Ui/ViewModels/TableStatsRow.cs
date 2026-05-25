using System;
using System.Collections.Generic;
using System.Globalization;
using Beholder.Protocol.Local;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// One row in the Settings tab's Data Storage section table. Wraps a wire
/// <see cref="TableStats"/> with pretty-name + retention + tooltip
/// description + per-row proportional-bar ratio + sort-order metadata.
/// </summary>
/// <remarks>
/// Per-table metadata (display name, retention, description, sort key)
/// lives in a single in-file <see cref="KnownTables"/> dictionary so the
/// Settings tab presents a consistent vocabulary for the entire schema in
/// one place. Tables the daemon adds but the UI doesn't know about
/// gracefully degrade — the raw <see cref="Name"/> shows in place of the
/// pretty name, retention reads "—", and the row sorts to the end.
/// </remarks>
internal sealed class TableStatsRow {
    /// <summary>
    /// Names of the five SQLite tables that participate in the rollup
    /// cascade. Used by <see cref="IsTrafficTier"/> to drive the visual
    /// grouping (the UI renders these in a separate "Traffic rollup"
    /// section above the flat-schema tables).
    /// </summary>
    private static readonly HashSet<string> TrafficTierNames = new(StringComparer.Ordinal) {
        "traffic_raw",
        "traffic_buckets_10s",
        "traffic_buckets_1m",
        "traffic_buckets_10m",
        "traffic_buckets_1h",
    };

    /// <summary>
    /// Per-table presentation metadata. Sort keys 0-4 are the traffic
    /// cascade in finest→coarsest order; 100+ are flat-schema tables in
    /// alphabetical order (so a new flat table fits cleanly without
    /// reshuffling keys). Anything not listed here renders with raw name
    /// and SortKey=999.
    /// </summary>
    private static readonly Dictionary<string, TableMetadata> KnownTables = new(StringComparer.Ordinal) {
        ["traffic_raw"] = new(
            DisplayName: "Traffic — raw (1s)",
            Retention: "10 min",
            Description: "Per-second flow buckets. The finest tier of the rollup cascade; aggregated into the 10s tier every 10 s and pruned after 10 min.",
            SortKey: 0),
        ["traffic_buckets_10s"] = new(
            DisplayName: "Traffic — 10s buckets",
            Retention: "7 days",
            Description: "Ten-second aggregates derived from traffic_raw. Drives recent-history charts that need sub-minute resolution.",
            SortKey: 1),
        ["traffic_buckets_1m"] = new(
            DisplayName: "Traffic — 1m buckets",
            Retention: "14 days",
            Description: "One-minute aggregates derived from traffic_buckets_10s.",
            SortKey: 2),
        ["traffic_buckets_10m"] = new(
            DisplayName: "Traffic — 10m buckets",
            Retention: "1 year",
            Description: "Ten-minute aggregates. Covers a full year of medium-resolution history.",
            SortKey: 3),
        ["traffic_buckets_1h"] = new(
            DisplayName: "Traffic — 1h buckets",
            Retention: "∞",
            Description: "Hourly aggregates. The coarsest, longest-lived tier — never pruned.",
            SortKey: 4),
        ["alert_state"] = new(
            DisplayName: "Alert read state",
            Retention: "∞",
            Description: "Per-alert viewed-at timestamps. Cosmetic UI state — not chain-audited.",
            SortKey: 100),
        ["checkpoint"] = new(
            DisplayName: "Chain checkpoints",
            Retention: "∞",
            Description: "Signed chain checkpoints (Phase 11 — currently unused).",
            SortKey: 101),
        ["dns_cache"] = new(
            DisplayName: "DNS cache",
            Retention: "∞",
            Description: "Cached IP→hostname mappings observed via ETW DNS events, reverse-DNS lookups, and TLS SNI extraction.",
            SortKey: 102),
        ["event_log"] = new(
            DisplayName: "Audit chain",
            Retention: "∞",
            Description: "Chain-hashed append-only event log. Every state-changing event (firewall rules, new processes, hash changes, scanner discoveries) is recorded here with a SHA-256 link to the previous row.",
            SortKey: 103),
        ["firewall_rules"] = new(
            DisplayName: "Firewall rules",
            Retention: "∞",
            Description: "Active Beholder-managed firewall rules. One row per (process path, direction).",
            SortKey: 104),
        ["lan_device"] = new(
            DisplayName: "LAN devices",
            Retention: "∞",
            Description: "Devices observed on the local network. Identity is keyed on MAC address per ADR 009.",
            SortKey: 105),
        ["process_registry"] = new(
            DisplayName: "Process registry",
            Retention: "∞",
            Description: "First-seen records for every binary that has produced network flows. Backs NewProcess dedup + binary-hash change detection.",
            SortKey: 106),
    };

    private sealed record TableMetadata(string DisplayName, string Retention, string Description, int SortKey);

    public string Name { get; }
    public long RowCount { get; }
    public string RowCountFormatted { get; }
    public bool IsTrafficTier { get; }
    public string DisplayName { get; }
    public string Retention { get; }
    public string Description { get; }
    /// <summary>
    /// This row's row count as a fraction of the group's maximum row
    /// count (max-of-section, not sum-of-section). Drives the per-row
    /// proportional bar. Using "fraction of max" rather than "fraction of
    /// total" keeps small rows legible — the largest row's bar fills the
    /// column and smaller rows remain visibly smaller without becoming
    /// invisible.
    /// </summary>
    public double RowCountShareRatio { get; }
    public int SortKey { get; }

    private TableStatsRow(string name, long rowCount, long maxRowCountInGroup) {
        Name = name;
        RowCount = rowCount;
        RowCountFormatted = rowCount.ToString("N0", CultureInfo.InvariantCulture);
        IsTrafficTier = TrafficTierNames.Contains(name);
        if (KnownTables.TryGetValue(name, out var meta)) {
            DisplayName = meta.DisplayName;
            Retention = meta.Retention;
            Description = meta.Description;
            SortKey = meta.SortKey;
        } else {
            DisplayName = name;
            Retention = "—";
            Description = $"Table '{name}' has no presentation metadata registered in the UI.";
            SortKey = 999;
        }
        RowCountShareRatio = maxRowCountInGroup > 0
            ? Math.Clamp((double)rowCount / maxRowCountInGroup, 0.0, 1.0)
            : 0.0;
    }

    /// <summary>
    /// Builds a row from the wire shape. <paramref name="maxRowCountInGroup"/>
    /// is the largest row count in this row's group (traffic tiers grouped
    /// together; flat tables grouped together) — used to compute the
    /// per-row proportional bar ratio.
    /// </summary>
    public static TableStatsRow FromProto(TableStats proto, long maxRowCountInGroup) {
        ArgumentNullException.ThrowIfNull(proto);
        return new TableStatsRow(proto.Name, proto.RowCount, maxRowCountInGroup);
    }
}
