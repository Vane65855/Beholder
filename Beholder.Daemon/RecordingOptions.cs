namespace Beholder.Daemon;

/// <summary>
/// Controls what traffic the daemon ingests. Bound from the
/// <c>"Recording"</c> section of <c>appsettings.json</c> via
/// <c>IOptionsMonitor&lt;RecordingOptions&gt;</c> so live reload takes effect
/// on the next flow event without a daemon restart.
/// </summary>
internal sealed class RecordingOptions {
    /// <summary>
    /// When true (default), Beholder's own processes (Beholder.Daemon and
    /// Beholder.Ui) are excluded from capture. The daemon-UI gRPC chatter
    /// generates roughly 50 MB over 30 days at default retention and tells
    /// the user nothing useful. Set to false to record everything, including
    /// self-traffic — useful for debugging or data-hoarding users.
    ///
    /// Future phases may add more granular controls (specific process paths,
    /// localhost-only filter, port ranges). This single flag is the v1 surface.
    /// </summary>
    public bool FilterSelfTraffic { get; set; } = true;
}
