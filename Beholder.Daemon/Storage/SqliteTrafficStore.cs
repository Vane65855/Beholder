using Beholder.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="ITrafficStore"/>. Writes per-destination
/// 1-second raw buckets into <c>traffic_raw</c>, and serves tier-aware aggregated
/// queries over the full rollup cascade. Tier selection is delegated to
/// <see cref="TierSelector"/>; the SQL body of each query method is identical
/// across tiers — only the table name changes.
/// </summary>
internal sealed class SqliteTrafficStore : ITrafficStore {
    private readonly ConnectionFactory _connectionFactory;
    private readonly IOptionsMonitor<RollupOptions> _options;
    private readonly TimeProvider _timeProvider;

    public SqliteTrafficStore(
        ConnectionFactory connectionFactory,
        IOptionsMonitor<RollupOptions> options,
        TimeProvider timeProvider
    ) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _connectionFactory = connectionFactory;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task WriteRawBucketsAsync(
        IReadOnlyList<TrafficBucket> buckets,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Count == 0) return;

        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO traffic_raw
                (process_path, process_name, remote_address, remote_port,
                 hostname, country, bytes_in, bytes_out, bucket_start_ms, bucket_seconds)
            VALUES
                ($processPath, $processName, $remoteAddress, $remotePort,
                 $hostname, $country, $bytesIn, $bytesOut, $bucketStartMs, $bucketSeconds);
            """;

        var pProcessPath = command.Parameters.Add("$processPath", SqliteType.Text);
        var pProcessName = command.Parameters.Add("$processName", SqliteType.Text);
        var pRemoteAddress = command.Parameters.Add("$remoteAddress", SqliteType.Text);
        var pRemotePort = command.Parameters.Add("$remotePort", SqliteType.Integer);
        var pHostname = command.Parameters.Add("$hostname", SqliteType.Text);
        var pCountry = command.Parameters.Add("$country", SqliteType.Text);
        var pBytesIn = command.Parameters.Add("$bytesIn", SqliteType.Integer);
        var pBytesOut = command.Parameters.Add("$bytesOut", SqliteType.Integer);
        var pBucketStartMs = command.Parameters.Add("$bucketStartMs", SqliteType.Integer);
        var pBucketSeconds = command.Parameters.Add("$bucketSeconds", SqliteType.Integer);

        command.Prepare();

        foreach (var bucket in buckets) {
            pProcessPath.Value = bucket.ProcessPath;
            pProcessName.Value = bucket.ProcessName;
            pRemoteAddress.Value = bucket.RemoteAddress;
            pRemotePort.Value = bucket.RemotePort;
            pHostname.Value = bucket.Hostname is not null ? bucket.Hostname : DBNull.Value;
            pCountry.Value = bucket.Country.ToString();
            pBytesIn.Value = bucket.BytesIn;
            pBytesOut.Value = bucket.BytesOut;
            pBucketStartMs.Value = bucket.BucketStart.ToUnixTimeMilliseconds();
            pBucketSeconds.Value = bucket.BucketSeconds;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrafficTimePoint>> GetProcessTimelineAsync(
        string processPath,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan resolution,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);

        var resolutionMs = (long)resolution.TotalMilliseconds;
        if (resolutionMs <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));

        using var connection = _connectionFactory.CreateConnection();
        return await TimelineStitcher.StitchAsync(
            connection, _options.CurrentValue.Tiers,
            from, to, SnapNowMsToMinute(), processPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DestinationSummary>> GetProcessDestinationsAsync(
        string processPath,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);

        var tier = SelectTierForRange(from, to);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT remote_address,
                   MAX(hostname),
                   MAX(country),
                   SUM(bytes_in),
                   SUM(bytes_out),
                   COUNT(DISTINCT remote_port)
            FROM {tier.TableName}
            WHERE process_path = $processPath
              AND bucket_start_ms >= $fromMs
              AND bucket_start_ms < $toMs
            GROUP BY remote_address
            ORDER BY SUM(bytes_in) + SUM(bytes_out) DESC;
            """;
        command.Parameters.AddWithValue("$processPath", processPath);
        command.Parameters.AddWithValue("$fromMs", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$toMs", to.ToUnixTimeMilliseconds());

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<DestinationSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            results.Add(new DestinationSummary(
                remoteAddress: reader.GetString(0),
                hostname: reader.IsDBNull(1) ? null : reader.GetString(1),
                country: ParseCountryCode(reader.GetString(2)),
                totalBytesIn: reader.GetInt64(3),
                totalBytesOut: reader.GetInt64(4),
                connectionCount: reader.GetInt32(5)
            ));
        }
        return results;
    }

    public async Task<IReadOnlyList<TrafficTimePoint>> GetAggregateTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan resolution,
        CancellationToken cancellationToken
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);

        var resolutionMs = (long)resolution.TotalMilliseconds;
        if (resolutionMs <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));

        using var connection = _connectionFactory.CreateConnection();
        return await TimelineStitcher.StitchAsync(
            connection, _options.CurrentValue.Tiers,
            from, to, SnapNowMsToMinute(), processPath: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the current time as Unix milliseconds, floored to the start of
    /// the current minute. Every stitched query derives its tier slice
    /// boundaries from this value, so rapid re-queries inside the same minute
    /// produce identical slice bounds and identical merged output — protecting
    /// the "same data → same chart" contract from sub-second drift amplified
    /// by <c>NiceMax</c> decade boundaries.
    /// </summary>
    private long SnapNowMsToMinute() =>
        (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 60_000L) * 60_000L;

    public async Task<IReadOnlyList<ProcessTrafficSummary>> GetProcessSummariesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        var tier = SelectTierForRange(from, to);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT process_path, process_name,
                   SUM(bytes_in), SUM(bytes_out)
            FROM {tier.TableName}
            WHERE bucket_start_ms >= $fromMs
              AND bucket_start_ms < $toMs
            GROUP BY process_path, process_name
            ORDER BY SUM(bytes_in) + SUM(bytes_out) DESC;
            """;
        command.Parameters.AddWithValue("$fromMs", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$toMs", to.ToUnixTimeMilliseconds());

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ProcessTrafficSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            results.Add(new ProcessTrafficSummary(
                processPath: reader.GetString(0),
                processName: reader.GetString(1),
                totalBytesIn: reader.GetInt64(2),
                totalBytesOut: reader.GetInt64(3)
            ));
        }
        return results;
    }

    public async Task<IReadOnlyList<CountryTrafficSummary>> GetCountryBreakdownAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        var tier = SelectTierForRange(from, to);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT country, SUM(bytes_in), SUM(bytes_out)
            FROM {tier.TableName}
            WHERE bucket_start_ms >= $fromMs
              AND bucket_start_ms < $toMs
            GROUP BY country
            ORDER BY SUM(bytes_in) + SUM(bytes_out) DESC;
            """;
        command.Parameters.AddWithValue("$fromMs", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$toMs", to.ToUnixTimeMilliseconds());

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<CountryTrafficSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            results.Add(new CountryTrafficSummary(
                country: ParseCountryCode(reader.GetString(0)),
                totalBytesIn: reader.GetInt64(1),
                totalBytesOut: reader.GetInt64(2)
            ));
        }
        return results;
    }

    /// <summary>
    /// Tier selection for queries with no resolution parameter
    /// (<see cref="GetProcessDestinationsAsync"/>, <see cref="GetCountryBreakdownAsync"/>).
    /// Treats the query as if the caller wanted ~300 points of temporal
    /// granularity — the same heuristic timeline queries use implicitly through
    /// their resolution parameter. This keeps tier selection consistent between
    /// timeline and aggregate queries over the same range, and avoids picking
    /// the coarsest tier (which has the slowest rollup cadence and may be
    /// missing recent data).
    /// </summary>
    private RollupTier SelectTierForRange(DateTimeOffset from, DateTimeOffset to) {
        var now = _timeProvider.GetUtcNow();
        var range = to - from;
        if (range < TimeSpan.Zero) range = TimeSpan.Zero;
        var pseudoResolution = TimeSpan.FromTicks(
            Math.Max(range.Ticks / 300, TimeSpan.TicksPerSecond));
        return TierSelector.Select(
            _options.CurrentValue.Tiers,
            from,
            pseudoResolution,
            now);
    }

    private static CountryCode ParseCountryCode(string value) {
        return value switch {
            "--" => CountryCode.Local,
            "??" => CountryCode.Unknown,
            _ => CountryCode.FromAlpha2(value)
        };
    }
}
