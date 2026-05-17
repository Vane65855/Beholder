namespace Beholder.Daemon;

/// <summary>
/// Controls LAN device discovery scan cadence. Bound from the <c>"Scanner"</c>
/// section of <c>appsettings.json</c> via <c>IOptionsMonitor&lt;ScannerOptions&gt;</c>;
/// changes take effect on the next scan tick without a daemon restart.
/// </summary>
internal sealed class ScannerOptions {
    /// <summary>
    /// How often the LAN scanner probes the local subnet for new or updated
    /// devices. Default 300 seconds (5 minutes). Power users on busy networks
    /// can reduce to ~60 seconds for faster discovery at the cost of more
    /// ambient ARP traffic. Values below the floor (30 seconds) are clamped
    /// at scheduler-startup time: a /24 takes ~1.3 s to probe end-to-end
    /// (256 IPs × 5 ms inter-probe delay), and a 30 s floor leaves
    /// comfortable headroom.
    /// </summary>
    public int ScanIntervalSeconds { get; set; } = 300;
}
