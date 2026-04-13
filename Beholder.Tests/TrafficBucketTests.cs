using Beholder.Core;

namespace Beholder.Tests;

public class TrafficBucketTests {
    private static TrafficBucket CreateValid(
        long id = 1,
        string processPath = "fake/firefox.exe",
        string processName = "firefox.exe",
        string remoteAddress = "1.2.3.4",
        int remotePort = 443,
        string? hostname = "example.com",
        long bytesIn = 1000,
        long bytesOut = 500,
        int bucketSeconds = 10
    ) {
        return new TrafficBucket(
            id, processPath, processName, remoteAddress, remotePort,
            hostname, CountryCode.FromAlpha2("US"), bytesIn, bytesOut,
            DateTimeOffset.UnixEpoch, bucketSeconds);
    }

    [Fact]
    public void Constructor_ValidArguments_PopulatesAllProperties() {
        var bucket = CreateValid();

        Assert.Equal(1, bucket.Id);
        Assert.Equal("fake/firefox.exe", bucket.ProcessPath);
        Assert.Equal("firefox.exe", bucket.ProcessName);
        Assert.Equal("1.2.3.4", bucket.RemoteAddress);
        Assert.Equal(443, bucket.RemotePort);
        Assert.Equal("example.com", bucket.Hostname);
        Assert.Equal(CountryCode.FromAlpha2("US"), bucket.Country);
        Assert.Equal(1000, bucket.BytesIn);
        Assert.Equal(500, bucket.BytesOut);
        Assert.Equal(DateTimeOffset.UnixEpoch, bucket.BucketStart);
        Assert.Equal(10, bucket.BucketSeconds);
    }

    [Fact]
    public void Constructor_NullHostname_Allowed() {
        var bucket = CreateValid(hostname: null);
        Assert.Null(bucket.Hostname);
    }

    [Fact]
    public void Constructor_ZeroId_Allowed() {
        var bucket = CreateValid(id: 0);
        Assert.Equal(0, bucket.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_EmptyProcessPath_ThrowsArgumentException(string path) {
        Assert.Throws<ArgumentException>(() => CreateValid(processPath: path));
    }

    [Fact]
    public void Constructor_NullProcessPath_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => CreateValid(processPath: null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_EmptyProcessName_ThrowsArgumentException(string name) {
        Assert.Throws<ArgumentException>(() => CreateValid(processName: name));
    }

    [Fact]
    public void Constructor_NullProcessName_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => CreateValid(processName: null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_EmptyRemoteAddress_ThrowsArgumentException(string addr) {
        Assert.Throws<ArgumentException>(() => CreateValid(remoteAddress: addr));
    }

    [Fact]
    public void Constructor_NullRemoteAddress_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => CreateValid(remoteAddress: null!));
    }

    [Fact]
    public void Constructor_NegativeBytesIn_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateValid(bytesIn: -1));
    }

    [Fact]
    public void Constructor_NegativeBytesOut_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateValid(bytesOut: -1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidBucketSeconds_Throws(int seconds) {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateValid(bucketSeconds: seconds));
    }

    [Fact]
    public void Constructor_ZeroBytesIn_Allowed() {
        var bucket = CreateValid(bytesIn: 0);
        Assert.Equal(0, bucket.BytesIn);
    }

    [Fact]
    public void Constructor_ZeroBytesOut_Allowed() {
        var bucket = CreateValid(bytesOut: 0);
        Assert.Equal(0, bucket.BytesOut);
    }
}
