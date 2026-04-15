using Beholder.Daemon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Background service that cascades traffic data through the rollup tier stack.
/// On each tick, for every (source, target) adjacent pair, reads rows from the
/// source tier since the target tier's last bucket and inserts aggregated rows
/// into the target. Also applies per-tier retention pruning after the cascade
/// pass (null-retention tiers are skipped — they never prune).
/// </summary>
/// <remarks>
/// <para>
/// Watermark: derived from <c>MAX(bucket_start_ms)</c> of the target tier. No
/// separate watermark table. Self-correcting across daemon restarts — the
/// service always resumes from where the target tier left off.
/// </para>
/// <para>
/// First-tick catch-up: after startup, the first tick runs every cascade pair
/// regardless of <see cref="RollupTier.RollupInterval"/>. This lets the service
/// catch up data that accumulated while the daemon was stopped.
/// </para>
/// <para>
/// The rollup invariant (<c>SUM(bytes)</c> across tiers is identical for any
/// overlapping retained range) is the acceptance criterion. See
/// <c>RollupServiceTests.RollupInvariant_Holds_AcrossAllTiers</c>.
/// </para>
/// </remarks>
internal sealed class RollupService : IHostedService, IAsyncDisposable {
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    private readonly ConnectionFactory _connectionFactory;
    private readonly IOptionsMonitor<RollupOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RollupService> _logger;

    private readonly Dictionary<string, DateTimeOffset> _lastRollupByTier =
        new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _firstTickComplete;
    private bool _disposed;

    public RollupService(
        ConnectionFactory connectionFactory,
        IOptionsMonitor<RollupOptions> options,
        TimeProvider timeProvider,
        ILogger<RollupService> logger
    ) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loopTask is not null) throw new InvalidOperationException("Rollup service already started.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_cts.Token);
        _logger.LogInformation("Rollup service started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _logger.LogInformation("Rollup service stopping");
        _cts?.Cancel();
        if (_loopTask is not null) {
            try {
                await _loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // Expected on shutdown.
            }
            _loopTask = null;
        }
        _logger.LogInformation("Rollup service stopped");
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken) {
        try {
            while (!cancellationToken.IsCancellationRequested) {
                try {
                    await RunTickAsync(cancellationToken).ConfigureAwait(false);
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    _logger.LogError(ex, "Rollup tick failed; will retry on next interval");
                }

                try {
                    await Task.Delay(TickInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    return;
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected on shutdown.
        }
    }

    /// <summary>
    /// Runs one full cascade + prune pass. Exposed internally so tests can
    /// drive the service deterministically via <c>FakeTimeProvider</c> without
    /// relying on the background loop's timing.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken cancellationToken) {
        var tiers = _options.CurrentValue.Tiers;
        var now = _timeProvider.GetUtcNow();

        for (var i = 0; i < tiers.Count - 1; i++) {
            var source = tiers[i];
            var target = tiers[i + 1];

            if (!ShouldRunRollup(source, now)) continue;

            try {
                await CascadeTierAsync(source, target, now, cancellationToken).ConfigureAwait(false);
                _lastRollupByTier[source.TableName] = now;
            } catch (Exception ex) {
                _logger.LogWarning(ex,
                    "Cascade from {Source} to {Target} failed", source.TableName, target.TableName);
            }
        }

        foreach (var tier in tiers) {
            if (tier.Retention is null) continue;
            try {
                await PruneTierAsync(tier, now, cancellationToken).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Prune of {Tier} failed", tier.TableName);
            }
        }

        _firstTickComplete = true;
    }

    private bool ShouldRunRollup(RollupTier source, DateTimeOffset now) {
        // Catch up on first tick after startup regardless of rollup interval —
        // the daemon may have been offline through a scheduled rollup window.
        if (!_firstTickComplete) return true;
        if (source.RollupInterval == TimeSpan.Zero) return false;
        if (!_lastRollupByTier.TryGetValue(source.TableName, out var lastRun)) return true;
        return now - lastRun >= source.RollupInterval;
    }

    private async Task CascadeTierAsync(
        RollupTier source,
        RollupTier target,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        var targetBucketMs = target.BucketSeconds * 1000L;
        var nowMs = now.ToUnixTimeMilliseconds();
        var alignedNowMs = (nowMs / targetBucketMs) * targetBucketMs;

        using var connection = _connectionFactory.CreateConnection();

        // Watermark is the FIRST new target bucket we need to populate — i.e.,
        // the next aligned boundary AFTER the last-written target bucket. If the
        // target is empty we start from 0. Using MAX(bucket_start_ms) as the
        // lower bound directly would re-roll the last target bucket's source
        // rows and double-count them.
        long watermarkMs;
        using (var watermarkCmd = connection.CreateCommand()) {
            watermarkCmd.CommandText = $"SELECT MAX(bucket_start_ms) FROM {target.TableName};";
            var value = await watermarkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null || value is DBNull) {
                watermarkMs = 0;
            } else {
                var lastBucket = value is long l ? l : Convert.ToInt64(value);
                watermarkMs = lastBucket + targetBucketMs;
            }
        }

        if (alignedNowMs <= watermarkMs) return;

        using var transaction = connection.BeginTransaction();
        using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = $"""
            INSERT INTO {target.TableName}
                (process_path, process_name, remote_address, remote_port,
                 hostname, country, bytes_in, bytes_out, bucket_start_ms, bucket_seconds)
            SELECT process_path,
                   process_name,
                   remote_address,
                   remote_port,
                   MAX(hostname),
                   MAX(country),
                   SUM(bytes_in),
                   SUM(bytes_out),
                   (bucket_start_ms / $targetBucketMs) * $targetBucketMs AS target_bucket_start,
                   $targetBucketSeconds
            FROM {source.TableName}
            WHERE bucket_start_ms >= $watermarkMs
              AND bucket_start_ms < $alignedNowMs
            GROUP BY target_bucket_start, process_path, process_name, remote_address, remote_port;
            """;
        insertCmd.Parameters.AddWithValue("$targetBucketMs", targetBucketMs);
        insertCmd.Parameters.AddWithValue("$targetBucketSeconds", target.BucketSeconds);
        insertCmd.Parameters.AddWithValue("$watermarkMs", watermarkMs);
        insertCmd.Parameters.AddWithValue("$alignedNowMs", alignedNowMs);

        var rows = await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (rows > 0) {
            _logger.LogDebug(
                "Cascaded {Rows} rows from {Source} to {Target}",
                rows, source.TableName, target.TableName);
        }
    }

    private async Task PruneTierAsync(
        RollupTier tier,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        var cutoffMs = (now - tier.Retention!.Value).ToUnixTimeMilliseconds();

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {tier.TableName} WHERE bucket_start_ms < $cutoffMs;";
        command.Parameters.AddWithValue("$cutoffMs", cutoffMs);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (rows > 0) {
            _logger.LogDebug(
                "Pruned {Rows} rows older than {Cutoff:O} from {Tier}",
                rows, DateTimeOffset.FromUnixTimeMilliseconds(cutoffMs), tier.TableName);
        }
    }
}
