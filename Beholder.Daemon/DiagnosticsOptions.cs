namespace Beholder.Daemon;

/// <summary>
/// Controls the opt-in diagnostic sampler used for performance soaks (Phase 12.3).
/// Bound from the <c>"Diagnostics"</c> section of <c>appsettings.json</c>. Off by
/// default so production logs stay quiet; enable it to capture a memory / GC /
/// database-size trend over a long run. See <c>docs/manual-tests/perf-soak.md</c>.
/// </summary>
internal sealed class DiagnosticsOptions {
    /// <summary>When true, the sampler logs a resource snapshot every <see cref="IntervalSeconds"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seconds between samples (clamped to at least 1). Default 60.</summary>
    public int IntervalSeconds { get; set; } = 60;
}
