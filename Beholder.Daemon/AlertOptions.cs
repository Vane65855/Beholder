namespace Beholder.Daemon;

/// <summary>
/// Controls the Phase 7 alert detector pipeline. Bound from the
/// <c>"Alert"</c> section of <c>appsettings.json</c> via
/// <c>IOptionsMonitor&lt;AlertOptions&gt;</c> so live reload takes effect on
/// the next detector tick without a daemon restart.
/// </summary>
internal sealed class AlertOptions {
    /// <summary>
    /// Master kill-switch for the <c>NewProcess</c> detector. When false the
    /// detector still subscribes to traffic events but emits no alerts —
    /// preserves the cheap subscription so flipping the flag back on takes
    /// effect on the next event without a restart.
    /// </summary>
    public bool EnableNewProcessDetection { get; set; } = true;

    /// <summary>
    /// Master kill-switch for the periodic binary-hash monitor that emits
    /// <c>HashChanged</c> alerts when a tracked binary's SHA-256 differs from
    /// the previously stored value.
    /// </summary>
    public bool EnableHashChangeDetection { get; set; } = true;

    /// <summary>
    /// Master kill-switch for the chain-integrity monitor that emits
    /// <c>ChainError</c> alerts on startup (mandatory) and periodically when
    /// <see cref="Beholder.Core.IEventStore.VerifyAsync"/> fails.
    /// </summary>
    public bool EnableChainIntegrityMonitor { get; set; } = true;

    /// <summary>
    /// How often the binary-hash monitor re-hashes registered binaries.
    /// Default 60 minutes — power users can drop the interval to ~1 minute
    /// for faster cold-start coverage at the cost of more disk I/O. Cold-
    /// start latency: a freshly-registered binary will not be hashed until
    /// the next periodic tick (up to this many minutes later).
    /// </summary>
    public int BinaryHashCheckIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// How often the chain-integrity monitor re-runs
    /// <see cref="Beholder.Core.IEventStore.VerifyAsync"/> after the mandatory
    /// startup verification. Verification is O(n) over the chain today
    /// (Phase 11 will add checkpointing), so the default cadence is hourly.
    /// </summary>
    public int ChainVerifyIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Per-file hash budget for the binary-hash monitor. Files that exceed
    /// this skip rather than block the entire monitor loop — antivirus locks
    /// or unusually large binaries should not stall the rest of the registry.
    /// Default 5 seconds.
    /// </summary>
    public int MaxFileHashTimeoutSeconds { get; set; } = 5;
}
