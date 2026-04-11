using System.Net;
using Beholder.Core;
using Beholder.Daemon.GeoIp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public sealed class DbIpProviderTests : IDisposable {
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "TestData", "beholder-test.mmdb");

    private readonly DbIpProvider _provider;

    public DbIpProviderTests() {
        _provider = new DbIpProvider(FixturePath, NullLogger<DbIpProvider>.Instance);
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void Constructor_NonExistentFile_ThrowsFileNotFoundException() {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mmdb");

        Assert.Throws<FileNotFoundException>(
            () => new DbIpProvider(missing, NullLogger<DbIpProvider>.Instance));
    }

    [Fact]
    public void Constructor_NullPath_ThrowsArgumentException() {
        Assert.Throws<ArgumentNullException>(
            () => new DbIpProvider(null!, NullLogger<DbIpProvider>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => new DbIpProvider(FixturePath, null!));
    }

    [Theory]
    [InlineData("8.8.8.8", "US")]
    [InlineData("1.1.1.1", "AU")]
    [InlineData("78.46.99.1", "DE")]
    public void Resolve_KnownPublicIp_ReturnsCorrectCountry(string address, string expected) {
        var result = _provider.Resolve(IPAddress.Parse(address));

        Assert.Equal(CountryCode.FromAlpha2(expected), result);
    }

    [Fact]
    public void Resolve_UnknownIp_ReturnsUnknown() {
        var result = _provider.Resolve(IPAddress.Parse("203.0.113.1"));

        Assert.Equal(CountryCode.Unknown, result);
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    public void Resolve_PrivateIpRFC1918_ReturnsLocal(string address) {
        var result = _provider.Resolve(IPAddress.Parse(address));

        Assert.Equal(CountryCode.Local, result);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Resolve_LoopbackIp_ReturnsLocal(string address) {
        var result = _provider.Resolve(IPAddress.Parse(address));

        Assert.Equal(CountryCode.Local, result);
    }

    [Theory]
    [InlineData("169.254.1.1")]
    [InlineData("fe80::1")]
    public void Resolve_LinkLocalIp_ReturnsLocal(string address) {
        var result = _provider.Resolve(IPAddress.Parse(address));

        Assert.Equal(CountryCode.Local, result);
    }

    [Fact]
    public void Resolve_CGNATRange_ReturnsLocal() {
        var result = _provider.Resolve(IPAddress.Parse("100.64.0.1"));

        Assert.Equal(CountryCode.Local, result);
    }

    [Fact]
    public void Resolve_NullAddress_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => _provider.Resolve(null!));
    }

    [Fact]
    public void Resolve_CacheHit_ReturnsSameResultWithoutMmdbAccess() {
        var address = IPAddress.Parse("8.8.8.8");

        var first = _provider.Resolve(address);
        var second = _provider.Resolve(address);

        Assert.Equal(CountryCode.FromAlpha2("US"), first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Dispose_ClosesMmdbReader() {
        var provider = new DbIpProvider(FixturePath, NullLogger<DbIpProvider>.Instance);

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => provider.Resolve(IPAddress.Parse("8.8.8.8")));
    }
}
