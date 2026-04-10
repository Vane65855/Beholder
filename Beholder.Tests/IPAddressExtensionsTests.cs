using System.Net;
using Beholder.Core;

namespace Beholder.Tests;

public class IPAddressExtensionsTests {
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.0", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.167.1.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.0", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    public void IsPrivateOrReserved_IPv4_ReturnsExpected(string address, bool expected) {
        var parsed = IPAddress.Parse(address);

        Assert.Equal(expected, parsed.IsPrivateOrReserved());
    }

    [Theory]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("2607:f8b0:4004:800::200e", false)]
    public void IsPrivateOrReserved_IPv6_ReturnsExpected(string address, bool expected) {
        var parsed = IPAddress.Parse(address);

        Assert.Equal(expected, parsed.IsPrivateOrReserved());
    }

    [Fact]
    public void IsPrivateOrReserved_Null_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => ((IPAddress)null!).IsPrivateOrReserved());
    }
}
