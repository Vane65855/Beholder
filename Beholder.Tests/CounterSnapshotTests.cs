using Beholder.Core;

namespace Beholder.Tests;

public class CounterSnapshotTests {
    [Fact]
    public void Constructor_ValidArguments_PopulatesAllProperties() {
        var byCountry = new Dictionary<CountryCode, long> {
            [CountryCode.FromAlpha2("US")] = 5000,
            [CountryCode.FromAlpha2("DE")] = 1500,
        };

        var snapshot = new CounterSnapshot(
            processName: "firefox.exe",
            processPath: @"C:\Program Files\Mozilla Firefox\firefox.exe",
            totalBytesIn: 100_000,
            totalBytesOut: 50_000,
            deltaBytesIn: 1_000,
            deltaBytesOut: 500,
            activeConnectionCount: 4,
            bytesOutByCountry: byCountry,
            timestamp: DateTimeOffset.UnixEpoch
        );

        Assert.Equal("firefox.exe", snapshot.ProcessName);
        Assert.Equal(100_000, snapshot.TotalBytesIn);
        Assert.Equal(50_000, snapshot.TotalBytesOut);
        Assert.Equal(1_000, snapshot.DeltaBytesIn);
        Assert.Equal(500, snapshot.DeltaBytesOut);
        Assert.Equal(4, snapshot.ActiveConnectionCount);
        Assert.Equal(5000, snapshot.BytesOutByCountry[CountryCode.FromAlpha2("US")]);
        Assert.Equal(1500, snapshot.BytesOutByCountry[CountryCode.FromAlpha2("DE")]);
    }

    [Fact]
    public void Constructor_NullBytesOutByCountry_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new CounterSnapshot(
            processName: "firefox.exe",
            processPath: "/opt/firefox/firefox",
            totalBytesIn: 0,
            totalBytesOut: 0,
            deltaBytesIn: 0,
            deltaBytesOut: 0,
            activeConnectionCount: 0,
            bytesOutByCountry: null!,
            timestamp: DateTimeOffset.UnixEpoch
        ));
    }

    [Fact]
    public void Constructor_NegativeTotalBytesIn_ThrowsArgumentOutOfRangeException() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CounterSnapshot(
            processName: "firefox.exe",
            processPath: "/opt/firefox/firefox",
            totalBytesIn: -1,
            totalBytesOut: 0,
            deltaBytesIn: 0,
            deltaBytesOut: 0,
            activeConnectionCount: 0,
            bytesOutByCountry: new Dictionary<CountryCode, long>(),
            timestamp: DateTimeOffset.UnixEpoch
        ));
    }
}
