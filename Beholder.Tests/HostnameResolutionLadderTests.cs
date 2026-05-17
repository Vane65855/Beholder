using System.Net;
using Beholder.Core;
using Beholder.Daemon.Windows.Scanner;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public sealed class HostnameResolutionLadderTests {
    [Fact]
    public void Constructor_NullProbes_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => new HostnameResolutionLadder(null!, NullLogger<HostnameResolutionLadder>.Instance));
    }

    [Fact]
    public async Task ResolveAllAsync_EmptyInput_ReturnsEmptyDictionary() {
        var ladder = CreateLadder(new FakeHostnameProbe("mDNS"), new FakeHostnameProbe("NetBIOS"));

        var result = await ladder.ResolveAllAsync([], CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveAllAsync_NullInput_ThrowsArgumentNullException() {
        var ladder = CreateLadder(new FakeHostnameProbe("mDNS"));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ladder.ResolveAllAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAllAsync_FirstProbeReturnsHostname_SecondNotCalled() {
        var first = new FakeHostnameProbe("mDNS") {
            Responder = (_, _) => Task.FromResult<string?>("iphone.local"),
        };
        var second = new FakeHostnameProbe("NetBIOS");
        var ladder = CreateLadder(first, second);

        var result = await ladder.ResolveAllAsync(
            [IPAddress.Parse("192.168.1.42")], CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("iphone.local", result["192.168.1.42"]);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(0, second.CallCount);  // never called because first succeeded
    }

    [Fact]
    public async Task ResolveAllAsync_FirstProbeReturnsNull_FallsThroughToSecond() {
        var first = new FakeHostnameProbe("mDNS");  // default null
        var second = new FakeHostnameProbe("NetBIOS") {
            Responder = (_, _) => Task.FromResult<string?>("VANE-PC"),
        };
        var ladder = CreateLadder(first, second);

        var result = await ladder.ResolveAllAsync(
            [IPAddress.Parse("192.168.1.42")], CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("VANE-PC", result["192.168.1.42"]);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public async Task ResolveAllAsync_AllProbesNull_NoEntryInResult() {
        var first = new FakeHostnameProbe("mDNS");   // default null
        var second = new FakeHostnameProbe("NetBIOS"); // default null
        var ladder = CreateLadder(first, second);

        var result = await ladder.ResolveAllAsync(
            [IPAddress.Parse("192.168.1.42")], CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public async Task ResolveAllAsync_ParallelDispatch_RunsConcurrently() {
        // 32 IPs × 100 ms fake probe sleep. Sequential would be 3.2 s.
        // With MaxParallelHostnameResolves=32 it should complete in ~100-300 ms.
        var probe = new FakeHostnameProbe("mDNS") {
            Responder = async (_, ct) => {
                await Task.Delay(100, ct);
                return "hostname";
            },
        };
        var ladder = CreateLadder(probe);
        var targets = Enumerable.Range(1, 32)
            .Select(i => new IPAddress([192, 168, 1, (byte)i]))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await ladder.ResolveAllAsync(targets, CancellationToken.None);
        sw.Stop();

        Assert.Equal(32, result.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5),
            $"Expected parallel dispatch <1.5s; took {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task ResolveAllAsync_DeadlineExpires_ReturnsPartial() {
        // 200ms internal deadline, 500ms-sleep probes. Deadline fires before
        // first batch completes; ladder returns whatever (probably empty)
        // partial results without throwing.
        var probe = new FakeHostnameProbe("mDNS") {
            Responder = async (_, ct) => {
                await Task.Delay(500, ct);
                return "should-not-arrive";
            },
        };
        var ladder = new HostnameResolutionLadder(
            new IHostnameProbe[] { probe },
            TimeSpan.FromMilliseconds(200),
            NullLogger<HostnameResolutionLadder>.Instance);
        var targets = Enumerable.Range(1, 64)
            .Select(i => new IPAddress([192, 168, 1, (byte)i]))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await ladder.ResolveAllAsync(targets, CancellationToken.None);
        sw.Stop();

        // Deadline should fire well before 64 × 500ms = 32 s. Allow ample
        // cleanup time but still strictly less than the no-deadline case.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"Expected deadline expiry < 3s; took {sw.Elapsed.TotalSeconds:F2}s");
        Assert.NotNull(result);  // contract: doesn't throw, returns whatever it has
    }

    [Fact]
    public async Task ResolveAllAsync_OuterCancellation_Throws() {
        var probe = new FakeHostnameProbe("mDNS") {
            Responder = async (_, ct) => {
                await Task.Delay(50, ct);
                return "hostname";
            },
        };
        var ladder = CreateLadder(probe);
        var targets = Enumerable.Range(1, 254)
            .Select(i => new IPAddress([192, 168, 1, (byte)i]))
            .ToList();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ladder.ResolveAllAsync(targets, cts.Token));
    }

    [Fact]
    public async Task ResolveAllAsync_ProbeThrows_LogsAndContinuesToNextProbe() {
        var first = new FakeHostnameProbe("mDNS") {
            Responder = (_, _) => throw new InvalidOperationException("first probe boom"),
        };
        var second = new FakeHostnameProbe("NetBIOS") {
            Responder = (_, _) => Task.FromResult<string?>("fallback-host"),
        };
        var ladder = CreateLadder(first, second);

        var result = await ladder.ResolveAllAsync(
            [IPAddress.Parse("192.168.1.42")], CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("fallback-host", result["192.168.1.42"]);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public async Task ResolveAllAsync_PartialBatch_OnlyResponsiveIpsInResult() {
        // Half the IPs (even last octet) respond; half don't (odd).
        var probe = new FakeHostnameProbe("mDNS") {
            Responder = (ip, _) => Task.FromResult<string?>(
                ip.GetAddressBytes()[3] % 2 == 0 ? $"host-{ip.GetAddressBytes()[3]}" : null),
        };
        var ladder = CreateLadder(probe);
        var targets = Enumerable.Range(1, 10)
            .Select(i => new IPAddress([192, 168, 1, (byte)i]))
            .ToList();

        var result = await ladder.ResolveAllAsync(targets, CancellationToken.None);

        // 2, 4, 6, 8, 10 respond. Odd-last-octet IPs absent (not present-with-null).
        Assert.Equal(5, result.Count);
        Assert.All(result.Keys, ip => {
            var lastOctet = byte.Parse(ip.Split('.').Last());
            Assert.Equal(0, lastOctet % 2);
        });
    }

    private static HostnameResolutionLadder CreateLadder(params IHostnameProbe[] probes) =>
        new(probes, NullLogger<HostnameResolutionLadder>.Instance);
}
