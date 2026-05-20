using System.Net;
using System.Net.Sockets;
using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// Hostname probe per Phase 9.5 / ADR 009. Uses the OS reverse-DNS resolver
/// (via the existing <see cref="IReverseDnsResolver"/> abstraction from
/// ADR 005) to look up the PTR record for a LAN IP. On most home networks
/// the configured DNS server is the router itself, and the router publishes
/// the DHCP-supplied hostname for each lease in its LAN DNS resolver — so
/// this probe picks up names like the Samsung S25's DHCP option 12
/// "device name" that mDNS-PTR / NetBIOS / DNS-SD all miss.
/// </summary>
/// <remarks>
/// <para>
/// Slots into the existing <see cref="HostnameResolutionLadder"/> after
/// <see cref="MdnsHostnameProbe"/> and <see cref="NetbiosHostnameProbe"/> —
/// priority order is "device's own advertisement beats the router's view of
/// it." Same kill-switch (<see cref="ScannerOptions.EnableHostnameResolution"/>)
/// disables this probe along with the rest of the ladder.
/// </para>
/// <para>
/// <b>Hit rate is router-dependent.</b> Many home routers (recent OpenWrt /
/// pfSense / FritzBox / many ISP-issued boxes) auto-populate their LAN DNS
/// from DHCP option 12 hostnames and answer PTR queries for LAN IPs. Other
/// routers don't (some HUAWEI / older budget models), in which case this
/// probe silently returns null and the next probe in the ladder runs. No
/// router-specific configuration is needed — we just ask the OS resolver
/// and accept whatever comes back.
/// </para>
/// <para>
/// <b>Privacy posture:</b> identical to ADR 005's
/// <c>ReverseDnsFallbackCache</c> for outbound-traffic hostnames — we use the
/// OS resolver, which on standard configurations routes private-range PTR
/// queries to the LAN router per RFC 6303 rather than leaking to public DNS.
/// Users who want strict "no DNS lookups" mode set
/// <c>ScannerOptions.EnableHostnameResolution = false</c>, which disables
/// the entire hostname ladder including this probe.
/// </para>
/// </remarks>
public sealed class RouterDnsHostnameProbe : IHostnameProbe {
    /// <summary>
    /// 2 seconds is 2× the mDNS / NetBIOS per-probe timeout — the OS
    /// resolver is slower because it serialises through the system DNS
    /// client and (on a cold cache) may have to fall through a chain of
    /// configured servers. Still safe inside the 60-second per-ladder
    /// deadline that <see cref="HostnameResolutionLadder"/> enforces.
    /// </summary>
    private const int PerProbeTimeoutMs = 2000;

    private readonly IReverseDnsResolver _resolver;
    private readonly ILogger<RouterDnsHostnameProbe> _logger;

    public RouterDnsHostnameProbe(
        IReverseDnsResolver resolver,
        ILogger<RouterDnsHostnameProbe> logger
    ) {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
        _logger = logger;
    }

    public string ProtocolName => "RouterDNS";

    public async Task<string?> ResolveAsync(IPAddress ip, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork) return null;

        using var timeoutCts = new CancellationTokenSource(PerProbeTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        try {
            var hostname = await _resolver.ResolveAsync(ip, linkedCts.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(hostname) ? null : hostname;
        } catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                && !cancellationToken.IsCancellationRequested) {
            // Per-probe timeout — router probably doesn't publish a PTR for
            // this IP, or the resolver chain is slow. Silent miss, next probe
            // in the ladder (if any) gets a turn.
            return null;
        }
    }
}
