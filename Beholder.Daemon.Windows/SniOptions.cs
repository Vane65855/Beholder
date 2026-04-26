namespace Beholder.Daemon.Windows;

/// <summary>
/// Tunables for the Pktmon-backed SNI capture source. Sized for the
/// hot-path decoupling between the ETW drain thread and the SNI worker
/// (see <see cref="PktmonSniSource"/>) and for the dedup window over
/// PktGroupIds.
/// </summary>
/// <remarks>
/// Public so <c>IOptionsMonitor&lt;SniOptions&gt;</c> binding in
/// <c>Beholder.Daemon/Program.cs</c> can reference it across the assembly
/// boundary. The equivalent Linux SNI mechanism would ship its own options
/// class when that platform is added.
/// </remarks>
public sealed class SniOptions {
    /// <summary>
    /// Whether the daemon attempts to capture TLS ClientHello SNI extensions
    /// from outbound TCP/443 traffic via Microsoft-Windows-PktMon ETW. Default
    /// <c>true</c>. Disable to skip the entire SNI source — the daemon still
    /// resolves via Windows DNS observation and reverse-DNS fallback. See
    /// ADR 006.
    /// </summary>
    public bool EnableSniCapture { get; set; } = true;

    /// <summary>
    /// Max number of packet snapshots queued between the ETW drain thread
    /// and the SNI parsing worker. Each snapshot is a small struct plus a
    /// managed byte[] copy of the captured packet (capped at the
    /// Pktmon-truncated size — typically &lt; 1500 bytes for a full
    /// ClientHello). 50000 capacity gives ~75 MB headroom worst-case.
    /// On overflow the oldest entry is discarded
    /// (<c>BoundedChannelFullMode.DropOldest</c>).
    /// </summary>
    public int QueueCapacity { get; set; } = 50_000;

    /// <summary>
    /// ETW session buffer size in MB for the Pktmon subscription. Pktmon
    /// emits ~600 packet events per second on a moderately-loaded machine
    /// (per the Phase 0 probe); 16 MB gives plenty of in-kernel headroom
    /// before the consumer needs to catch up. Same default as
    /// <c>DnsOptions.SessionBufferSizeMB</c>.
    /// </summary>
    public int SessionBufferSizeMB { get; set; } = 16;

    /// <summary>
    /// How many recent <c>PktGroupId</c> values to remember when
    /// deduplicating events. Each packet captured by Pktmon appears ~4
    /// times at different NDIS components (per the Phase 0 probe — same
    /// PktGroupId across all appearances). 16 384 lets us keep ~2 minutes
    /// of recent IDs at 600/sec, comfortably exceeding the
    /// "see all duplicates of the same packet" window which is at most a
    /// few hundred microseconds.
    /// </summary>
    public int DedupCapacity { get; set; } = 16_384;
}
