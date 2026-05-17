using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// Probes a set of IPv4 addresses by issuing parallel ARP requests via
/// <see cref="IphlpapiInterop.TrySendArp"/>. The caller (typically
/// <see cref="WindowsLanDeviceProbe"/>) decides which IPs to probe — usually
/// just the addresses missing from the OS's existing ARP cache, since the
/// cache walk is faster.
/// </summary>
/// <remarks>
/// <para>
/// Why parallel: <c>SendARP</c> is documented as "waits for a response."
/// Windows internally holds the call for ~1 s per unresponsive IP before
/// giving up. Single-threaded across a /24 with mostly-unresponsive hosts
/// took ~4 minutes in Phase 9.2 smoke testing; parallel with
/// <see cref="MaxParallelProbes"/> = 64 reduces wall-clock to ~5 s. The
/// per-scan <see cref="PerScanDeadline"/> caps wall-clock for pathological
/// subnets and returns partial results on expiry.
/// </para>
/// <para>
/// Subnet math (<see cref="EnumerateHostAddresses"/>, <see cref="IsInSubnet"/>)
/// lives on this class as static helpers because they're tightly coupled to
/// the probe's input shape — both deal in (network, mask) and IP-set
/// arithmetic.
/// </para>
/// </remarks>
public sealed class ArpScanProbe {
    /// <summary>Per-call timeout — Windows ignores it for SendARP, kept for API symmetry.</summary>
    private const int ArpResponseTimeoutMs = 1000;

    /// <summary>
    /// Concurrent <c>SendARP</c> calls. 64 chosen empirically: SendARP is
    /// I/O-bound (the thread blocks for ~1 s on unresponsive IPs), so the
    /// thread-pool can comfortably handle this many concurrent blocks.
    /// Wall-clock for a /24 with all-unresponsive cache-miss IPs:
    /// 254/64 × ~1 s ≈ 4 s.
    /// </summary>
    private const int MaxParallelProbes = 64;

    /// <summary>
    /// Defensive ceiling on a single <see cref="ProbeIpsAsync"/> wall-clock.
    /// A pathological subnet (e.g. /16 with no responsive devices) would
    /// otherwise tie up the scheduler indefinitely. On deadline, return
    /// whatever partial results came in.
    /// </summary>
    private static readonly TimeSpan DefaultPerScanDeadline = TimeSpan.FromSeconds(60);

    private const long MaxHostsPerScan = 4094;  // /20 ceiling — defensive

    private readonly Func<IPAddress, int, string?> _probeFunc;
    private readonly TimeSpan _perScanDeadline;

    /// <summary>Production constructor: routes probes through <see cref="IphlpapiInterop.TrySendArp"/>.</summary>
    public ArpScanProbe() : this(IphlpapiInterop.TrySendArp, DefaultPerScanDeadline) { }

    /// <summary>
    /// Test-only constructor allowing injection of a fake probe delegate and
    /// a shorter deadline for deterministic parallelism + deadline-expiry
    /// tests. Mirrors the test-injection pattern from Phase 8's
    /// <c>HeatmapPalette</c> internal constructor.
    /// </summary>
    internal ArpScanProbe(Func<IPAddress, int, string?> probeFunc, TimeSpan perScanDeadline) {
        ArgumentNullException.ThrowIfNull(probeFunc);
        _probeFunc = probeFunc;
        _perScanDeadline = perScanDeadline;
    }

    /// <summary>
    /// Issues parallel ARP probes for each IP in <paramref name="targetIps"/>
    /// and returns the responders. Honors <paramref name="cancellationToken"/>;
    /// also enforces an internal deadline (default 60 s) that produces partial
    /// results on expiry rather than throwing.
    /// </summary>
    public async Task<IReadOnlyList<ArpResult>> ProbeIpsAsync(
        IEnumerable<IPAddress> targetIps,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(targetIps);

        using var deadlineCts = new CancellationTokenSource(_perScanDeadline);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadlineCts.Token);

        var results = new ConcurrentBag<ArpResult>();
        try {
            await Parallel.ForEachAsync(targetIps, new ParallelOptions {
                MaxDegreeOfParallelism = MaxParallelProbes,
                CancellationToken = linkedCts.Token,
            }, (ip, ct) => {
                ct.ThrowIfCancellationRequested();
                var mac = _probeFunc(ip, ArpResponseTimeoutMs);
                if (mac is not null) results.Add(new ArpResult(ip, mac));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        } catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested
                                                && !cancellationToken.IsCancellationRequested) {
            // Deadline expired before the caller cancelled — return whatever
            // we collected so far. If the OUTER cancellation token fired, the
            // exception propagates (the caller asked us to stop).
        }
        return results.ToList();
    }

    /// <summary>
    /// Enumerates all usable host addresses inside the subnet defined by
    /// <paramref name="networkAddress"/> + <paramref name="subnetMask"/>,
    /// skipping the network and broadcast addresses. Caps at 4094 hosts
    /// (a /20 — the largest practical home/SMB subnet); anything larger
    /// would imply a corporate LAN where exhaustive ARP-sweeping is
    /// impolite and a more targeted scope should be configured (deferred
    /// to a future ScannerOptions hook).
    /// </summary>
    public static IEnumerable<IPAddress> EnumerateHostAddresses(
        IPAddress networkAddress, IPAddress subnetMask
    ) {
        ArgumentNullException.ThrowIfNull(networkAddress);
        ArgumentNullException.ThrowIfNull(subnetMask);
        if (networkAddress.AddressFamily != AddressFamily.InterNetwork) yield break;
        if (subnetMask.AddressFamily != AddressFamily.InterNetwork) yield break;

        var networkInt = ToUInt32BigEndian(networkAddress.GetAddressBytes());
        var maskInt = ToUInt32BigEndian(subnetMask.GetAddressBytes());
        var network = networkInt & maskInt;
        var broadcast = network | ~maskInt;

        var hostCount = (long)broadcast - network - 1;  // exclude network + broadcast
        if (hostCount <= 0) yield break;
        if (hostCount > MaxHostsPerScan) hostCount = MaxHostsPerScan;

        for (long i = 1; i <= hostCount; i++) {
            var hostInt = network + (uint)i;
            yield return new IPAddress(FromUInt32BigEndian(hostInt));
        }
    }

    /// <summary>
    /// True when <paramref name="ip"/> falls inside the subnet defined by
    /// <paramref name="networkAddress"/> + <paramref name="subnetMask"/>.
    /// Used to filter the ARP-cache walk's results down to our primary NIC's
    /// subnet (the cache may contain entries for other interfaces, VPN
    /// remotes, etc.). Returns <see langword="false"/> for any non-IPv4
    /// input rather than throwing (function-style — matches the
    /// <c>yield break</c> pattern in <see cref="EnumerateHostAddresses"/>).
    /// </summary>
    public static bool IsInSubnet(IPAddress ip, IPAddress networkAddress, IPAddress subnetMask) {
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentNullException.ThrowIfNull(networkAddress);
        ArgumentNullException.ThrowIfNull(subnetMask);
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        if (networkAddress.AddressFamily != AddressFamily.InterNetwork) return false;
        if (subnetMask.AddressFamily != AddressFamily.InterNetwork) return false;

        var ipBytes = ip.GetAddressBytes();
        var netBytes = networkAddress.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        for (var i = 0; i < 4; i++) {
            if ((ipBytes[i] & maskBytes[i]) != (netBytes[i] & maskBytes[i])) return false;
        }
        return true;
    }

    private static uint ToUInt32BigEndian(byte[] bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static byte[] FromUInt32BigEndian(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    public sealed record ArpResult(IPAddress Ip, string Mac);
}
