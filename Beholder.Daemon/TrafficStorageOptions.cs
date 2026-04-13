namespace Beholder.Daemon;

/// <summary>
/// Configuration for the traffic storage engine. Controls bucket resolution,
/// retention periods, and in-memory eviction timeouts. Bound from the
/// <c>[TrafficStorage]</c> section of the daemon configuration.
/// </summary>
internal sealed class TrafficStorageOptions {
    /// <summary>How many days of traffic data to retain before pruning. Default: 30.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>How often (in hours) the prune job runs. Default: 1.</summary>
    public int PruneIntervalHours { get; set; } = 1;

    /// <summary>Duration of each storage bucket in seconds. Default: 10.</summary>
    public int BucketSeconds { get; set; } = 10;

    /// <summary>
    /// Minutes of inactivity before a destination aggregate is evicted from memory.
    /// Any un-flushed bucket bytes are persisted to SQLite before eviction. Default: 5.
    /// </summary>
    public int IdleDestinationTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// Hours of inactivity before a process's session-scoped lifetime totals are
    /// evicted from memory. Prevents unbounded growth from short-lived processes.
    /// Default: 1.
    /// </summary>
    public int IdleProcessTimeoutHours { get; set; } = 1;
}
