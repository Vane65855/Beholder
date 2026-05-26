using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// Windows implementation of <see cref="ILanDeviceProbe"/>. Two-pass scan:
///
/// <list type="number">
/// <item><b>Fast pass</b> reads the OS's existing IPv4 ARP / neighbor cache
/// via <see cref="IphlpapiInterop.TryEnumerateIpv4ArpCache"/>. On a typical
/// LAN where every device talks to the gateway periodically, this catches
/// 80-100% of devices instantly with zero packets sent.</item>
/// <item><b>Slow pass</b> issues parallel <c>SendARP</c> requests via
/// <see cref="ArpScanProbe.ProbeIpsAsync"/> for subnet IPs missing from the
/// cache. Bounded parallelism (64 concurrent) + a 60 s deadline keeps the
/// pathological cold-cache case to ~5 s wall-clock on a /24 instead of the
/// ~4 minutes the original Phase 9.2 sequential scan took.</item>
/// </list>
///
/// mDNS + NetBIOS hostname-resolution sub-probes plug into the same scanner
/// during Phase 9.2.5 (they fill in <see cref="LanDeviceObservation.Hostname"/>
/// which stays null in 9.2 / 9.2.1).
/// </summary>
/// <remarks>
/// Subnet discovery uses the cross-platform <see cref="NetworkInterface"/>
/// API rather than P/Invoke — the .NET surface covers NIC enumeration
/// completely, leaving P/Invoke for operations that genuinely require it
/// (ARP probes via <c>SendARP</c>, ARP cache walk via <c>GetIpNetTable2</c>,
/// and future mDNS / NetBIOS calls).
/// </remarks>
public sealed class WindowsLanDeviceProbe : ILanDeviceProbe {
    private readonly ArpScanProbe _arpProbe;
    private readonly HostnameResolutionLadder? _hostnameResolutionLadder;
    private readonly MdnsServiceDiscoveryProbe? _mdnsServiceDiscoveryProbe;
    private readonly IScannerSettingsState? _scannerSettings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WindowsLanDeviceProbe> _logger;

    public WindowsLanDeviceProbe(
        ArpScanProbe arpProbe,
        TimeProvider timeProvider,
        ILogger<WindowsLanDeviceProbe> logger,
        HostnameResolutionLadder? hostnameResolutionLadder = null,
        MdnsServiceDiscoveryProbe? mdnsServiceDiscoveryProbe = null,
        IScannerSettingsState? scannerSettings = null
    ) {
        ArgumentNullException.ThrowIfNull(arpProbe);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _arpProbe = arpProbe;
        // Phase 13.4: probes are always injected (no longer null-gated at
        // construction). The IScannerSettingsState toggle gates whether they
        // actually run per scan, so the SetScannerSettings RPC takes effect
        // on the next scan tick. When the state singleton is null (test
        // fixtures that don't exercise the toggle), the probes themselves
        // determine whether they run via their nullable refs — preserves
        // the pre-13.4 test contract.
        _hostnameResolutionLadder = hostnameResolutionLadder;
        _mdnsServiceDiscoveryProbe = mdnsServiceDiscoveryProbe;
        _scannerSettings = scannerSettings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private bool ShouldRunHostnameResolution()
        => _scannerSettings?.EnableHostnameResolution ?? true;

    public async Task<IReadOnlyList<LanDeviceObservation>> ScanAsync(CancellationToken cancellationToken) {
        var subnet = TryGetPrimarySubnet();
        if (subnet is null) {
            _logger.LogWarning(
                "LAN scan skipped: no active NIC with a default gateway (no LAN-attached interface)");
            return [];
        }
        var (network, mask) = subnet.Value;

        // Fast pass: read Windows' ARP cache, filter to our subnet.
        var cacheEntries = IphlpapiInterop.TryEnumerateIpv4ArpCache(_logger)
            .Where(e => ArpScanProbe.IsInSubnet(e.Ip, network, mask))
            .ToList();
        var cachedIps = cacheEntries.Select(e => e.Ip).ToHashSet();

        // Slow pass: parallel SendARP for IPs in the subnet not already in the cache.
        var uncachedIps = ArpScanProbe.EnumerateHostAddresses(network, mask)
            .Where(ip => !cachedIps.Contains(ip));
        var probedResults = await _arpProbe.ProbeIpsAsync(uncachedIps, cancellationToken)
            .ConfigureAwait(false);

        // Merge by IP-string. Cache wins on collisions — its entries reflect
        // what Windows currently considers reachable, and any genuine MAC
        // change between cache and a fresh SendARP will be detected by
        // LanScannerService.ProcessObservationAsync via its GetByIpAsync
        // lookup against the previous scan's persisted row anyway.
        var now = _timeProvider.GetUtcNow();
        var merged = new Dictionary<string, LanDeviceObservation>(StringComparer.Ordinal);
        foreach (var probed in probedResults) {
            merged[probed.Ip.ToString()] = new LanDeviceObservation(
                Mac: probed.Mac, Ip: probed.Ip.ToString(), Hostname: null, ObservedAt: now);
        }
        foreach (var cached in cacheEntries) {
            merged[cached.Ip.ToString()] = new LanDeviceObservation(
                Mac: cached.Mac, Ip: cached.Ip.ToString(), Hostname: null, ObservedAt: now);
        }

        // Phase 9.2.6: mDNS service-discovery browse pass. One batched
        // multicast browse covers the whole subnet — devices that
        // advertise any of the curated well-known service types reply
        // with PTR + SRV + A records the parser correlates into a
        // (source-IP → hostname) pair. Runs first because (a) it's a
        // single multicast send-and-wait rather than per-IP and (b) it
        // catches the modern majority of Bonjour-style LAN devices.
        // Skipped entirely when the kill-switch is disabled.
        var hostnameResolutionEnabled = ShouldRunHostnameResolution();

        var sdHits = 0;
        if (hostnameResolutionEnabled && _mdnsServiceDiscoveryProbe is not null && merged.Count > 0) {
            var sdHostnames = await _mdnsServiceDiscoveryProbe.BrowseAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var ip in merged.Keys.ToList()) {
                if (sdHostnames.TryGetValue(ip, out var name)) {
                    merged[ip] = merged[ip] with { Hostname = name };
                    sdHits++;
                }
            }
        }

        // Phase 9.2.5: per-IP mDNS-PTR + NetBIOS ladder. Now restricted
        // to observations the SD browse couldn't resolve — devices that
        // don't advertise any of the queried services (often Windows
        // machines with NetBIOS still enabled, plus residual NAT/minimal
        // home routers). Same dictionary patch as the SD pass.
        var ladderHits = 0;
        if (hostnameResolutionEnabled && _hostnameResolutionLadder is not null) {
            var ipsNeedingLadder = merged.Values
                .Where(o => string.IsNullOrEmpty(o.Hostname))
                .Select(o => IPAddress.Parse(o.Ip))
                .ToList();
            if (ipsNeedingLadder.Count > 0) {
                var ladderHostnames = await _hostnameResolutionLadder
                    .ResolveAllAsync(ipsNeedingLadder, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var ip in merged.Keys.ToList()) {
                    if (string.IsNullOrEmpty(merged[ip].Hostname)
                        && ladderHostnames.TryGetValue(ip, out var name)) {
                        merged[ip] = merged[ip] with { Hostname = name };
                        ladderHits++;
                    }
                }
            }
        }

        _logger.LogDebug(
            "LAN scan pass: cache hits {CacheHits}, parallel probes {ProbedCount}, merged {TotalObservations}, hostnames-via-SD {SdHits}, hostnames-via-ladder {LadderHits}",
            cacheEntries.Count, probedResults.Count, merged.Count, sdHits, ladderHits);

        return merged.Values.ToList();
    }

    /// <summary>
    /// Picks the primary IPv4 NIC — first interface that is
    /// <see cref="OperationalStatus.Up"/>, has at least one default gateway,
    /// and has an IPv4 unicast address with a subnet mask. Loopback, tunnel,
    /// and PPP interfaces are skipped.
    /// </summary>
    private static (IPAddress Network, IPAddress Mask)? TryGetPrimarySubnet() {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel
                or NetworkInterfaceType.Ppp
                or NetworkInterfaceType.Unknown) continue;

            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.Count == 0) continue;

            foreach (var ua in props.UnicastAddresses) {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask is null || ua.IPv4Mask.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask.GetAddressBytes().All(b => b == 0)) continue;  // skip degenerate 0.0.0.0 mask

                var addrBytes = ua.Address.GetAddressBytes();
                var maskBytes = ua.IPv4Mask.GetAddressBytes();
                var networkBytes = new byte[4];
                for (var i = 0; i < 4; i++) networkBytes[i] = (byte)(addrBytes[i] & maskBytes[i]);
                return (new IPAddress(networkBytes), ua.IPv4Mask);
            }
        }
        return null;
    }
}
