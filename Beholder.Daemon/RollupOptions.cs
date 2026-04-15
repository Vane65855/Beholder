namespace Beholder.Daemon;

/// <summary>
/// Controls the storage cascade: which tiers exist, their bucket sizes, retention,
/// and rollup cadence. Shipped with two hand-tuned presets —
/// <see cref="RetentionPreset.Balanced"/> and <see cref="RetentionPreset.Compact"/>
/// — selected via <see cref="Preset"/>. Bound from the <c>"Rollup"</c> section of
/// <c>appsettings.json</c>. A future settings page will flip <see cref="Preset"/>
/// live via <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>.
/// </summary>
/// <remarks>
/// Why only <see cref="Preset"/> is bound, not individual tier retentions:
/// letting users edit per-tier retention in config creates combinations that break
/// the tier-selection contract (e.g., a user setting <c>_10s</c> to 60 days would
/// shadow <c>_1m</c> for mid-range queries and waste query cost). The two presets
/// are hand-checked to leave the tier selector's routing intact. Full per-tier
/// customization is deferred to a future settings page, where UI validation can
/// show storage estimates and guard against invalid combinations.
/// </remarks>
internal sealed class RollupOptions {
    /// <summary>
    /// Default: <see cref="RetentionPreset.Balanced"/>. Power users can change this
    /// in <c>appsettings.json</c> before the settings page ships.
    /// </summary>
    public RetentionPreset Preset { get; set; } = RetentionPreset.Balanced;

    /// <summary>
    /// Ordered finest → coarsest. The rollup service walks adjacent pairs. Derived
    /// from <see cref="Preset"/> on every access; the per-preset lists are
    /// pre-computed static arrays so the switch is O(1).
    /// </summary>
    public IReadOnlyList<RollupTier> Tiers => Preset switch {
        RetentionPreset.Compact => CompactTiers,
        _ => BalancedTiers,
    };

    // Balanced: "keep plenty of history, don't micro-manage storage"
    // ~1.4 GB year 1 at ~100 active destinations, +~90 MB/year after.
    private static readonly IReadOnlyList<RollupTier> BalancedTiers = [
        new RollupTier(
            TableName: "traffic_raw",
            BucketSeconds: 1,
            Retention: TimeSpan.FromMinutes(10),
            RollupInterval: TimeSpan.FromSeconds(10)),
        new RollupTier(
            TableName: "traffic_buckets_10s",
            BucketSeconds: 10,
            Retention: TimeSpan.FromDays(7),
            RollupInterval: TimeSpan.FromMinutes(1)),
        new RollupTier(
            TableName: "traffic_buckets_1m",
            BucketSeconds: 60,
            Retention: TimeSpan.FromDays(14),
            RollupInterval: TimeSpan.FromMinutes(10)),
        new RollupTier(
            TableName: "traffic_buckets_10m",
            BucketSeconds: 600,
            Retention: TimeSpan.FromDays(365),
            RollupInterval: TimeSpan.FromHours(1)),
        new RollupTier(
            TableName: "traffic_buckets_1h",
            BucketSeconds: 3600,
            Retention: null,
            RollupInterval: TimeSpan.Zero),
    ];

    // Compact: "keep storage small, fine-grained history on recent data only"
    // ~580 MB year 1 at ~100 active destinations, +~90 MB/year after.
    private static readonly IReadOnlyList<RollupTier> CompactTiers = [
        new RollupTier(
            TableName: "traffic_raw",
            BucketSeconds: 1,
            Retention: TimeSpan.FromMinutes(10),
            RollupInterval: TimeSpan.FromSeconds(10)),
        new RollupTier(
            TableName: "traffic_buckets_10s",
            BucketSeconds: 10,
            Retention: TimeSpan.FromDays(3),
            RollupInterval: TimeSpan.FromMinutes(1)),
        new RollupTier(
            TableName: "traffic_buckets_1m",
            BucketSeconds: 60,
            Retention: TimeSpan.FromDays(7),
            RollupInterval: TimeSpan.FromMinutes(10)),
        new RollupTier(
            TableName: "traffic_buckets_10m",
            BucketSeconds: 600,
            Retention: TimeSpan.FromDays(90),
            RollupInterval: TimeSpan.FromHours(1)),
        new RollupTier(
            TableName: "traffic_buckets_1h",
            BucketSeconds: 3600,
            Retention: null,
            RollupInterval: TimeSpan.Zero),
    ];
}

/// <summary>
/// Named retention profiles for the rollup cascade. The future settings UI
/// binds to this enum via a radio-group control.
/// </summary>
internal enum RetentionPreset {
    /// <summary>
    /// Default. Generous per-tier retention; ~1.4 GB year-1 footprint at ~100
    /// active destinations. Sized for "I want full historical fidelity without
    /// thinking about storage".
    /// </summary>
    Balanced,

    /// <summary>
    /// Smaller footprint. ~580 MB year-1 at the cost of shorter zoom-in headroom
    /// on older data. Sized for "I'd rather pay less storage".
    /// </summary>
    Compact,
}

/// <summary>
/// One tier in the rollup cascade. <see cref="Retention"/> is null for the
/// terminal tier (meaning "never prune"); the rollup service treats null as
/// infinite retention and skips pruning that tier entirely.
/// </summary>
/// <param name="TableName">SQLite table name for this tier (e.g. <c>traffic_buckets_10s</c>).</param>
/// <param name="BucketSeconds">Duration of each stored bucket in this tier. 1 for raw, 10/60/600/3600 for rolled tiers.</param>
/// <param name="Retention">How long rows in this tier are kept before pruning. Null means never prune (infinite retention).</param>
/// <param name="RollupInterval">How often data from this tier is drained into the next. <see cref="TimeSpan.Zero"/> for the terminal tier (no further draining).</param>
internal sealed record RollupTier(
    string TableName,
    int BucketSeconds,
    TimeSpan? Retention,
    TimeSpan RollupInterval);
