using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public class RollupServiceTests : IDisposable {
    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly ConnectionFactory _connectionFactory;
    private readonly SqliteTrafficStore _store;
    private readonly FakeTimeProvider _timeProvider;
    private readonly FakeOptionsMonitor<RollupOptions> _options;
    private readonly RollupService _service;

    // BaseTime is aligned to a 1-hour boundary so bucket-boundary math in tests
    // is predictable.
    private static readonly DateTimeOffset BaseTime =
        new(2026, 4, 15, 12, 0, 0, TimeSpan.Zero);

    public RollupServiceTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        _timeProvider = new FakeTimeProvider(BaseTime);
        _options = new FakeOptionsMonitor<RollupOptions>(new RollupOptions());
        _store = new SqliteTrafficStore(_connectionFactory, _options, _timeProvider);
        _service = new RollupService(
            _connectionFactory,
            _options,
            _timeProvider,
            NullLogger<RollupService>.Instance);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static TrafficBucket MakeRaw(
        long bytesIn, long bytesOut, DateTimeOffset bucketStart,
        string processPath = "C:/app/firefox.exe",
        string processName = "firefox.exe",
        string remoteAddress = "1.1.1.1",
        int remotePort = 443
    ) => new(
        id: 0,
        processPath: processPath,
        processName: processName,
        remoteAddress: remoteAddress,
        remotePort: remotePort,
        hostname: "example.com",
        country: CountryCode.FromAlpha2("US"),
        bytesIn: bytesIn,
        bytesOut: bytesOut,
        bucketStart: bucketStart,
        bucketSeconds: 1);

    private long CountRows(string tableName) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        var result = command.ExecuteScalar();
        return result is long l ? l : Convert.ToInt64(result);
    }

    private long SumBytes(string tableName, DateTimeOffset from, DateTimeOffset to) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(SUM(bytes_in + bytes_out), 0) FROM {tableName}
            WHERE bucket_start_ms >= $fromMs AND bucket_start_ms < $toMs;
            """;
        command.Parameters.AddWithValue("$fromMs", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$toMs", to.ToUnixTimeMilliseconds());
        var result = command.ExecuteScalar();
        return result is long l ? l : Convert.ToInt64(result);
    }

    private async Task WriteRawBucketsDirectlyAsync(IEnumerable<TrafficBucket> buckets) {
        await _store.WriteRawBucketsAsync(buckets.ToArray(), CancellationToken.None);
    }

    [Fact]
    public async Task EmptyRaw_RollsUpNothing() {
        _timeProvider.Advance(TimeSpan.FromHours(2));
        await _service.RunTickAsync(CancellationToken.None);

        Assert.Equal(0, CountRows("traffic_raw"));
        Assert.Equal(0, CountRows("traffic_buckets_10s"));
        Assert.Equal(0, CountRows("traffic_buckets_1m"));
        Assert.Equal(0, CountRows("traffic_buckets_10m"));
        Assert.Equal(0, CountRows("traffic_buckets_1h"));
    }

    [Fact]
    public async Task RawToTenS_SingleBucket() {
        // 10 raw rows covering one 10-second window.
        var raws = Enumerable.Range(0, 10)
            .Select(i => MakeRaw(
                bytesIn: 100,
                bytesOut: 50,
                bucketStart: BaseTime.AddSeconds(i)))
            .ToList();
        await WriteRawBucketsDirectlyAsync(raws);

        _timeProvider.Advance(TimeSpan.FromSeconds(15));
        await _service.RunTickAsync(CancellationToken.None);

        Assert.Equal(1, CountRows("traffic_buckets_10s"));
        Assert.Equal(1500, SumBytes("traffic_buckets_10s", BaseTime, BaseTime.AddMinutes(1)));
    }

    [Fact]
    public async Task RawToTenS_MultipleProcesses() {
        var raws = new List<TrafficBucket>();
        for (var i = 0; i < 10; i++) {
            raws.Add(MakeRaw(
                bytesIn: 100, bytesOut: 0,
                bucketStart: BaseTime.AddSeconds(i),
                processPath: "C:/app/a.exe", processName: "a.exe"));
            raws.Add(MakeRaw(
                bytesIn: 0, bytesOut: 200,
                bucketStart: BaseTime.AddSeconds(i),
                processPath: "C:/app/b.exe", processName: "b.exe"));
        }
        await WriteRawBucketsDirectlyAsync(raws);

        _timeProvider.Advance(TimeSpan.FromSeconds(15));
        await _service.RunTickAsync(CancellationToken.None);

        // One row per process in _10s for this 10-second window.
        Assert.Equal(2, CountRows("traffic_buckets_10s"));
    }

    [Fact]
    public async Task FullCascade_PropagatesFromRawToTenS_AndOnward() {
        // Stage 60 raw rows covering a full minute.
        var raws = Enumerable.Range(0, 60)
            .Select(i => MakeRaw(
                bytesIn: 10, bytesOut: 5,
                bucketStart: BaseTime.AddSeconds(i)))
            .ToList();
        await WriteRawBucketsDirectlyAsync(raws);

        // Advance well past the 1m boundary so both raw→10s and 10s→1m are due.
        _timeProvider.Advance(TimeSpan.FromMinutes(2));
        await _service.RunTickAsync(CancellationToken.None);

        Assert.True(CountRows("traffic_buckets_10s") > 0, "_10s should have rollup rows");
        Assert.True(CountRows("traffic_buckets_1m") > 0, "_1m should have rollup rows");
    }

    [Fact]
    public async Task RollupInvariant_Holds_AcrossAllTiers() {
        // Stage deterministic raw data covering a full hour (3600 seconds).
        // One row per second with varying bytes to ensure grouping is exercised.
        var raws = new List<TrafficBucket>();
        long expectedSum = 0;
        for (var i = 0; i < 3600; i++) {
            var bytesIn = 100L + i;
            var bytesOut = 50L + (i * 2);
            expectedSum += bytesIn + bytesOut;
            raws.Add(MakeRaw(
                bytesIn: bytesIn, bytesOut: bytesOut,
                bucketStart: BaseTime.AddSeconds(i)));
        }
        await WriteRawBucketsDirectlyAsync(raws);

        // Advance enough for every tier's rollup interval to have elapsed.
        // Note: raw retention is 10 minutes, so by the time we verify, the raw
        // tier has been pruned — raw is intentionally excluded from the
        // invariant assertion since it no longer retains the queried range.
        _timeProvider.Advance(TimeSpan.FromHours(2));
        // Two ticks: first tick catches up everything (first-tick behavior),
        // second tick confirms no double-writes.
        await _service.RunTickAsync(CancellationToken.None);
        await _service.RunTickAsync(CancellationToken.None);

        var rangeStart = BaseTime;
        var rangeEnd = BaseTime.AddHours(1);

        var tenSSum = SumBytes("traffic_buckets_10s", rangeStart, rangeEnd);
        var oneMSum = SumBytes("traffic_buckets_1m", rangeStart, rangeEnd);
        var tenMSum = SumBytes("traffic_buckets_10m", rangeStart, rangeEnd);
        var oneHSum = SumBytes("traffic_buckets_1h", rangeStart, rangeEnd);

        Assert.Equal(expectedSum, tenSSum);
        Assert.Equal(expectedSum, oneMSum);
        Assert.Equal(expectedSum, tenMSum);
        Assert.Equal(expectedSum, oneHSum);
    }

    [Fact]
    public async Task Watermark_ResumesFromMaxTarget() {
        // First batch of raw data.
        var firstBatch = Enumerable.Range(0, 20)
            .Select(i => MakeRaw(
                bytesIn: 10, bytesOut: 0,
                bucketStart: BaseTime.AddSeconds(i)))
            .ToList();
        await WriteRawBucketsDirectlyAsync(firstBatch);

        _timeProvider.Advance(TimeSpan.FromSeconds(30));
        await _service.RunTickAsync(CancellationToken.None);
        var firstCount = CountRows("traffic_buckets_10s");

        // Second batch: raw data for a later time window. The watermark should
        // cause the service to skip re-rolling the first batch.
        var secondBatch = Enumerable.Range(0, 20)
            .Select(i => MakeRaw(
                bytesIn: 10, bytesOut: 0,
                bucketStart: BaseTime.AddSeconds(30 + i)))
            .ToList();
        await WriteRawBucketsDirectlyAsync(secondBatch);

        _timeProvider.Advance(TimeSpan.FromSeconds(30));
        await _service.RunTickAsync(CancellationToken.None);
        var secondCount = CountRows("traffic_buckets_10s");

        // Second batch should have added new rows without duplicating.
        Assert.True(secondCount > firstCount);

        // No duplicated sum: total = 40 raw rows × 10 bytes = 400.
        Assert.Equal(
            400,
            SumBytes("traffic_buckets_10s", BaseTime, BaseTime.AddMinutes(2)));
    }

    [Fact]
    public async Task Retention_PrunesOldRowsFromCappedTier() {
        // Write raw rows that are already older than the raw retention (10 min).
        var staleRaws = new List<TrafficBucket> {
            MakeRaw(bytesIn: 1, bytesOut: 0, bucketStart: BaseTime.AddMinutes(-20)),
            MakeRaw(bytesIn: 1, bytesOut: 0, bucketStart: BaseTime.AddMinutes(-15)),
        };
        // Plus a fresh raw row.
        staleRaws.Add(MakeRaw(bytesIn: 1, bytesOut: 0, bucketStart: BaseTime));
        await WriteRawBucketsDirectlyAsync(staleRaws);

        await _service.RunTickAsync(CancellationToken.None);

        // Stale rows should be pruned from raw (retention 10 min).
        var remainingRaw = CountRows("traffic_raw");
        Assert.True(remainingRaw < 3, $"expected stale raw rows to be pruned, found {remainingRaw}");
    }

    [Fact]
    public async Task Retention_NullRetentionTier_NeverPrunes() {
        // The _1h terminal tier has null retention. Stage ancient data there
        // via direct INSERT and verify the service leaves it alone after a tick.
        using (var connection = _connectionFactory.CreateConnection()) {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO traffic_buckets_1h
                    (process_path, process_name, remote_address, remote_port,
                     hostname, country, bytes_in, bytes_out, bucket_start_ms, bucket_seconds)
                VALUES
                    ('x', 'x', '1.1.1.1', 443, NULL, 'US', 1, 1, $ms, 3600);
                """;
            cmd.Parameters.AddWithValue("$ms", BaseTime.AddYears(-10).ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        var before = CountRows("traffic_buckets_1h");
        Assert.Equal(1, before);

        await _service.RunTickAsync(CancellationToken.None);

        var after = CountRows("traffic_buckets_1h");
        Assert.Equal(1, after);
    }

    [Fact]
    public async Task PartialBucket_NotRolled() {
        // Stage raw data covering seconds 0..5 of a 10-second window. _now
        // advances to second 7 — not past the 10s boundary. Nothing should
        // roll into _10s yet.
        var raws = Enumerable.Range(0, 5)
            .Select(i => MakeRaw(
                bytesIn: 10, bytesOut: 0,
                bucketStart: BaseTime.AddSeconds(i)))
            .ToList();
        await WriteRawBucketsDirectlyAsync(raws);

        _timeProvider.Advance(TimeSpan.FromSeconds(7));
        await _service.RunTickAsync(CancellationToken.None);

        Assert.Equal(0, CountRows("traffic_buckets_10s"));
    }

    [Fact]
    public async Task FirstTick_CatchesUpAllTiers_RegardlessOfInterval() {
        // Raw data covering multiple minutes. First tick must catch up every
        // adjacent pair — raw→10s AND 10s→1m — even though _10s tier's rollup
        // interval is 1 minute (which hasn't elapsed since startup).
        var raws = Enumerable.Range(0, 120)
            .Select(i => MakeRaw(
                bytesIn: 10, bytesOut: 0,
                bucketStart: BaseTime.AddSeconds(i)))
            .ToList();
        await WriteRawBucketsDirectlyAsync(raws);

        _timeProvider.Advance(TimeSpan.FromMinutes(3));
        await _service.RunTickAsync(CancellationToken.None);

        Assert.True(CountRows("traffic_buckets_10s") > 0);
        Assert.True(CountRows("traffic_buckets_1m") > 0);
    }

    [Fact]
    public void PresetSwitchedLive_NextCurrentValueRead_ReflectsNewPreset() {
        // Locks in the live-reload contract documented in RollupService's
        // XML remarks: preset changes applied via appsettings.json take
        // effect on the next read of CurrentValue (i.e. next rollup tick
        // or next store query). The test does NOT assert race-free mid-
        // operation behavior — that's the documented gap, not a contract.
        var tiersBefore = _options.CurrentValue.Tiers;
        // Balanced: _10s retention = 7d
        Assert.Equal(TimeSpan.FromDays(7), tiersBefore[1].Retention);

        _options.Set(new RollupOptions { Preset = RetentionPreset.Compact });

        var tiersAfter = _options.CurrentValue.Tiers;
        // Compact: _10s retention = 3d
        Assert.Equal(TimeSpan.FromDays(3), tiersAfter[1].Retention);
    }
}
