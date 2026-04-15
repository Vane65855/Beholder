using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public class SqliteTrafficStoreTests : IDisposable {
    private readonly string _tempDir;
    private readonly SqliteTrafficStore _store;
    private readonly FakeTimeProvider _timeProvider;

    // BaseTime is the default "now" for tests. Queries in tests use time
    // windows that position the target tier under Balanced retention:
    // - BaseTime ± a few minutes → hits `traffic_raw` for fine resolutions,
    //   `traffic_buckets_10s` for 10s-or-coarser resolutions.
    private static readonly DateTimeOffset BaseTime =
        new(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);

    public SqliteTrafficStoreTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();
        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        _timeProvider = new FakeTimeProvider(BaseTime);
        _store = new SqliteTrafficStore(
            connectionFactory,
            new FakeOptionsMonitor<RollupOptions>(new RollupOptions()),
            _timeProvider);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static TrafficBucket CreateBucket(
        string processPath = "C:/app/firefox.exe",
        string processName = "firefox.exe",
        string remoteAddress = "93.184.216.34",
        int remotePort = 443,
        string? hostname = "example.com",
        string countryAlpha2 = "US",
        long bytesIn = 1000,
        long bytesOut = 500,
        DateTimeOffset? bucketStart = null,
        int bucketSeconds = 1
    ) {
        var country = countryAlpha2 switch {
            "--" => CountryCode.Local,
            "??" => CountryCode.Unknown,
            _ => CountryCode.FromAlpha2(countryAlpha2)
        };
        return new TrafficBucket(
            0, processPath, processName, remoteAddress, remotePort,
            hostname, country, bytesIn, bytesOut,
            bucketStart ?? BaseTime, bucketSeconds);
    }

    [Fact]
    public async Task WriteRawBucketsAsync_EmptyList_DoesNotThrow() {
        await _store.WriteRawBucketsAsync([], CancellationToken.None);
    }

    [Fact]
    public async Task WriteRawBucketsAsync_SingleBucket_RoundTrips() {
        var bucket = CreateBucket();
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        // Query a window centered on BaseTime with fine resolution → tier
        // selector picks traffic_raw (via fallback, since no tier retains
        // BaseTime — but _timeProvider.Now == BaseTime so the range is tiny).
        var timeline = await _store.GetProcessTimelineAsync(
            "C:/app/firefox.exe",
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Single(timeline);
        Assert.Equal(1000, timeline[0].BytesIn);
        Assert.Equal(500, timeline[0].BytesOut);
    }

    [Fact]
    public async Task WriteRawBucketsAsync_MultipleBuckets_AllPersisted() {
        var buckets = new[] {
            CreateBucket(remoteAddress: "1.1.1.1", bytesIn: 100, bytesOut: 50),
            CreateBucket(remoteAddress: "2.2.2.2", bytesIn: 200, bytesOut: 100),
            CreateBucket(remoteAddress: "3.3.3.3", bytesIn: 300, bytesOut: 150)
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var destinations = await _store.GetProcessDestinationsAsync(
            "C:/app/firefox.exe",
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11),
            CancellationToken.None);

        Assert.Equal(3, destinations.Count);
        Assert.Equal(600, destinations.Sum(d => d.TotalBytesIn));
        Assert.Equal(300, destinations.Sum(d => d.TotalBytesOut));
    }

    [Fact]
    public async Task WriteRawBucketsAsync_NullHostname_StoredAsNull() {
        var bucket = CreateBucket(hostname: null);
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        var destinations = await _store.GetProcessDestinationsAsync(
            "C:/app/firefox.exe",
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11),
            CancellationToken.None);

        Assert.Single(destinations);
        Assert.Null(destinations[0].Hostname);
    }

    [Fact]
    public async Task GetProcessTimelineAsync_GroupsByResolution() {
        // Write four raw buckets at 0s/2s/5s/12s. Query at 9-second resolution
        // so the tier selector picks traffic_raw (raw passes the bucket rule at
        // 1 ≤ 9; _10s fails at 10 > 9). SQL GROUP BY re-bucketizes into two
        // 9-second output windows: [0-9s) covers the first three source rows,
        // [9-18s) covers the fourth.
        var buckets = new[] {
            CreateBucket(bytesIn: 100, bytesOut: 50, bucketStart: BaseTime),
            CreateBucket(bytesIn: 200, bytesOut: 100, bucketStart: BaseTime.AddSeconds(2)),
            CreateBucket(bytesIn: 300, bytesOut: 150, bucketStart: BaseTime.AddSeconds(5)),
            CreateBucket(bytesIn: 400, bytesOut: 200, bucketStart: BaseTime.AddSeconds(12))
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var timeline = await _store.GetProcessTimelineAsync(
            "C:/app/firefox.exe",
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(20),
            TimeSpan.FromSeconds(9),
            CancellationToken.None);

        Assert.Equal(2, timeline.Count);
        Assert.Equal(600, timeline[0].BytesIn);
        Assert.Equal(300, timeline[0].BytesOut);
        Assert.Equal(400, timeline[1].BytesIn);
        Assert.Equal(200, timeline[1].BytesOut);
    }

    [Fact]
    public async Task GetProcessTimelineAsync_FiltersOnProcessPath() {
        var buckets = new[] {
            CreateBucket(processPath: "C:/app/firefox.exe", bytesIn: 100, bytesOut: 50),
            CreateBucket(processPath: "C:/app/chrome.exe", processName: "chrome.exe", bytesIn: 999, bytesOut: 888)
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var timeline = await _store.GetProcessTimelineAsync(
            "C:/app/firefox.exe",
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Single(timeline);
        Assert.Equal(100, timeline[0].BytesIn);
    }

    [Fact]
    public async Task GetProcessTimelineAsync_EmptyRange_ReturnsEmpty() {
        var bucket = CreateBucket();
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        // Query a range that excludes the written bucket. Tier selector
        // picks traffic_raw (fine resolution) and finds no matching rows.
        var timeline = await _store.GetProcessTimelineAsync(
            "C:/app/firefox.exe",
            BaseTime.AddHours(1), BaseTime.AddHours(2),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Empty(timeline);
    }

    [Fact]
    public async Task GetProcessDestinationsAsync_AggregatesPerAddress() {
        var buckets = new[] {
            CreateBucket(remoteAddress: "1.1.1.1", remotePort: 443, bytesIn: 100, bytesOut: 50),
            CreateBucket(remoteAddress: "1.1.1.1", remotePort: 80, bytesIn: 200, bytesOut: 100),
            CreateBucket(remoteAddress: "2.2.2.2", remotePort: 443, bytesIn: 300, bytesOut: 150)
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var destinations = await _store.GetProcessDestinationsAsync(
            "C:/app/firefox.exe",
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11),
            CancellationToken.None);

        Assert.Equal(2, destinations.Count);

        var addr1 = destinations.First(d => d.RemoteAddress == "1.1.1.1");
        Assert.Equal(300, addr1.TotalBytesIn);
        Assert.Equal(150, addr1.TotalBytesOut);
        Assert.Equal(2, addr1.ConnectionCount);

        var addr2 = destinations.First(d => d.RemoteAddress == "2.2.2.2");
        Assert.Equal(300, addr2.TotalBytesIn);
        Assert.Equal(150, addr2.TotalBytesOut);
        Assert.Equal(1, addr2.ConnectionCount);
    }

    [Fact]
    public async Task GetProcessDestinationsAsync_PreservesCountryAndHostname() {
        var bucket = CreateBucket(
            hostname: "cdn.example.com",
            countryAlpha2: "DE");
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        var destinations = await _store.GetProcessDestinationsAsync(
            "C:/app/firefox.exe",
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11),
            CancellationToken.None);

        Assert.Single(destinations);
        Assert.Equal("cdn.example.com", destinations[0].Hostname);
        Assert.Equal(CountryCode.FromAlpha2("DE"), destinations[0].Country);
    }

    [Fact]
    public async Task GetAggregateTimelineAsync_SumsAcrossProcesses() {
        var buckets = new[] {
            CreateBucket(processPath: "C:/app/firefox.exe", bytesIn: 100, bytesOut: 50),
            CreateBucket(processPath: "C:/app/chrome.exe", processName: "chrome.exe", bytesIn: 200, bytesOut: 100)
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var timeline = await _store.GetAggregateTimelineAsync(
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Single(timeline);
        Assert.Equal(300, timeline[0].BytesIn);
        Assert.Equal(150, timeline[0].BytesOut);
    }

    [Fact]
    public async Task GetCountryBreakdownAsync_GroupsByCountry() {
        var buckets = new[] {
            CreateBucket(remoteAddress: "1.1.1.1", countryAlpha2: "US", bytesIn: 100, bytesOut: 50),
            CreateBucket(remoteAddress: "2.2.2.2", countryAlpha2: "US", bytesIn: 200, bytesOut: 100),
            CreateBucket(remoteAddress: "3.3.3.3", countryAlpha2: "DE", bytesIn: 300, bytesOut: 150)
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var breakdown = await _store.GetCountryBreakdownAsync(
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11),
            CancellationToken.None);

        Assert.Equal(2, breakdown.Count);

        var us = breakdown.First(c => c.Country == CountryCode.FromAlpha2("US"));
        Assert.Equal(300, us.TotalBytesIn);
        Assert.Equal(150, us.TotalBytesOut);

        var de = breakdown.First(c => c.Country == CountryCode.FromAlpha2("DE"));
        Assert.Equal(300, de.TotalBytesIn);
        Assert.Equal(150, de.TotalBytesOut);
    }

    [Fact]
    public async Task GetCountryBreakdownAsync_HandlesLocalAndUnknown() {
        var buckets = new[] {
            CreateBucket(remoteAddress: "192.168.1.1", countryAlpha2: "--", bytesIn: 100, bytesOut: 50),
            CreateBucket(remoteAddress: "10.0.0.1", countryAlpha2: "??", bytesIn: 200, bytesOut: 100)
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var breakdown = await _store.GetCountryBreakdownAsync(
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11),
            CancellationToken.None);

        Assert.Equal(2, breakdown.Count);
        Assert.Contains(breakdown, c => c.Country == CountryCode.Local);
        Assert.Contains(breakdown, c => c.Country == CountryCode.Unknown);
    }

    [Fact]
    public async Task GetProcessTimelineAsync_InvalidResolution_Throws() {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.GetProcessTimelineAsync(
                "C:/app/firefox.exe", BaseTime, BaseTime.AddHours(1),
                TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task GetAggregateTimelineAsync_InvalidResolution_Throws() {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.GetAggregateTimelineAsync(
                BaseTime, BaseTime.AddHours(1),
                TimeSpan.FromSeconds(-1), CancellationToken.None));
    }
}
