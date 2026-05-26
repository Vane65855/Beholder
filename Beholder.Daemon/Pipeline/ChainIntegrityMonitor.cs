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
    private readonly IChainStatusCache _chainStatusCache;
    private readonly IOptionsMonitor<AlertOptions> _options;
    private readonly IAlertSettingsState _alertSettings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChainIntegrityMonitor> _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _loopTask;
    private bool _disposed;

    public ChainIntegrityMonitor(
        IEventStore eventStore,
        IAlertEmitter alertEmitter,
        IChainStatusCache chainStatusCache,
        IOptionsMonitor<AlertOptions> options,
        IAlertSettingsState alertSettings,
        TimeProvider timeProvider,
        ILogger<ChainIntegrityMonitor> logger
    ) {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(alertEmitter);
        ArgumentNullException.ThrowIfNull(chainStatusCache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(alertSettings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _eventStore = eventStore;
        _alertEmitter = alertEmitter;
        _chainStatusCache = chainStatusCache;
        // Phase 13.3: numeric interval stays on IOptionsMonitor<AlertOptions>
        // (advanced JSON-only tuning); the kill-switch moves to the live state
        // singleton so the SetAlertSettings RPC takes effect on the next tick.
        _options = options;
        _alertSettings = alertSettings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        // Phase 13.3 behavior change: the mandatory startup chain verify ALWAYS
        // runs, regardless of EnableChainIntegrityMonitor. The toggle now
        // gates only the *periodic* verify loop (checked per-tick at line
        // RunLoopAsync below). Rationale: chain corruption is most likely to
        // be discovered at startup (power-loss mid-write, manual SQL tampering
        // between daemon runs); allowing the user to skip startup verify via
        // a UI flip would silently hide corruption — exactly the failure mode
        // the chain exists to prevent. The periodic loop is the legitimate
        // disable target — power users on low-resource hardware may want to
        // skip the hourly O(n) re-verify without disabling the startup check.
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
            // Mandatory startup verify (cannot be disabled via the UI toggle —
            // see StartAsync's docstring for the rationale). Failure here
            // produces the alert even before the periodic timer's first tick.
            await VerifyOnceAsync(cancellationToken).ConfigureAwait(false);

            var interval = TimeSpan.FromMinutes(_options.CurrentValue.ChainVerifyIntervalMinutes);
            using var timer = new PeriodicTimer(interval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
                if (!_alertSettings.EnableChainIntegrityMonitor) continue;
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
            // than a chain corruption. We also don't update the cache —
            // a transient "the verify call threw" is meaningless to surface
            // alongside the last real verification result.
            _logger.LogError(ex, "ChainIntegrityMonitor: VerifyAsync threw");
            return;
        }

        // Cache both success and verification-failure outcomes so the
        // Settings tab's Maintenance section can show "last verified: N
        // minutes ago" regardless of whether the chain was valid. The
        // user-triggered VerifyChain RPC writes the same cache; either
        // writer's most-recent result wins.
        _chainStatusCache.Update(result, _timeProvider.GetUtcNow());

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
