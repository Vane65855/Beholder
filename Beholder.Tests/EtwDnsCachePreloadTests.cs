#if PLATFORM_WINDOWS
using System.Net;
using Beholder.Daemon.Windows;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

/// <summary>
/// Covers the preload-side cache-population seam on <see cref="EtwDnsCache"/>:
/// <c>IngestResolved</c> directly writes an (IP, hostname) pair without going
/// through the Windows-formatted-string parser used by the live ETW path. The
/// actual <see cref="DnsApiInterop.TryEnumerateResolverCache"/> call is not
/// unit-tested — it would require a real Windows DNS resolver cache state
/// that CI boxes don't have. Production validation of that path is the
/// smoke-test step described in <c>docs/decisions/004-dns-cache-preload-undocumented-api.md</c>.
/// </summary>
public class EtwDnsCachePreloadTests {
    private static EtwDnsCache CreateCache() =>
        new(NullLogger<EtwDnsCache>.Instance,
            new FakeOptionsMonitor<DnsOptions>(new DnsOptions()),
            new FakeHostnameResolutionSettingsState());

    [Fact]
    public void IngestResolved_PopulatesCacheForIpv4() {
        var cache = CreateCache();
        var address = IPAddress.Parse("93.184.216.34");

        cache.IngestResolved("example.com", address);

        Assert.Equal("example.com", cache.Resolve(address));
    }

    [Fact]
    public void IngestResolved_PopulatesCacheForIpv6() {
        var cache = CreateCache();
        var address = IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946");

        cache.IngestResolved("example.com", address);

        Assert.Equal("example.com", cache.Resolve(address));
    }

    [Fact]
    public void IngestResolved_WhitespaceQueryName_Throws() {
        var cache = CreateCache();

        Assert.Throws<ArgumentException>(
            () => cache.IngestResolved("   ", IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void IngestResolved_NullAddress_Throws() {
        var cache = CreateCache();

        Assert.Throws<ArgumentNullException>(
            () => cache.IngestResolved("example.com", null!));
    }

    [Fact]
    public void IngestResolved_LaterCallOverwritesPreviousName() {
        // Matches the last-writer-wins semantics of the live-ETW Ingest path.
        // CDN IPs routinely host many hostnames and the cache holds whichever
        // mapping was learned most recently.
        var cache = CreateCache();
        var address = IPAddress.Parse("93.184.216.34");

        cache.IngestResolved("first.example.com", address);
        cache.IngestResolved("second.example.com", address);

        Assert.Equal("second.example.com", cache.Resolve(address));
    }

    [Fact]
    public void PreloadLoopPopulatesAllEntries() {
        // Simulates what PreloadFromWindowsDnsCache does once
        // TryEnumerateResolverCache yields its tuples. Proves the loop-body
        // semantics (name + address per entry, delegating to IngestResolved)
        // without reaching into the P/Invoke layer.
        var cache = CreateCache();
        var entries = new[] {
            ("one.example.com", IPAddress.Parse("10.0.0.1")),
            ("two.example.com", IPAddress.Parse("10.0.0.2")),
            ("three.example.com", IPAddress.Parse("10.0.0.3")),
            ("four.example.com", IPAddress.Parse("10.0.0.4")),
            ("five.example.com", IPAddress.Parse("10.0.0.5")),
        };

        foreach (var (name, address) in entries) {
            cache.IngestResolved(name, address);
        }

        foreach (var (name, address) in entries) {
            Assert.Equal(name, cache.Resolve(address));
        }
    }

    [Fact]
    public void IngestResolved_CoexistsWithLiveIngest() {
        // The live ETW path (Ingest) and the preload path (IngestResolved)
        // write to the same _cache. Verify both paths interoperate cleanly
        // without stomping one another's entries when they cover disjoint IPs.
        var cache = CreateCache();

        cache.IngestResolved("preload.example.com", IPAddress.Parse("1.1.1.1"));
        cache.Ingest("live.example.com", "2.2.2.2");

        Assert.Equal("preload.example.com", cache.Resolve(IPAddress.Parse("1.1.1.1")));
        Assert.Equal("live.example.com", cache.Resolve(IPAddress.Parse("2.2.2.2")));
    }
}
#endif
