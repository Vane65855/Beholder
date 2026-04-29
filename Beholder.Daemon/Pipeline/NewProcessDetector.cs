using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Phase 7 detector: emits a <see cref="AlertKind.NewProcess"/> alert the first
/// time a binary path appears on the network <em>ever</em>. Subscribes to
/// <see cref="IProcessFirstNetworkFlowSource.OnProcessFirstNetworkFlow"/>
/// (the engine's session-scoped fire-once-per-key event) and consults
/// <see cref="IProcessRegistry"/> for cross-restart deduplication: a binary
/// already in the registry is not a new process — it's just one the engine
/// hadn't seen since the last daemon restart.
/// </summary>
/// <remarks>
/// <para>
/// Errors per event are caught + logged: one binary's failure (filesystem,
/// SQLite, RPC) must not knock the detector offline for everything else.
/// </para>
/// <para>
/// The detector does not compute a hash here. The first hash arrives via
/// <see cref="BinaryHashMonitor"/> on its next periodic tick, which
/// establishes the SHA-256 baseline silently (no alert). Cold-start latency
/// for the first hash is therefore up to
/// <see cref="AlertOptions.BinaryHashCheckIntervalMinutes"/>.
/// </para>
/// </remarks>
internal sealed class NewProcessDetector : IHostedService {
    private readonly IProcessFirstNetworkFlowSource _flowSource;
    private readonly IProcessRegistry _processRegistry;
    private readonly IAlertEmitter _alertEmitter;
    private readonly IOptionsMonitor<AlertOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NewProcessDetector> _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _subscribed;

    public NewProcessDetector(
        IProcessFirstNetworkFlowSource flowSource,
        IProcessRegistry processRegistry,
        IAlertEmitter alertEmitter,
        IOptionsMonitor<AlertOptions> options,
        TimeProvider timeProvider,
        ILogger<NewProcessDetector> logger
    ) {
        ArgumentNullException.ThrowIfNull(flowSource);
        ArgumentNullException.ThrowIfNull(processRegistry);
        ArgumentNullException.ThrowIfNull(alertEmitter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _flowSource = flowSource;
        _processRegistry = processRegistry;
        _alertEmitter = alertEmitter;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _flowSource.OnProcessFirstNetworkFlow += OnFirstFlow;
        _subscribed = true;
        _logger.LogInformation("NewProcessDetector started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (_subscribed) {
            _flowSource.OnProcessFirstNetworkFlow -= OnFirstFlow;
            _subscribed = false;
        }
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
        _logger.LogInformation("NewProcessDetector stopped");
    }

    private void OnFirstFlow(string processPath) {
        // Fire and forget: the engine consumer thread must not block on
        // SQLite + chain I/O. Errors propagate into ProcessAsync's catch.
        _ = Task.Run(() => ProcessAsync(processPath, _shutdownCts.Token));
    }

    /// <summary>
    /// Test seam: synchronously walks the registry check + emit path so
    /// tests can observe the alert and registry update without timing on
    /// the fire-and-forget Task spawned by <see cref="OnFirstFlow"/>.
    /// </summary>
    internal async Task ProcessAsync(string processPath, CancellationToken cancellationToken) {
        try {
            if (!_options.CurrentValue.EnableNewProcessDetection) return;

            var existing = await _processRegistry.GetByPathAsync(processPath, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null) {
                // Daemon-restart case: the engine forgot, but the registry
                // remembers. Refresh last_seen so the registry reflects the
                // current observation, but do not emit an alert.
                var refreshed = new ProcessInfo(
                    path: existing.Path,
                    displayName: existing.DisplayName,
                    sha256: existing.Sha256,
                    firstSeen: existing.FirstSeen,
                    lastSeen: _timeProvider.GetUtcNow(),
                    lastHashedAt: existing.LastHashedAt);
                await _processRegistry.RegisterAsync(refreshed, cancellationToken).ConfigureAwait(false);
                return;
            }

            var now = _timeProvider.GetUtcNow();
            var displayName = ExtractDisplayName(processPath);
            var info = new ProcessInfo(
                path: processPath,
                displayName: displayName,
                sha256: null,
                firstSeen: now,
                lastSeen: now,
                lastHashedAt: null);
            await _processRegistry.RegisterAsync(info, cancellationToken).ConfigureAwait(false);

            var summary = $"{displayName} accessed the network for the first time";
            await _alertEmitter
                .EmitAlertAsync(AlertKind.NewProcess, processPath, summary, cancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected on shutdown.
        } catch (Exception ex) {
            _logger.LogError(ex,
                "NewProcessDetector failed to process {ProcessPath}", processPath);
        }
    }

    /// <summary>
    /// Returns the file name component of <paramref name="processPath"/>, or
    /// the path itself when it has no separator (defensive — should never
    /// happen for OS-supplied paths but cheap to handle).
    /// </summary>
    private static string ExtractDisplayName(string processPath) {
        var name = Path.GetFileName(processPath);
        return string.IsNullOrEmpty(name) ? processPath : name;
    }
}
