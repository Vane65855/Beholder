using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Phase 7 detector: periodically re-hashes every binary the
/// <see cref="IProcessRegistry"/> remembers and emits a
/// <see cref="AlertKind.HashChanged"/> alert when a binary's SHA-256 differs
/// from the previously stored value.
/// </summary>
/// <remarks>
/// <para>
/// First-hash establishes the baseline silently — a registry entry created
/// by <see cref="NewProcessDetector"/> arrives with <c>Sha256 == null</c>,
/// so the first tick after that registration computes the hash and stores
/// it without alerting. Subsequent ticks compare against the stored hash;
/// inequality emits the alert and overwrites the stored hash so a single
/// patch only alerts once.
/// </para>
/// <para>
/// Cold-start latency: a freshly-registered binary waits up to
/// <see cref="AlertOptions.BinaryHashCheckIntervalMinutes"/> for its first
/// hash. Acceptable for v1; an "eager hash on first-seen" optimization is
/// out of scope per Phase 7 plan.
/// </para>
/// </remarks>
internal sealed class BinaryHashMonitor : IHostedService, IDisposable {
    private readonly IProcessRegistry _processRegistry;
    private readonly IAlertEmitter _alertEmitter;
    private readonly IOptionsMonitor<AlertOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BinaryHashMonitor> _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _loopTask;
    private bool _disposed;

    public BinaryHashMonitor(
        IProcessRegistry processRegistry,
        IAlertEmitter alertEmitter,
        IOptionsMonitor<AlertOptions> options,
        TimeProvider timeProvider,
        ILogger<BinaryHashMonitor> logger
    ) {
        ArgumentNullException.ThrowIfNull(processRegistry);
        ArgumentNullException.ThrowIfNull(alertEmitter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _processRegistry = processRegistry;
        _alertEmitter = alertEmitter;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _loopTask = Task.Run(() => RunLoopAsync(_shutdownCts.Token), cancellationToken);
        _logger.LogInformation("BinaryHashMonitor started");
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
        _logger.LogInformation("BinaryHashMonitor stopped");
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _shutdownCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken) {
        try {
            // Read the cadence at loop entry — live reload of the interval
            // takes effect on the next daemon restart, not mid-loop. The
            // EnableHashChangeDetection flag is checked inside
            // SweepOnceAsync per tick so it can be flipped at runtime.
            var interval = TimeSpan.FromMinutes(_options.CurrentValue.BinaryHashCheckIntervalMinutes);
            using var timer = new PeriodicTimer(interval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
                await SweepOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected on shutdown.
        } catch (Exception ex) {
            _logger.LogError(ex, "BinaryHashMonitor loop crashed");
        }
    }

    /// <summary>
    /// Test seam: synchronously walks the registry and processes each entry.
    /// Tests bypass the <see cref="PeriodicTimer"/> by calling this directly,
    /// which avoids both the wait and any host-lifecycle dance. Honors the
    /// <see cref="AlertOptions.EnableHashChangeDetection"/> kill-switch as a
    /// no-op.
    /// </summary>
    internal async Task SweepOnceAsync(CancellationToken cancellationToken) {
        if (!_options.CurrentValue.EnableHashChangeDetection) return;

        IReadOnlyList<ProcessInfo> entries;
        try {
            entries = await _processRegistry.ListAllAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex, "BinaryHashMonitor: failed to enumerate registry");
            return;
        }

        var timeout = TimeSpan.FromSeconds(_options.CurrentValue.MaxFileHashTimeoutSeconds);
        foreach (var entry in entries) {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessEntryAsync(entry, timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessEntryAsync(
        ProcessInfo entry, TimeSpan timeout, CancellationToken cancellationToken
    ) {
        var current = await BinaryHasher.ComputeAsync(entry.Path, timeout, _logger, cancellationToken)
            .ConfigureAwait(false);
        if (current is null) return;  // BinaryHasher already logged; skip.

        var now = _timeProvider.GetUtcNow();

        if (entry.Sha256 is null) {
            // First-hash: establish the baseline silently.
            await UpdateRegistryAsync(entry, current, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (entry.Sha256.AsSpan().SequenceEqual(current)) {
            // Unchanged: refresh last_hash_at only.
            await UpdateRegistryAsync(entry, entry.Sha256, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Hash changed. Update the registry first so a transient alert-emit
        // failure doesn't repeat the alert on every subsequent tick (the
        // chain row is durable; the registry update is the dedup key).
        await UpdateRegistryAsync(entry, current, now, cancellationToken).ConfigureAwait(false);

        var oldHashShort = HexShort(entry.Sha256);
        var summary = $"{entry.DisplayName} binary changed (SHA-256 differs from prior {oldHashShort})";
        try {
            await _alertEmitter
                .EmitAlertAsync(AlertKind.HashChanged, entry.Path, summary, cancellationToken)
                .ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex,
                "BinaryHashMonitor: failed to emit HashChanged alert for {Path}", entry.Path);
        }
    }

    private async Task UpdateRegistryAsync(
        ProcessInfo entry, byte[] sha256, DateTimeOffset now, CancellationToken cancellationToken
    ) {
        var updated = new ProcessInfo(
            path: entry.Path,
            displayName: entry.DisplayName,
            sha256: sha256,
            firstSeen: entry.FirstSeen,
            lastSeen: entry.LastSeen,
            lastHashedAt: now);
        try {
            await _processRegistry.RegisterAsync(updated, cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex,
                "BinaryHashMonitor: failed to update registry for {Path}", entry.Path);
        }
    }

    private static string HexShort(byte[] sha256) {
        // First 8 hex chars (32 bits) is the standard "git-style" prefix —
        // enough to distinguish for human comparison without bloating the
        // alert summary. Lowercase to match common hash-display conventions.
        const int prefixBytes = 4;
        var len = Math.Min(prefixBytes, sha256.Length);
        return Convert.ToHexString(sha256, 0, len).ToLowerInvariant();
    }
}
