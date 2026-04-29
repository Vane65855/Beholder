using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Phase 7 detector: runs <see cref="IEventStore.VerifyAsync"/> at startup
/// (mandatory) and then periodically. A failure produces a
/// <see cref="AlertKind.ChainError"/> alert with the failed seq + error
/// message embedded in the summary.
/// </summary>
/// <remarks>
/// <para>
/// Verifying the chain at startup is the canonical time to catch
/// corruption — power loss mid-write, manual SQL tampering between daemon
/// runs, disk bit-rot. Periodic re-verification covers durable corruption
/// that develops while the daemon is running. Verification is O(n) over
/// the chain today (Phase 11 will introduce checkpointing); hourly is
/// fine for chains under ~100k rows.
/// </para>
/// </remarks>
internal sealed class ChainIntegrityMonitor : IHostedService, IDisposable {
    private readonly IEventStore _eventStore;
    private readonly IAlertEmitter _alertEmitter;
    private readonly IOptionsMonitor<AlertOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChainIntegrityMonitor> _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _loopTask;
    private bool _disposed;

    public ChainIntegrityMonitor(
        IEventStore eventStore,
        IAlertEmitter alertEmitter,
        IOptionsMonitor<AlertOptions> options,
        TimeProvider timeProvider,
        ILogger<ChainIntegrityMonitor> logger
    ) {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(alertEmitter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _eventStore = eventStore;
        _alertEmitter = alertEmitter;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!_options.CurrentValue.EnableChainIntegrityMonitor) {
            _logger.LogInformation(
                "ChainIntegrityMonitor disabled by AlertOptions; skipping startup + periodic verify");
            return Task.CompletedTask;
        }
        _loopTask = Task.Run(() => RunLoopAsync(_shutdownCts.Token), cancellationToken);
        _logger.LogInformation("ChainIntegrityMonitor started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        if (_loopTask is not null) {
            try {
                await _loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // Expected on shutdown.
            }
            _loopTask = null;
        }
        _logger.LogInformation("ChainIntegrityMonitor stopped");
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _shutdownCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken) {
        try {
            // Mandatory startup verify. Failure here produces the alert
            // even before the periodic timer's first tick.
            await VerifyOnceAsync(cancellationToken).ConfigureAwait(false);

            var interval = TimeSpan.FromMinutes(_options.CurrentValue.ChainVerifyIntervalMinutes);
            using var timer = new PeriodicTimer(interval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
                if (!_options.CurrentValue.EnableChainIntegrityMonitor) continue;
                await VerifyOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected on shutdown.
        } catch (Exception ex) {
            _logger.LogError(ex, "ChainIntegrityMonitor loop crashed");
        }
    }

    /// <summary>
    /// Test seam: synchronously runs one verification pass and emits an
    /// alert on failure. Tests bypass the loop and call this directly.
    /// </summary>
    internal async Task VerifyOnceAsync(CancellationToken cancellationToken) {
        ChainVerificationResult result;
        try {
            result = await _eventStore.VerifyAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            // Verify itself threw — log and bail. The next periodic tick
            // will retry; we don't synthesize a ChainError alert here
            // because the failure may be transient (e.g., DB lock) rather
            // than a chain corruption.
            _logger.LogError(ex, "ChainIntegrityMonitor: VerifyAsync threw");
            return;
        }

        if (result.IsValid) {
            _logger.LogDebug(
                "Chain verification passed: {RowsVerified} rows", result.RowsVerified);
            return;
        }

        var summary = result.FailedAtSeq.HasValue
            ? $"Chain verification failed at seq {result.FailedAtSeq.Value}: {result.ErrorMessage}"
            : $"Chain verification failed: {result.ErrorMessage}";

        try {
            await _alertEmitter
                .EmitAlertAsync(AlertKind.ChainError, processPath: string.Empty, summary, cancellationToken)
                .ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex,
                "ChainIntegrityMonitor: failed to emit ChainError alert for seq {Seq}",
                result.FailedAtSeq);
        }
    }
}
