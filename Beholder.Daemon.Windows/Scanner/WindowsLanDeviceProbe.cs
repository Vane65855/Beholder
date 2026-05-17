using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// Windows implementation of <see cref="ILanDeviceProbe"/>. For Phase 9.2 this
/// orchestrates the ARP probe only; mDNS + NetBIOS sub-probes plug in here
/// during 9.2.5 (the same orchestrator gains two more parallel layers that
/// fill in <see cref="LanDeviceObservation.Hostname"/> when available).
/// </summary>
/// <remarks>
/// Discovers the local primary IPv4 subnet via the cross-platform
/// <see cref="NetworkInterface"/> API rather than P/Invoke — the .NET surface
/// covers NIC enumeration completely, leaving P/Invoke for the operations
/// that genuinely require it (ARP requests, mDNS callbacks, NetBIOS queries).
/// </remarks>
public sealed class WindowsLanDeviceProbe : ILanDeviceProbe {
    private readonly ArpScanProbe _arpProbe;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WindowsLanDeviceProbe> _logger;

    public WindowsLanDeviceProbe(
        ArpScanProbe arpProbe,
        TimeProvider timeProvider,
        ILogger<WindowsLanDeviceProbe> logger
    ) {
        ArgumentNullException.ThrowIfNull(arpProbe);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _arpProbe = arpProbe;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LanDeviceObservation>> ScanAsync(CancellationToken cancellationToken) {
        var subnet = TryGetPrimarySubnet();
        if (subnet is null) {
            _logger.LogWarning(
                "LAN scan skipped: no active NIC with a default gateway (no LAN-attached interface)");
            return [];
        }

        var (network, mask) = subnet.Value;
        var arpResults = await _arpProbe.ScanSubnetAsync(network, mask, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        return arpResults
            .Select(r => new LanDeviceObservation(
                Mac: r.Mac, Ip: r.Ip.ToString(), Hostname: null, ObservedAt: now))
            .ToList();
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
