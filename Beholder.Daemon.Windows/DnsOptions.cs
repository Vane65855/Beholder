namespace Beholder.Daemon.Windows;

/// <summary>
/// Tunables for the Windows DNS observation layer. Sizes the in-memory queue
/// between the ETW callback and the hostname-cache consumer (see
/// <see cref="EtwDnsCache"/>) and the underlying ETW session's kernel ring.
/// </summary>
/// <remarks>
/// Under load (boot storms, browser prefetch bursts, pathological reconnect
/// loops) the ETW drain thread can fall behind the kernel. The session drops
/// events silently if its ring fills; we drop the oldest queued event if our
/// bounded channel fills. Both paths are instrumented: the 30 s metrics timer
/// in <see cref="EtwDnsCache"/> logs non-zero <c>EventsLost</c> and channel
/// drops with structured fields so real-world misses are visible instead of
/// invisible.
/// <para>
/// Public so <c>IOptionsMonitor&lt;DnsOptions&gt;</c> binding in
/// <c>Beholder.Daemon/Program.cs</c> can reference it across the assembly
/// boundary. The equivalent Linux DNS mechanism (netlink or nscd) would ship
/// its own options class when that platform is added.
/// </para>
/// </remarks>
public sealed class DnsOptions {
    /// <summary>
    /// Max number of DNS events queued between the ETW drain thread and the
    /// cache consumer. Each entry holds two string references plus the strings
    /// themselves (query name and answer list) — roughly 300-350 B per entry
    /// worst case. The default 150 000 gives ~50 MB of in-flight headroom,
    /// enough to absorb the first boot-storm post-login without drops. On
    /// overflow the oldest entry is discarded (<c>BoundedChannelFullMode.DropOldest</c>)
    /// and the drop count is logged once per 30 s window.
    /// </summary>
    public int QueueCapacity { get; set; } = 150_000;

    /// <summary>
    /// ETW session buffer size in MB. The session's kernel-side ring sits
    /// between Windows and our user-mode consumer; a bigger ring absorbs
    /// kernel-side spikes before the consumer needs to catch up. Default 16 MB
    /// — well above the TraceEvent library default and enough headroom that a
    /// single thread-scheduling hiccup on our drain thread doesn't translate
    /// into <c>EventsLost</c>.
    /// </summary>
    public int SessionBufferSizeMB { get; set; } = 16;

    /// <summary>
    /// Whether the daemon attempts to preload its hostname cache from the
    /// Windows DNS resolver cache at startup via the undocumented
    /// <c>DnsGetCacheDataTable</c> export. Default <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Kill-switch for environments where the underlying P/Invoke
    /// misbehaves. The Win11 22H2+ behaviour where the legacy export
    /// returns <c>ERROR_INVALID_FUNCTION</c> is logged-and-skipped already,
    /// so this flag exists for the harder failure mode: a future Windows
    /// update that surfaces a different signature or a process-isolation
    /// rule that makes the call itself dangerous. Set to <c>false</c> in
    /// <c>appsettings.json</c> under section <c>"Dns"</c> (or via env var
    /// <c>Dns__EnablePreload=false</c>) to skip the preload entirely. See
    /// <c>docs/decisions/004-dns-cache-preload-undocumented-api.md</c>.
    /// </remarks>
    public bool EnablePreload { get; set; } = true;
}
