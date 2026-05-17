using System.Net;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// Sweeps a single IPv4 subnet by issuing a sequential ARP request for every
/// host address. One probe at a time with a small inter-probe delay — bounded
/// total scan time (~1.3 s on a /24) and no bursty pattern that an IDS might
/// flag as a port-scanner heuristic.
/// </summary>
public sealed class ArpScanProbe {
    /// <summary>Delay between consecutive SendARP calls; ~1.3 s for a /24.</summary>
    private const int ArpProbeDelayMs = 5;

    /// <summary>
    /// Per-IP timeout for <see cref="IphlpapiInterop.TrySendArp"/>. Reserved
    /// for the API surface; Windows internally bounds SendARP at ~3 s.
    /// </summary>
    private const int ArpResponseTimeoutMs = 1000;

    public async Task<IReadOnlyList<ArpResult>> ScanSubnetAsync(
        IPAddress networkAddress,
        IPAddress subnetMask,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(networkAddress);
        ArgumentNullException.ThrowIfNull(subnetMask);

        var hostAddresses = EnumerateHostAddresses(networkAddress, subnetMask);
        var results = new List<ArpResult>(capacity: 32);

        foreach (var ip in hostAddresses) {
            cancellationToken.ThrowIfCancellationRequested();
            var mac = IphlpapiInterop.TrySendArp(ip, ArpResponseTimeoutMs);
            if (mac is not null) results.Add(new ArpResult(ip, mac));
            await Task.Delay(ArpProbeDelayMs, cancellationToken).ConfigureAwait(false);
        }
        return results;
    }

    /// <summary>
    /// Enumerates all usable host addresses inside the subnet defined by
    /// <paramref name="networkAddress"/> + <paramref name="subnetMask"/>,
    /// skipping the network and broadcast addresses. Caps the enumeration at
    /// 4096 hosts as a defensive ceiling: a /20 has 4094 hosts (the largest
    /// practical home/SMB subnet); anything larger would imply a corporate
    /// LAN where ARP-sweeping every host is impolite and the user should
    /// configure a more targeted scope (deferred to a future ScannerOptions
    /// hook).
    /// </summary>
    public static IEnumerable<IPAddress> EnumerateHostAddresses(
        IPAddress networkAddress, IPAddress subnetMask
    ) {
        if (networkAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) yield break;
        if (subnetMask.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) yield break;

        var networkInt = ToUInt32BigEndian(networkAddress.GetAddressBytes());
        var maskInt = ToUInt32BigEndian(subnetMask.GetAddressBytes());
        var network = networkInt & maskInt;
        var broadcast = network | ~maskInt;

        var hostCount = (long)broadcast - network - 1;  // exclude network + broadcast
        if (hostCount <= 0) yield break;

        const long MaxHostsPerScan = 4094;  // /20 ceiling — defensive
        if (hostCount > MaxHostsPerScan) hostCount = MaxHostsPerScan;

        for (long i = 1; i <= hostCount; i++) {
            var hostInt = network + (uint)i;
            yield return new IPAddress(FromUInt32BigEndian(hostInt));
        }
    }

    private static uint ToUInt32BigEndian(byte[] bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static byte[] FromUInt32BigEndian(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    public sealed record ArpResult(IPAddress Ip, string Mac);
}
