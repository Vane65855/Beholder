using System.Net;
using Beholder.Daemon.Windows.Scanner;

namespace Beholder.Tests;

public sealed class ArpScanProbeTests {
    // --- IsInSubnet ---

    [Fact]
    public void IsInSubnet_IpInside24_ReturnsTrue() {
        var inside = ArpScanProbe.IsInSubnet(
            IPAddress.Parse("192.168.1.42"),
            IPAddress.Parse("192.168.1.0"),
            IPAddress.Parse("255.255.255.0"));

        Assert.True(inside);
    }

    [Fact]
    public void IsInSubnet_IpOutside24_ReturnsFalse() {
        var outside = ArpScanProbe.IsInSubnet(
            IPAddress.Parse("10.0.0.5"),
            IPAddress.Parse("192.168.1.0"),
            IPAddress.Parse("255.255.255.0"));

        Assert.False(outside);
    }

    [Fact]
    public void IsInSubnet_IpExactlyAtNetworkBoundary_ReturnsTrue() {
        Assert.True(ArpScanProbe.IsInSubnet(
            IPAddress.Parse("192.168.1.0"),
            IPAddress.Parse("192.168.1.0"),
            IPAddress.Parse("255.255.255.0")));
        Assert.True(ArpScanProbe.IsInSubnet(
            IPAddress.Parse("192.168.1.255"),
            IPAddress.Parse("192.168.1.0"),
            IPAddress.Parse("255.255.255.0")));
    }

    [Fact]
    public void IsInSubnet_22BitMask_AcceptsLargerRange() {
        // 192.168.4.0/22 spans 192.168.4.0 — 192.168.7.255.
        var mask = IPAddress.Parse("255.255.252.0");
        Assert.True(ArpScanProbe.IsInSubnet(
            IPAddress.Parse("192.168.5.10"), IPAddress.Parse("192.168.4.0"), mask));
        Assert.True(ArpScanProbe.IsInSubnet(
            IPAddress.Parse("192.168.7.255"), IPAddress.Parse("192.168.4.0"), mask));
        Assert.False(ArpScanProbe.IsInSubnet(
            IPAddress.Parse("192.168.8.0"), IPAddress.Parse("192.168.4.0"), mask));
    }

    [Fact]
    public void IsInSubnet_Ipv6Input_ReturnsFalse() {
        var ipv6 = IPAddress.Parse("fe80::1");

        Assert.False(ArpScanProbe.IsInSubnet(
            ipv6, IPAddress.Parse("192.168.1.0"), IPAddress.Parse("255.255.255.0")));
    }

    // --- EnumerateHostAddresses (unchanged behavior; guards against accidental regression) ---

    [Fact]
    public void EnumerateHostAddresses_Slash24_ReturnsAllUsableHosts() {
        var hosts = ArpScanProbe.EnumerateHostAddresses(
            IPAddress.Parse("192.168.1.0"),
            IPAddress.Parse("255.255.255.0")).ToList();

        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), hosts[^1]);
    }

    [Fact]
    public void EnumerateHostAddresses_LargerThanCeiling_CapsAt4094Hosts() {
        // A /16 has 65534 host addresses; the defensive ceiling clamps at 4094.
        var hosts = ArpScanProbe.EnumerateHostAddresses(
            IPAddress.Parse("10.0.0.0"),
            IPAddress.Parse("255.255.0.0")).Count();

        Assert.Equal(4094, hosts);
    }

    [Fact]
    public void EnumerateHostAddresses_Ipv6Input_ReturnsEmpty() {
        var hosts = ArpScanProbe.EnumerateHostAddresses(
            IPAddress.Parse("fe80::"),
            IPAddress.Parse("255.255.255.0")).ToList();

        Assert.Empty(hosts);
    }

    // --- ProbeIpsAsync ---

    [Fact]
    public async Task ProbeIpsAsync_EmptyInput_ReturnsEmpty() {
        var probe = new ArpScanProbe((_, _) => null, TimeSpan.FromSeconds(60));

        var results = await probe.ProbeIpsAsync([], CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ProbeIpsAsync_AllResponders_ReturnsAllResults() {
        var probe = new ArpScanProbe(
            (ip, _) => $"00:00:00:00:00:{ip.GetAddressBytes()[3]:x2}",
            TimeSpan.FromSeconds(60));
        var targets = ArpScanProbe.EnumerateHostAddresses(
            IPAddress.Parse("192.168.1.0"), IPAddress.Parse("255.255.255.252")).ToList();  // /30 → 2 hosts

        var results = await probe.ProbeIpsAsync(targets, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Ip.Equals(IPAddress.Parse("192.168.1.1")));
        Assert.Contains(results, r => r.Ip.Equals(IPAddress.Parse("192.168.1.2")));
    }

    [Fact]
    public async Task ProbeIpsAsync_NoResponders_ReturnsEmpty() {
        var probe = new ArpScanProbe((_, _) => null, TimeSpan.FromSeconds(60));
        var targets = ArpScanProbe.EnumerateHostAddresses(
            IPAddress.Parse("192.168.1.0"), IPAddress.Parse("255.255.255.0")).ToList();

        var results = await probe.ProbeIpsAsync(targets, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ProbeIpsAsync_MixedResponses_ReturnsOnlyResponders() {
        // Respond only for IPs ending in even numbers.
        var probe = new ArpScanProbe(
            (ip, _) => ip.GetAddressBytes()[3] % 2 == 0 ? "aa:bb:cc:dd:ee:ff" : null,
            TimeSpan.FromSeconds(60));
        var targets = ArpScanProbe.EnumerateHostAddresses(
            IPAddress.Parse("192.168.1.0"), IPAddress.Parse("255.255.255.248")).ToList();  // /29 → 6 hosts

        var results = await probe.ProbeIpsAsync(targets, CancellationToken.None);

        // 1, 3, 5 (odd) → no response; 2, 4, 6 (even) → respond.
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(0, r.Ip.GetAddressBytes()[3] % 2));
    }

    [Fact]
    public async Task ProbeIpsAsync_ParallelDispatch_RunsConcurrently() {
        // Assert the probes actually OVERLAP in time — not that the batch
        // finishes by some wall-clock deadline. A deadline measures the thread
        // pool's thread-injection rate, a shared global resource this code does
        // not control, so it passes in isolation but flakes under full-suite
        // contention. Instead each fake probe records the peak number of probes
        // running simultaneously; a serial dispatch can never exceed 1.
        var gate = new object();
        var current = 0;
        var peak = 0;
        var probe = new ArpScanProbe(
            (_, _) => {
                lock (gate) { current++; if (current > peak) peak = current; }
                Thread.Sleep(50);   // linger briefly so concurrent probes overlap
                lock (gate) { current--; }
                return null;
            },
            TimeSpan.FromSeconds(60));
        var targets = Enumerable.Range(1, 64)
            .Select(i => new IPAddress([192, 168, 1, (byte)i]))
            .ToList();

        await probe.ProbeIpsAsync(targets, CancellationToken.None);

        // >= 2 is the provable, flake-proof floor: any overlap at all proves the
        // dispatch was not serial. Deliberately not a higher number — asserting,
        // say, "64-way" would re-couple the test to how many threads the pool
        // grants under load, reintroducing the exact fragility this removes.
        Assert.True(peak >= 2,
            $"Expected probes to run concurrently; peak simultaneous probes was {peak}");
    }

    [Fact]
    public async Task ProbeIpsAsync_DeadlineExpires_ReturnsPartialResults() {
        // Fake probe sleeps 500 ms per call. With a 200 ms deadline + 64-way
        // parallelism, the deadline fires before the first batch completes,
        // and ProbeIpsAsync returns whatever (in this case, zero or a few)
        // responders had accumulated rather than throwing.
        var probe = new ArpScanProbe(
            (_, _) => { Thread.Sleep(500); return "aa:bb:cc:dd:ee:ff"; },
            TimeSpan.FromMilliseconds(200));
        var targets = Enumerable.Range(1, 254)
            .Select(i => new IPAddress([192, 168, 1, (byte)i]))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await probe.ProbeIpsAsync(targets, CancellationToken.None);
        sw.Stop();

        // Deadline should fire well before 254 × 500ms = 127 s. Allow 2 s
        // for cleanup + the in-flight first batch to drain.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected deadline expiry <2s; took {sw.Elapsed.TotalSeconds:F2}s");
        // Partial results may or may not be empty depending on scheduling;
        // the key contract is "didn't throw, did bail early."
        Assert.NotNull(results);
    }

    [Fact]
    public async Task ProbeIpsAsync_OuterCancellation_Throws() {
        var probe = new ArpScanProbe(
            (_, _) => { Thread.Sleep(50); return null; },
            TimeSpan.FromSeconds(60));
        var targets = Enumerable.Range(1, 254)
            .Select(i => new IPAddress([192, 168, 1, (byte)i]))
            .ToList();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ProbeIpsAsync(targets, cts.Token));
    }

    [Fact]
    public void Constructor_NullProbeFunc_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => new ArpScanProbe(null!, TimeSpan.FromSeconds(60)));
    }
}
