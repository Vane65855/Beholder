#if PLATFORM_WINDOWS
using System.Net;
using Beholder.Daemon.Windows;

namespace Beholder.Tests;

public class EtwDnsCacheExtractAddressesTests {
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
}
#endif
