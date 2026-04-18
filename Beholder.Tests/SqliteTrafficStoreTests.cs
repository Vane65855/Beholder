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
        // Write four raw buckets at 0s/2s/5s/2700s. Query at 9-second resolution.
        // The store's adapter clamps effective resolution to min(requested,
        // extent/300). Extent = 2700s, extent/300 = 9s, so effective stays at
        // the requested 9s. SQL GROUP BY re-bucketizes into two 9-second output
        // windows: [0-9s) covers the first three source rows, [2700-2709s)
        // covers the fourth.
        //
        // BaseTime (2026-04-13 12:00 UTC) is exactly divisible by 9000 ms so
        // bucket boundaries align to the row timestamps — keeps the assertions
        // deterministic across runs.
        var buckets = new[] {
            CreateBucket(bytesIn: 100, bytesOut: 50, bucketStart: BaseTime),
            CreateBucket(bytesIn: 200, bytesOut: 100, bucketStart: BaseTime.AddSeconds(2)),
            CreateBucket(bytesIn: 300, bytesOut: 150, bucketStart: BaseTime.AddSeconds(5)),
            CreateBucket(bytesIn: 400, bytesOut: 200, bucketStart: BaseTime.AddSeconds(2700))
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var timeline = await _store.GetProcessTimelineAsync(
            "C:/app/firefox.exe",
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(2710),
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

    // ---- GetProcessSummariesAsync ----

    [Fact]
    public async Task GetProcessSummariesAsync_AggregatesPerProcess_ReturnsOneRowPerProcess() {
        var buckets = new[] {
            CreateBucket(processPath: "C:/app/firefox.exe", processName: "firefox.exe",
                         bytesIn: 100, bytesOut: 50, remoteAddress: "1.1.1.1"),
            CreateBucket(processPath: "C:/app/firefox.exe", processName: "firefox.exe",
                         bytesIn: 200, bytesOut: 100, remoteAddress: "2.2.2.2"),
            CreateBucket(processPath: "C:/app/chrome.exe", processName: "chrome.exe",
                         bytesIn: 400, bytesOut: 200, remoteAddress: "3.3.3.3"),
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var summaries = await _store.GetProcessSummariesAsync(
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11), CancellationToken.None);

        Assert.Equal(2, summaries.Count);

        var firefox = summaries.First(s => s.ProcessPath == "C:/app/firefox.exe");
        Assert.Equal("firefox.exe", firefox.ProcessName);
        Assert.Equal(300, firefox.TotalBytesIn);
        Assert.Equal(150, firefox.TotalBytesOut);

        var chrome = summaries.First(s => s.ProcessPath == "C:/app/chrome.exe");
        Assert.Equal("chrome.exe", chrome.ProcessName);
        Assert.Equal(400, chrome.TotalBytesIn);
        Assert.Equal(200, chrome.TotalBytesOut);
    }

    [Fact]
    public async Task GetProcessSummariesAsync_OrdersByTotalBytesDesc() {
        // The SQL ORDER BY is SUM(bytes_in) + SUM(bytes_out) DESC — highest total first.
        var buckets = new[] {
            CreateBucket(processPath: "C:/app/low.exe",  processName: "low.exe",
                         bytesIn: 10, bytesOut: 5),
            CreateBucket(processPath: "C:/app/high.exe", processName: "high.exe",
                         bytesIn: 1000, bytesOut: 500),
            CreateBucket(processPath: "C:/app/mid.exe",  processName: "mid.exe",
                         bytesIn: 100, bytesOut: 50),
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var summaries = await _store.GetProcessSummariesAsync(
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11), CancellationToken.None);

        Assert.Equal(3, summaries.Count);
        Assert.Equal("C:/app/high.exe", summaries[0].ProcessPath);
        Assert.Equal("C:/app/mid.exe",  summaries[1].ProcessPath);
        Assert.Equal("C:/app/low.exe",  summaries[2].ProcessPath);
    }

    [Fact]
    public async Task GetProcessSummariesAsync_FiltersByRange_ExcludesOutsideBuckets() {
        var buckets = new[] {
            CreateBucket(processPath: "C:/app/firefox.exe", processName: "firefox.exe",
                         bytesIn: 100, bytesOut: 50, bucketStart: BaseTime),
            CreateBucket(processPath: "C:/app/firefox.exe", processName: "firefox.exe",
                         bytesIn: 999, bytesOut: 999,
                         bucketStart: BaseTime.AddHours(2)),
        };
        await _store.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var summaries = await _store.GetProcessSummariesAsync(
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11), CancellationToken.None);

        Assert.Single(summaries);
        Assert.Equal(100, summaries[0].TotalBytesIn);
        Assert.Equal(50,  summaries[0].TotalBytesOut);
    }

    [Fact]
    public async Task GetProcessSummariesAsync_EmptyRange_ReturnsEmpty() {
        var bucket = CreateBucket();
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        var summaries = await _store.GetProcessSummariesAsync(
            BaseTime.AddHours(1), BaseTime.AddHours(2), CancellationToken.None);

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetProcessSummariesAsync_NoData_ReturnsEmpty() {
        var summaries = await _store.GetProcessSummariesAsync(
            BaseTime.AddSeconds(-1), BaseTime.AddSeconds(11), CancellationToken.None);

        Assert.Empty(summaries);
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

    [Fact]
    public async Task GetProcessTimelineAsync_FromAfterTo_Throws() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.GetProcessTimelineAsync(
                "C:/app/firefox.exe", BaseTime.AddHours(1), BaseTime,
                TimeSpan.FromSeconds(1), CancellationToken.None));

    [Fact]
    public async Task GetProcessDestinationsAsync_FromAfterTo_Throws() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.GetProcessDestinationsAsync(
                "C:/app/firefox.exe", BaseTime.AddHours(1), BaseTime,
                CancellationToken.None));

    [Fact]
    public async Task GetAggregateTimelineAsync_FromAfterTo_Throws() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.GetAggregateTimelineAsync(
                BaseTime.AddHours(1), BaseTime,
                TimeSpan.FromSeconds(1), CancellationToken.None));

    [Fact]
    public async Task GetProcessSummariesAsync_FromAfterTo_Throws() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.GetProcessSummariesAsync(
                BaseTime.AddHours(1), BaseTime, CancellationToken.None));

    [Fact]
    public async Task GetCountryBreakdownAsync_FromAfterTo_Throws() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.GetCountryBreakdownAsync(
                BaseTime.AddHours(1), BaseTime, CancellationToken.None));

    // Zero-width range tests. Each test seeds a bucket at BaseTime and queries
    // (BaseTime, BaseTime) — a legal but zero-width range. The SQL predicate
    // `bucket_start_ms >= fromMs AND bucket_start_ms < toMs` with from == to is
    // always false, so the seeded row must not appear. This also locks in the
    // exclusive-upper-bound semantic: if someone flips `<` to `<=`, the seeded
    // bucket at bucket_start_ms == BaseTime would be included and all 5 tests
    // fail. For the stitched methods (Aggregate/ProcessTimeline), the test also
    // proves the slicer's `sliceFromMs < sliceToMs` guard short-circuits empty
    // slices without reaching the bucket-width picker (which divides by extent).

    [Fact]
    public async Task GetProcessTimelineAsync_ZeroWidthRange_ReturnsEmpty() {
        var bucket = CreateBucket();
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        var timeline = await _store.GetProcessTimelineAsync(
            "C:/app/firefox.exe",
            BaseTime, BaseTime,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Empty(timeline);
    }

    [Fact]
    public async Task GetProcessDestinationsAsync_ZeroWidthRange_ReturnsEmpty() {
        var bucket = CreateBucket();
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        var destinations = await _store.GetProcessDestinationsAsync(
            "C:/app/firefox.exe",
            BaseTime, BaseTime,
            CancellationToken.None);

        Assert.Empty(destinations);
    }

    [Fact]
    public async Task GetAggregateTimelineAsync_ZeroWidthRange_ReturnsEmpty() {
        var bucket = CreateBucket();
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        var timeline = await _store.GetAggregateTimelineAsync(
            BaseTime, BaseTime,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Empty(timeline);
    }

    [Fact]
    public async Task GetProcessSummariesAsync_ZeroWidthRange_ReturnsEmpty() {
        var bucket = CreateBucket();
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        var summaries = await _store.GetProcessSummariesAsync(
            BaseTime, BaseTime, CancellationToken.None);

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetCountryBreakdownAsync_ZeroWidthRange_ReturnsEmpty() {
        var bucket = CreateBucket();
        await _store.WriteRawBucketsAsync([bucket], CancellationToken.None);

        var breakdown = await _store.GetCountryBreakdownAsync(
            BaseTime, BaseTime, CancellationToken.None);

        Assert.Empty(breakdown);
    }

    [Fact]
    public async Task GetAggregateTimelineAsync_StitchesAcrossTiers() {
        // Verify the stitched multi-tier query: each time slice of the range is
        // served by the finest tier whose retention covers that slice. Under the
        // Balanced preset at BaseTime:
        //   - raw   (retention 10min) serves [now-10min, now)
        //   - _10s  (retention 7d)    serves [now-7d,    now-10min)
        //   - _1m   (retention 14d)   serves [now-14d,   now-7d)
        //   - _10m  (retention 365d)  serves [now-365d,  now-14d)
        //   - _1h   (retention null)  serves [everything older, now-365d)
        // Seed one distinguishable row in each tier and query back 600 days so
        // the _1h slice is actually included in the request — this exercises
        // the null-retention branch in the slicing logic (SqliteTrafficStore.cs
        // at the `tier.Retention is null` check). Extent across the 5 seeded
        // rows is ~500 days, which rounds up past the coarsest NiceResolutionsMs
        // entry (1 day), so each row falls into its own 1-day output bucket.
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);

        // Raw: 5 minutes ago (inside raw's 10-min slice)
        await InsertDirectAsync(connectionFactory, "traffic_raw",
            bucketStart: BaseTime.AddMinutes(-5), bucketSeconds: 1,
            bytesIn: 111, bytesOut: 11);

        // _10s: 1 day ago (inside _10s's [10min, 7d] slice)
        await InsertDirectAsync(connectionFactory, "traffic_buckets_10s",
            bucketStart: BaseTime.AddDays(-1), bucketSeconds: 10,
            bytesIn: 222, bytesOut: 22);

        // _1m: 10 days ago (inside _1m's [7d, 14d] slice)
        await InsertDirectAsync(connectionFactory, "traffic_buckets_1m",
            bucketStart: BaseTime.AddDays(-10), bucketSeconds: 60,
            bytesIn: 333, bytesOut: 33);

        // _10m: 100 days ago (inside _10m's [14d, 365d] slice)
        await InsertDirectAsync(connectionFactory, "traffic_buckets_10m",
            bucketStart: BaseTime.AddDays(-100), bucketSeconds: 600,
            bytesIn: 444, bytesOut: 44);

        // _1h: 500 days ago (inside _1h's [365d, ∞) slice — null-retention branch)
        await InsertDirectAsync(connectionFactory, "traffic_buckets_1h",
            bucketStart: BaseTime.AddDays(-500), bucketSeconds: 3600,
            bytesIn: 555, bytesOut: 55);

        var timeline = await _store.GetAggregateTimelineAsync(
            BaseTime.AddDays(-600), BaseTime,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Equal(5, timeline.Count);

        // Ordered by timestamp (oldest → newest). Verify each tier's row came
        // from the correct tier by checking the byte values we seeded.
        Assert.Equal(555, timeline[0].BytesIn);  Assert.Equal(55, timeline[0].BytesOut);  // _1h
        Assert.Equal(444, timeline[1].BytesIn);  Assert.Equal(44, timeline[1].BytesOut);  // _10m
        Assert.Equal(333, timeline[2].BytesIn);  Assert.Equal(33, timeline[2].BytesOut);  // _1m
        Assert.Equal(222, timeline[3].BytesIn);  Assert.Equal(22, timeline[3].BytesOut);  // _10s
        Assert.Equal(111, timeline[4].BytesIn);  Assert.Equal(11, timeline[4].BytesOut);  // raw
    }

    [Fact]
    public async Task GetAggregateTimelineAsync_SameDataDifferentRanges_ReturnsIdenticalArrays() {
        // Locks in the "same data → same chart" guarantee. The daemon picks
        // effectiveResolutionMs purely from actual data extent (ignoring the
        // caller's resolutionMs hint), so three different request windows that
        // all wrap the same underlying data return identical arrays.
        //
        // Scenario: two rows in the _10s tier (2d4h span). Query "last 7d" /
        // "last 30d" / "all time" (56y). All three queries hit the _10s slice
        // [now-7d, now-10min) which is identical across the three requests,
        // see the same (dataMin, dataMax), pick the same nice bucket width,
        // and return identical output.
        //
        // Both rows go into _10s (not _1m) because Balanced's _10s slice spans
        // [now-7d, now-10min), which is where BOTH BaseTime-2d and BaseTime-4h
        // fall. Seeding a row in _1m at BaseTime-2d would NOT be visible to
        // the stitch — the _1m slice is [now-14d, now-7d), which BaseTime-2d
        // is newer than and thus excluded.
        var factory = new ConnectionFactory(Path.Combine(_tempDir, "beholder.db"), pooling: false);
        await InsertDirectAsync(factory, "traffic_buckets_10s",
            bucketStart: BaseTime.AddDays(-2),
            bucketSeconds: 10, bytesIn: 1000, bytesOut: 100);
        await InsertDirectAsync(factory, "traffic_buckets_10s",
            bucketStart: BaseTime.AddHours(-4),
            bucketSeconds: 10, bytesIn: 2000, bytesOut: 200);

        // Three ranges, same underlying data. Caller-side resolutions match
        // what the UI computes today: range / 300. The daemon ignores these
        // values for bucket-width purposes.
        var last7d = await _store.GetAggregateTimelineAsync(
            BaseTime.AddDays(-7), BaseTime,
            TimeSpan.FromMilliseconds(TimeSpan.FromDays(7).TotalMilliseconds / 300),
            CancellationToken.None);
        var last30d = await _store.GetAggregateTimelineAsync(
            BaseTime.AddDays(-30), BaseTime,
            TimeSpan.FromMilliseconds(TimeSpan.FromDays(30).TotalMilliseconds / 300),
            CancellationToken.None);
        var allTime = await _store.GetAggregateTimelineAsync(
            DateTimeOffset.UnixEpoch, BaseTime,
            TimeSpan.FromMilliseconds((BaseTime - DateTimeOffset.UnixEpoch).TotalMilliseconds / 300),
            CancellationToken.None);

        // All three responses must be byte-for-byte equal.
        Assert.Equal(last7d.Count, last30d.Count);
        Assert.Equal(last7d.Count, allTime.Count);

        for (int i = 0; i < last7d.Count; i++) {
            Assert.Equal(last7d[i].Timestamp, last30d[i].Timestamp);
            Assert.Equal(last7d[i].Timestamp, allTime[i].Timestamp);
            Assert.Equal(last7d[i].BytesIn, last30d[i].BytesIn);
            Assert.Equal(last7d[i].BytesIn, allTime[i].BytesIn);
            Assert.Equal(last7d[i].BytesOut, last30d[i].BytesOut);
            Assert.Equal(last7d[i].BytesOut, allTime[i].BytesOut);
        }
    }

    [Fact]
    public async Task GetAggregateTimelineAsync_TimeDriftWithinMinute_ReturnsIdenticalArrays() {
        // Locks in the minute-snapping of nowMs. Two queries taken 30 seconds
        // apart (same minute) see the same snapped nowMs, so their slice
        // boundaries are identical, extent is identical, and output is
        // byte-for-byte identical. Without minute-snapping, the slice
        // boundaries shift with each tick and the GROUP BY grid drifts,
        // causing the chart's peak bucket to flicker across NiceMax
        // decade boundaries.
        var factory = new ConnectionFactory(Path.Combine(_tempDir, "beholder.db"), pooling: false);
        await InsertDirectAsync(factory, "traffic_buckets_10s",
            bucketStart: BaseTime.AddDays(-2),
            bucketSeconds: 10, bytesIn: 1000, bytesOut: 100);
        await InsertDirectAsync(factory, "traffic_buckets_10s",
            bucketStart: BaseTime.AddHours(-4),
            bucketSeconds: 10, bytesIn: 2000, bytesOut: 200);

        var first = await _store.GetAggregateTimelineAsync(
            BaseTime.AddDays(-7), BaseTime,
            TimeSpan.FromMilliseconds(TimeSpan.FromDays(7).TotalMilliseconds / 300),
            CancellationToken.None);

        // Advance the clock by 30 seconds — still inside the same minute.
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        var second = await _store.GetAggregateTimelineAsync(
            BaseTime.AddDays(-7), BaseTime,
            TimeSpan.FromMilliseconds(TimeSpan.FromDays(7).TotalMilliseconds / 300),
            CancellationToken.None);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++) {
            Assert.Equal(first[i].Timestamp, second[i].Timestamp);
            Assert.Equal(first[i].BytesIn, second[i].BytesIn);
            Assert.Equal(first[i].BytesOut, second[i].BytesOut);
        }
    }

    /// <summary>
    /// Writes a single row directly into the given tier table, bypassing the
    /// rollup service. Used by multi-tier stitch tests to seed tier tables
    /// independently without spinning up the full rollup pipeline.
    /// </summary>
    private static async Task InsertDirectAsync(
        ConnectionFactory factory,
        string tableName,
        DateTimeOffset bucketStart,
        int bucketSeconds,
        long bytesIn,
        long bytesOut
    ) {
        using var connection = factory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {tableName}
                (process_path, process_name, remote_address, remote_port,
                 hostname, country, bytes_in, bytes_out, bucket_start_ms, bucket_seconds)
            VALUES
                ('C:/app/firefox.exe', 'firefox.exe', '93.184.216.34', 443,
                 'example.com', 'US', $bytesIn, $bytesOut, $bucketStartMs, $bucketSeconds);
            """;
        command.Parameters.AddWithValue("$bytesIn", bytesIn);
        command.Parameters.AddWithValue("$bytesOut", bytesOut);
        command.Parameters.AddWithValue("$bucketStartMs", bucketStart.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$bucketSeconds", bucketSeconds);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
