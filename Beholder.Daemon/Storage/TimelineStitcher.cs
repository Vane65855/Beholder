using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Pure static helper that composes a multi-tier timeline. Each time slice of
/// the request range is served by the finest tier whose retention covers that
/// slice's age — recent portions from raw 1-second data, older portions
/// progressively coarser. Output is a contiguous uniform-bucket array aligned
/// to an effective bucket width derived from actual data extent inside the
/// range: buckets with no traffic are zero-filled, never omitted, so index↔
/// wall-clock stays linear across the whole array (ADR 017).
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="SqliteTrafficStore"/> to keep the store focused on
/// the SQL I/O boundary. The stitcher does not own a connection factory, does
/// not know about tables beyond <see cref="RollupTier.TableName"/>, and has no
/// instance state — the caller passes the open connection, the tier list, and
/// the minute-snapped <c>nowMs</c>, making this trivially unit-testable as a
/// pure function of its inputs.
/// </para>
/// <para>
/// Caller's <c>resolutionMs</c> (when present in the RPC surface) is advisory
/// and intentionally unused here — bucket width is derived from actual data
/// extent, preserving the Phase 5.4.3 "same data → same chart" contract that
/// guarantees 7d/30d/All Time queries over the same underlying data return
/// byte-identical arrays.
/// </para>
/// </remarks>
internal static class TimelineStitcher {
    /// <summary>
    /// Discrete set of "nice" bucket widths. Effective resolution is rounded UP
    /// into this set so that small drifts in extent (e.g. from new live data
    /// arriving between queries) don't shift the GROUP BY grid by a few
    /// hundred ms — which would re-assign source rows to slightly different
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

    /// <summary>
    /// Composes a multi-tier timeline for the given range, optionally filtered
    /// by <paramref name="processPath"/>. Each tier's retention slice is
    /// queried from its own table and results are merged by output-bucket
    /// timestamp. Returns an empty list when no tier has data in the range.
    /// </summary>
    /// <param name="connection">Open SQLite connection — caller owns lifetime.</param>
    /// <param name="tiers">Tier list (typically <c>RollupOptions.Tiers</c>).</param>
    /// <param name="from">Query lower bound (inclusive).</param>
    /// <param name="to">Query upper bound (exclusive).</param>
    /// <param name="nowMs">
    /// Current time in Unix milliseconds, pre-snapped by the caller to the
    /// start of the current minute. Each tier's slice boundary
    /// (raw = <c>nowMs-10min</c>, _10s = <c>nowMs-7d</c>, etc.) is derived
    /// from this value, so rapid re-queries within the same minute produce
    /// the SAME slice bounds — same source rows per slice, identical merged
    /// output. Snapping is the caller's responsibility to keep this helper
    /// a pure function of its inputs.
    /// </param>
    /// <param name="processPath">
    /// Null → aggregate across all processes; non-null → per-process filter.
    /// </param>
    /// <param name="remoteAddress">
    /// Phase 9.6 fix: optional remote-IP filter. Null/empty → no filter
    /// (preserves the pre-9.6 contract); non-empty → restrict the per-tier
    /// GROUP BY to traffic exchanged with that IP. Composes with
    /// <paramref name="processPath"/> when both are set. Backs the Scanner
    /// → Traffic cross-link's chart-update path.
    /// </param>
    /// <param name="excludedProcessPaths">
    /// Totals-excluded process paths removed from the aggregation (null or
    /// empty = no exclusion). Only the aggregate caller passes this — a
    /// per-process timeline always includes its explicitly selected process.
    /// Applied to the data-extent scan too, so a range whose only traffic is
    /// excluded correctly yields an empty timeline.
    /// </param>
    public static async Task<IReadOnlyList<TrafficTimePoint>> StitchAsync(
        SqliteConnection connection,
        IReadOnlyList<RollupTier> tiers,
        DateTimeOffset from,
        DateTimeOffset to,
        long nowMs,
        string? processPath,
        CancellationToken cancellationToken,
        string? remoteAddress = null,
        IReadOnlyList<string>? excludedProcessPaths = null
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tiers);

        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();

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

        // Adapt the GROUP BY bucket width to the actual data extent inside the
        // requested range, not the range itself. This is what makes "Last 7 Days",
        // "Last 30 Days", and "All Time" produce identical charts when the
        // underlying data is the same 2d4h block — all three queries see the same
        // (dataMinMs, dataMaxMs), compute the same effective resolution, align to
        // the same GROUP BY grid, and return the same array.
        var extent = await ComputeDataExtentAsync(
            connection, slices, processPath, cancellationToken, remoteAddress,
            excludedProcessPaths).ConfigureAwait(false);
        if (extent is null) {
            // No data in any tier for the range → empty result. Short-circuit to
            // avoid running the per-tier GROUP BY queries we know will be empty.
            return [];
        }
        var (dataMinMs, dataMaxMs) = extent.Value;
        var actualExtentMs = dataMaxMs - dataMinMs;

        // Pick the finest nice bucket width that still yields ≤400 output
        // buckets across the data extent. Floor at 1s so degenerate cases with
        // a single data point still produce a usable grid.
        var target = Math.Max(actualExtentMs / 400, 1000);
        var effectiveResolutionMs = NiceResolutionsMs[^1]; // fallback: coarsest
        foreach (var r in NiceResolutionsMs) {
            if (r >= target) { effectiveResolutionMs = r; break; }
        }

        // Execute one GROUP BY query per tier slice, merging into a dictionary
        // keyed by output bucket timestamp (ms).
        var merged = new Dictionary<long, (long BytesIn, long BytesOut)>();

        var hasAddressFilter = !string.IsNullOrEmpty(remoteAddress);
        var whereAddress = hasAddressFilter ? "AND remote_address = $address" : string.Empty;

        foreach (var (tier, sliceFromMs, sliceToMs) in slices) {
            using var command = connection.CreateCommand();
            var whereProcess = processPath is null ? "" : "AND process_path = $processPath";
            var whereExcluded = ProcessExclusionSqlFilter.BindNotInClause(command, excludedProcessPaths);
            command.CommandText = $"""
                SELECT (bucket_start_ms / $resolutionMs) * $resolutionMs AS ts,
                       SUM(bytes_in), SUM(bytes_out)
                FROM {tier.TableName}
                WHERE bucket_start_ms >= $fromMs
                  AND bucket_start_ms < $toMs
                  {whereProcess}
                  {whereAddress}
                  {whereExcluded}
                GROUP BY ts
                ORDER BY ts;
                """;
            command.Parameters.AddWithValue("$fromMs", sliceFromMs);
            command.Parameters.AddWithValue("$toMs", sliceToMs);
            command.Parameters.AddWithValue("$resolutionMs", effectiveResolutionMs);
            if (processPath is not null) {
                command.Parameters.AddWithValue("$processPath", processPath);
            }
            if (hasAddressFilter) {
                command.Parameters.AddWithValue("$address", remoteAddress!);
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

        if (merged.Count == 0) return [];

        // Expand the merged buckets onto the uniform grid from first to last
        // bucket, zero-filling empty buckets. Gaps must render as flat zero
        // lines, not be compressed away: the chart maps pixels to wall-clock
        // linearly across the array, and the selection feature converts those
        // pixels back into query windows (ADR 017). All tiers share the same
        // GROUP BY alignment, so stepping by the resolution hits every key.
        var gridStartMs = long.MaxValue;
        var gridEndMs = long.MinValue;
        foreach (var ts in merged.Keys) {
            if (ts < gridStartMs) gridStartMs = ts;
            if (ts > gridEndMs) gridEndMs = ts;
        }

        var bucketCount = (gridEndMs - gridStartMs) / effectiveResolutionMs + 1;
        var results = new List<TrafficTimePoint>((int)bucketCount);
        for (var ts = gridStartMs; ts <= gridEndMs; ts += effectiveResolutionMs) {
            merged.TryGetValue(ts, out var bucketBytes);
            results.Add(new TrafficTimePoint(
                timestamp: DateTimeOffset.FromUnixTimeMilliseconds(ts),
                bytesIn: bucketBytes.BytesIn,
                bytesOut: bucketBytes.BytesOut));
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
        CancellationToken cancellationToken,
        string? remoteAddress = null,
        IReadOnlyList<string>? excludedProcessPaths = null
    ) {
        long? overallMin = null;
        long? overallMax = null;

        var hasAddressFilter = !string.IsNullOrEmpty(remoteAddress);
        var whereAddress = hasAddressFilter ? "AND remote_address = $address" : string.Empty;

        foreach (var (tier, sliceFromMs, sliceToMs) in slices) {
            using var command = connection.CreateCommand();
            var whereProcess = processPath is null ? "" : "AND process_path = $processPath";
            var whereExcluded = ProcessExclusionSqlFilter.BindNotInClause(command, excludedProcessPaths);
            command.CommandText = $"""
                SELECT MIN(bucket_start_ms), MAX(bucket_start_ms)
                FROM {tier.TableName}
                WHERE bucket_start_ms >= $fromMs
                  AND bucket_start_ms < $toMs
                  {whereProcess}
                  {whereAddress}
                  {whereExcluded};
                """;
            command.Parameters.AddWithValue("$fromMs", sliceFromMs);
            command.Parameters.AddWithValue("$toMs", sliceToMs);
            if (processPath is not null) {
                command.Parameters.AddWithValue("$processPath", processPath);
            }
            if (hasAddressFilter) {
                command.Parameters.AddWithValue("$address", remoteAddress!);
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
}
