#if PLATFORM_WINDOWS
using System.Net;
using Beholder.Daemon.Windows;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class EtwDnsCacheExtractAddressesTests {
    private static EtwDnsCache CreateCache(int queueCapacity = 1024) =>
        new(NullLogger<EtwDnsCache>.Instance,
            new FakeOptionsMonitor<DnsOptions>(new DnsOptions { QueueCapacity = queueCapacity }));

    [Fact]
    public void ExtractAddresses_EmptyInput_ReturnsEmpty() {
        var addresses = EtwDnsCache.ExtractAddresses(string.Empty).ToList();
        Assert.Empty(addresses);
    }

    [Fact]
    public void ExtractAddresses_SingleIpv4_ReturnsOneAddress() {
        var addresses = EtwDnsCache.ExtractAddresses("93.184.216.34").ToList();
        var only = Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), only);
    }

    [Fact]
    public void ExtractAddresses_SingleIpv6_ReturnsOneAddress() {
        var addresses = EtwDnsCache.ExtractAddresses("2606:2800:220:1:248:1893:25c8:1946").ToList();
        var only = Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), only);
    }

    [Fact]
    public void ExtractAddresses_MultipleAddresses_ReturnsAll() {
        var addresses = EtwDnsCache.ExtractAddresses("1.1.1.1;2.2.2.2;3.3.3.3").ToList();
        Assert.Equal(3, addresses.Count);
        Assert.Contains(IPAddress.Parse("1.1.1.1"), addresses);
        Assert.Contains(IPAddress.Parse("2.2.2.2"), addresses);
        Assert.Contains(IPAddress.Parse("3.3.3.3"), addresses);
    }

    [Fact]
    public void ExtractAddresses_PrefixedFormat_ParsesCorrectly() {
        var addresses = EtwDnsCache.ExtractAddresses("type:  1 93.184.216.34").ToList();
        var only = Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), only);
    }

    [Fact]
    public void ExtractAddresses_MixedPrefixedAndBare_ReturnsAll() {
        var addresses = EtwDnsCache.ExtractAddresses("type:  1 1.1.1.1;2.2.2.2").ToList();
        Assert.Equal(2, addresses.Count);
        Assert.Contains(IPAddress.Parse("1.1.1.1"), addresses);
        Assert.Contains(IPAddress.Parse("2.2.2.2"), addresses);
    }

    [Fact]
    public void ExtractAddresses_MalformedEntries_AreSkipped() {
        var addresses = EtwDnsCache.ExtractAddresses("garbage;1.1.1.1").ToList();
        var only = Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse("1.1.1.1"), only);
    }

    // ---- Ingest + Resolve round-trip ----
    //
    // Covers the cache-population path that was expanded in Phase 6.3 to
    // accept any DNS Client ETW event carrying a query name + answer, not
    // just event 3008. These tests drive Ingest (which OnEtwEvent calls
    // after it pulls QueryName/QueryResults out of the raw payload),
    // bypassing the ETW TraceEvent construction that isn't easily mockable
    // in unit tests. They verify the second half of the pipeline — parse
    // + map + cache update — produces the expected Resolve() results.

    [Fact]
    public void Ingest_PopulatesCacheForSingleIpv4() {
        var cache = CreateCache();

        cache.Ingest("example.com", "93.184.216.34");

        Assert.Equal("example.com", cache.Resolve(IPAddress.Parse("93.184.216.34")));
    }

    [Fact]
    public void Ingest_PopulatesCacheForMultiAnswerEvent() {
        // Mirrors what a cache-hit event (3010) typically contains — a record
        // with multiple addresses for the same query.
        var cache = CreateCache();

        cache.Ingest("cdn.example.com", "104.16.0.1;104.16.0.2;104.16.0.3");

        Assert.Equal("cdn.example.com", cache.Resolve(IPAddress.Parse("104.16.0.1")));
        Assert.Equal("cdn.example.com", cache.Resolve(IPAddress.Parse("104.16.0.2")));
        Assert.Equal("cdn.example.com", cache.Resolve(IPAddress.Parse("104.16.0.3")));
    }

    [Fact]
    public void Ingest_LaterQueryOverwritesPreviousHostnameForSameIp() {
        // IPs can be reused across domains (shared hosting, CDN edges). Most
        // recent wins — matches the existing docstring on IDnsCache.
        var cache = CreateCache();

        cache.Ingest("first.example.com", "93.184.216.34");
        cache.Ingest("second.example.com", "93.184.216.34");

        Assert.Equal("second.example.com", cache.Resolve(IPAddress.Parse("93.184.216.34")));
    }

    [Fact]
    public void Ingest_IgnoresCnameSegments() {
        // A cache-hit event's QueryResults can include type-5 CNAME rows
        // interleaved with type-1 A rows. ExtractAddresses skips CNAMEs
        // (their tail tokens don't parse as IPs), so only the actual A
        // records land in the cache.
        var cache = CreateCache();

        cache.Ingest(
            "www.example.com",
            "type:  5 edge.example.com;type:  1 93.184.216.34");

        Assert.Equal("www.example.com", cache.Resolve(IPAddress.Parse("93.184.216.34")));
        Assert.Null(cache.Resolve(IPAddress.Parse("0.0.0.1")));
    }

    [Fact]
    public void Resolve_Miss_ReturnsNull() {
        var cache = CreateCache();

        Assert.Null(cache.Resolve(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void Ingest_WhitespaceQueryName_Throws() {
        var cache = CreateCache();

        Assert.Throws<ArgumentException>(() => cache.Ingest("  ", "93.184.216.34"));
    }

    [Fact]
    public void Ingest_WhitespaceQueryResults_Throws() {
        var cache = CreateCache();

        Assert.Throws<ArgumentException>(() => cache.Ingest("example.com", "  "));
    }
}
#endif
