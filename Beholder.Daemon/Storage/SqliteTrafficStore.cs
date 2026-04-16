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

        var resolutionMs = (long)resolution.TotalMilliseconds;
        if (resolutionMs <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));

        return await StitchMultiTierTimelineAsync(
            from, to, resolutionMs, processPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DestinationSummary>> GetProcessDestinationsAsync(
        string processPath,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

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
        var resolutionMs = (long)resolution.TotalMilliseconds;
        if (resolutionMs <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));

        return await StitchMultiTierTimelineAsync(
            from, to, resolutionMs, processPath: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stitched multi-tier timeline query. Each time slice of the range is
    /// served by the finest tier whose retention covers that slice's age —
    /// recent portions from raw 1-second data, older portions progressively
    /// coarser. Output is a uniform-bucket array aligned to <paramref name="resolutionMs"/>
    /// so <see cref="TrafficChartControl"/> can render it with uniform X-axis spacing.
    /// </summary>
    /// <summary>
    /// Discrete set of "nice" output-bucket widths. Effective resolution is
    /// rounded UP into this set so that small drifts in extent (e.g. from new
    /// live data arriving between queries) don't shift the GROUP BY grid by a
    /// few hundred ms — which would re-assign source rows to slightly different
    /// output buckets and cause the chart's peak-bucket value to fluctuate
    /// across <c>NiceMax</c> decade boundaries, producing 2× visual Y-axis
    /// jumps. Each decade has 2–3 entries; small enough that the chart doesn't
    /// visibly coarsen when the range nudges from one bucket width to the next,
    /// coarse enough that short-term drift doesn't flip the choice.
    /// </summary>
    private static readonly long[] NiceResolutionsMs = [
        1_000,                 // 1 s
        5_000,                 // 5 s
        10_000,                // 10 s
        30_000,                // 30 s
        60_000,                // 1 min
        5 * 60_000L,           // 5 min
        10 * 60_000L,          // 10 min
        30 * 60_000L,          // 30 min
        60 * 60_000L,          // 1 hr
        6 * 60 * 60_000L,      // 6 hr
        24 * 60 * 60_000L,     // 1 day
    ];

    private async Task<IReadOnlyList<TrafficTimePoint>> StitchMultiTierTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        long resolutionMs,
        string? processPath,
        CancellationToken cancellationToken
    ) {
        var tiers = _options.CurrentValue.Tiers;
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();

        // Snap nowMs DOWN to the start of the current minute. Every tier's
        // slice boundary (raw = nowMs-10min, _10s = nowMs-7d, etc.) is derived
        // from nowMs, so rapid re-queries within the same minute produce the
        // SAME slice bounds — which means the same source rows per slice,
        // which means identical merged output. Without snapping, a query at
        // t+0s and t+5s compute slice boundaries 5s apart and thus may pick
        // up/drop rows at the boundary, producing slightly different peaks
        // that NiceMax amplifies visually.
        var nowMs = (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 60_000L) * 60_000L;

        // Compute per-tier slices: each tier gets the range from its retention
        // boundary (or the start of the query, whichever is later) up to the
        // next-finer tier's retention boundary (so slices don't overlap).
        // Walk finest → coarsest, tracking the oldest time already covered.
        // Initial coverage boundary is toMs (upper query bound), not nowMs —
        // the finest tier naturally extends up to toMs so that data timestamped
        // at or after now (common in tests and live streaming) is included.
        var oldestCoveredMs = toMs; // no coverage yet; starts at query's upper bound
        var slices = new List<(RollupTier Tier, long SliceFromMs, long SliceToMs)>();

        foreach (var tier in tiers) {
            // Tier's coverage: from now back by its retention (or all of history
            // if null retention). Clamped so we don't cover beyond the query range.
            long tierCoversFromMs;
            if (tier.Retention is null) {
                tierCoversFromMs = fromMs;
            } else {
                var retentionMs = (long)tier.Retention.Value.TotalMilliseconds;
                tierCoversFromMs = Math.Max(fromMs, nowMs - retentionMs);
            }

            // This tier's slice: [tierCoversFromMs, oldestCoveredMs). Everything
            // newer than oldestCoveredMs is already handled by a finer tier.
            var sliceFromMs = tierCoversFromMs;
            var sliceToMs = oldestCoveredMs;

            if (sliceFromMs < sliceToMs) {
                // Intersect with query range in case this tier extends past `to`.
                sliceFromMs = Math.Max(sliceFromMs, fromMs);
                sliceToMs = Math.Min(sliceToMs, toMs);

                if (sliceFromMs < sliceToMs) {
                    slices.Add((tier, sliceFromMs, sliceToMs));
                }

                oldestCoveredMs = tierCoversFromMs;
            }

            // Stop once we've covered back to the query's `from`.
            if (oldestCoveredMs <= fromMs) break;
        }

        using var connection = _connectionFactory.CreateConnection();

        // Adapt the GROUP BY bucket width to the actual data extent inside the
        // requested range, not the range itself. This is what makes "Last 7 Days",
        // "Last 30 Days", and "All Time" produce identical charts when the
        // underlying data is the same 2d4h block — all three queries see the same
        // (dataMinMs, dataMaxMs), compute the same effective resolution, align to
        // the same GROUP BY grid, and return the same array. Without this, each
        // range's resolution scales to the request window, so different bucket
        // widths re-partition the same rows into different output timestamps.
        var extent = await ComputeDataExtentAsync(
            connection, slices, processPath, cancellationToken).ConfigureAwait(false);
        if (extent is null) {
            // No data in any tier for the range → empty result. Short-circuit to
            // avoid running the per-tier GROUP BY queries we know will be empty.
            return [];
        }
        var (dataMinMs, dataMaxMs) = extent.Value;
        var actualExtentMs = dataMaxMs - dataMinMs;

        // Pick the finest nice bucket width that still yields ≤400 output
        // buckets across the data extent. Purely data-driven — the caller's
        // <paramref name="resolutionMs"/> is advisory and intentionally
        // ignored, so that two queries with different request ranges over the
        // same underlying data produce identical bucket widths (and therefore
        // identical output arrays). Floor at 1s so degenerate cases with a
        // single data point still produce a usable grid.
        var target = Math.Max(actualExtentMs / 400, 1000);
        var effectiveResolutionMs = NiceResolutionsMs[^1]; // fallback: coarsest
        foreach (var r in NiceResolutionsMs) {
            if (r >= target) { effectiveResolutionMs = r; break; }
        }

        // Execute one GROUP BY query per tier slice, merging into a dictionary
        // keyed by output bucket timestamp (ms).
        var merged = new Dictionary<long, (long BytesIn, long BytesOut)>();

        foreach (var (tier, sliceFromMs, sliceToMs) in slices) {
            using var command = connection.CreateCommand();
            var whereProcess = processPath is null ? "" : "AND process_path = $processPath";
            command.CommandText = $"""
                SELECT (bucket_start_ms / $resolutionMs) * $resolutionMs AS ts,
                       SUM(bytes_in), SUM(bytes_out)
                FROM {tier.TableName}
                WHERE bucket_start_ms >= $fromMs
                  AND bucket_start_ms < $toMs
                  {whereProcess}
                GROUP BY ts
                ORDER BY ts;
                """;
            command.Parameters.AddWithValue("$fromMs", sliceFromMs);
            command.Parameters.AddWithValue("$toMs", sliceToMs);
            command.Parameters.AddWithValue("$resolutionMs", effectiveResolutionMs);
            if (processPath is not null) {
                command.Parameters.AddWithValue("$processPath", processPath);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                var ts = reader.GetInt64(0);
                var bytesIn = reader.GetInt64(1);
                var bytesOut = reader.GetInt64(2);
                if (merged.TryGetValue(ts, out var existing)) {
                    merged[ts] = (existing.BytesIn + bytesIn, existing.BytesOut + bytesOut);
                } else {
                    merged[ts] = (bytesIn, bytesOut);
                }
            }
        }

        // Sort by timestamp and convert to TrafficTimePoint list.
        var results = new List<TrafficTimePoint>(merged.Count);
        foreach (var kvp in merged.OrderBy(k => k.Key)) {
            results.Add(new TrafficTimePoint(
                timestamp: DateTimeOffset.FromUnixTimeMilliseconds(kvp.Key),
                bytesIn: kvp.Value.BytesIn,
                bytesOut: kvp.Value.BytesOut));
        }
        return results;
    }

    /// <summary>
    /// Scans each tier slice for its data extent (<c>MIN/MAX(bucket_start_ms)</c>)
    /// and returns the overall min/max across all tiers. Returns <c>null</c> when
    /// no tier has any row in its slice. Uses the same <c>processPath</c> filter as
    /// the main stitched query so per-process timelines compute extent over only
    /// that process's data. Each query is an indexed aggregate — negligible cost
    /// next to the main GROUP BY queries.
    /// </summary>
    private static async Task<(long MinMs, long MaxMs)?> ComputeDataExtentAsync(
        SqliteConnection connection,
        IReadOnlyList<(RollupTier Tier, long SliceFromMs, long SliceToMs)> slices,
        string? processPath,
        CancellationToken cancellationToken
    ) {
        long? overallMin = null;
        long? overallMax = null;

        foreach (var (tier, sliceFromMs, sliceToMs) in slices) {
            using var command = connection.CreateCommand();
            var whereProcess = processPath is null ? "" : "AND process_path = $processPath";
            command.CommandText = $"""
                SELECT MIN(bucket_start_ms), MAX(bucket_start_ms)
                FROM {tier.TableName}
                WHERE bucket_start_ms >= $fromMs
                  AND bucket_start_ms < $toMs
                  {whereProcess};
                """;
            command.Parameters.AddWithValue("$fromMs", sliceFromMs);
            command.Parameters.AddWithValue("$toMs", sliceToMs);
            if (processPath is not null) {
                command.Parameters.AddWithValue("$processPath", processPath);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                if (reader.IsDBNull(0)) continue; // no rows in this tier's slice
                var tierMin = reader.GetInt64(0);
                var tierMax = reader.GetInt64(1);
                if (overallMin is null || tierMin < overallMin) overallMin = tierMin;
                if (overallMax is null || tierMax > overallMax) overallMax = tierMax;
            }
        }

        if (overallMin is null || overallMax is null) return null;
        return (overallMin.Value, overallMax.Value);
    }

    public async Task<IReadOnlyList<ProcessTrafficSummary>> GetProcessSummariesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken
    ) {
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

    private RollupTier SelectTierForTimeline(DateTimeOffset from, TimeSpan resolution) =>
        TierSelector.Select(
            _options.CurrentValue.Tiers,
            from,
            resolution,
            _timeProvider.GetUtcNow());

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
