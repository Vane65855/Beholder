namespace Beholder.Daemon;

/// <summary>
/// Governs the traffic engine's in-memory working set. These knobs control how
/// long the engine keeps per-destination and per-process state alive before
/// evicting it — they are NOT about how long data is persisted on disk.
/// </summary>
/// <remarks>
/// Storage cadence and retention live in <see cref="RollupOptions"/>. The engine
/// flushes raw buckets every second; the rollup service cascades them through
/// coarser tiers and handles per-tier pruning. See <c>docs/ARCHITECTURE.md</c>
/// "Storage Rollup Architecture" for the full data flow.
/// </remarks>
internal sealed class TrafficStorageOptions {
    /// <summary>
    /// Minutes of inactivity before a destination aggregate is evicted from
    /// memory. Any un-flushed bucket bytes are persisted to SQLite before
    /// eviction. Default: 5.
    /// </summary>
    public int IdleDestinationTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// Hours of inactivity before a process's session-scoped lifetime totals
    /// are evicted from memory. Prevents unbounded growth from short-lived
    /// processes. Default: 1.
    /// </summary>
    public int IdleProcessTimeoutHours { get; set; } = 1;
}
