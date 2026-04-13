using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="ITrafficStore"/>. Stores per-destination
/// traffic buckets in the <c>traffic_buckets_10s</c> table and serves aggregated queries
/// for timelines, destination breakdowns, and country summaries.
/// </summary>
internal sealed class SqliteTrafficStore : ITrafficStore {
    private readonly ConnectionFactory _connectionFactory;

    public SqliteTrafficStore(ConnectionFactory connectionFactory) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task WriteBucketsAsync(
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
            INSERT INTO traffic_buckets_10s
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

        var resolutionMs = (long)resolution.TotalMilliseconds;
        if (resolutionMs <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (bucket_start_ms / $resolutionMs) * $resolutionMs AS ts,
                   SUM(bytes_in), SUM(bytes_out)
            FROM traffic_buckets_10s
            WHERE process_path = $processPath
              AND bucket_start_ms >= $fromMs
              AND bucket_start_ms < $toMs
            GROUP BY ts
            ORDER BY ts;
            """;
        command.Parameters.AddWithValue("$processPath", processPath);
        command.Parameters.AddWithValue("$fromMs", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$toMs", to.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$resolutionMs", resolutionMs);

        return await ReadTimePointsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DestinationSummary>> GetProcessDestinationsAsync(
        string processPath,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT remote_address,
                   MAX(hostname),
                   MAX(country),
                   SUM(bytes_in),
                   SUM(bytes_out),
                   COUNT(DISTINCT remote_port)
            FROM traffic_buckets_10s
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
        var resolutionMs = (long)resolution.TotalMilliseconds;
        if (resolutionMs <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (bucket_start_ms / $resolutionMs) * $resolutionMs AS ts,
                   SUM(bytes_in), SUM(bytes_out)
            FROM traffic_buckets_10s
            WHERE bucket_start_ms >= $fromMs
              AND bucket_start_ms < $toMs
            GROUP BY ts
            ORDER BY ts;
            """;
        command.Parameters.AddWithValue("$fromMs", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$toMs", to.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$resolutionMs", resolutionMs);

        return await ReadTimePointsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CountryTrafficSummary>> GetCountryBreakdownAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken
    ) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT country, SUM(bytes_in), SUM(bytes_out)
            FROM traffic_buckets_10s
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

    public async Task<long> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM traffic_buckets_10s WHERE bucket_start_ms < $cutoffMs;";
        command.Parameters.AddWithValue("$cutoffMs", cutoff.ToUnixTimeMilliseconds());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TrafficTimePoint>> ReadTimePointsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken
    ) {
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<TrafficTimePoint>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            results.Add(new TrafficTimePoint(
                timestamp: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                bytesIn: reader.GetInt64(1),
                bytesOut: reader.GetInt64(2)
            ));
        }
        return results;
    }

    private static CountryCode ParseCountryCode(string value) {
        return value switch {
            "--" => CountryCode.Local,
            "??" => CountryCode.Unknown,
            _ => CountryCode.FromAlpha2(value)
        };
    }
}
