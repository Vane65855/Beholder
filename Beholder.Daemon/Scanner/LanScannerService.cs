using Beholder.Core;
using Beholder.Daemon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Scanner;

/// <summary>
/// Periodic LAN-device-discovery scheduler. On every tick, asks
/// <see cref="ILanDeviceProbe"/> for one scan, enriches each observation via
/// <see cref="IOuiVendorLookup"/>, classifies the state transition
/// (new MAC / known MAC / known IP + new MAC), emits the matching chain event
/// to <see cref="IEventStore"/>, and upserts the row in
/// <see cref="ILanDeviceStore"/>. Linux daemons don't register an
/// <see cref="ILanDeviceProbe"/>; the constructor accepts a nullable probe
/// (ADR 007 pattern) and the service logs a warning + skips scanning when
/// the dependency is unavailable.
/// </summary>
internal sealed class LanScannerService : IHostedService, IAsyncDisposable {
    /// <summary>Lower bound on <c>ScanIntervalSeconds</c>; clamped at scheduler-startup time.</summary>
    private const int MinIntervalSeconds = 30;

    /// <summary>How long <see cref="StopAsync"/> waits for an in-flight scan to drain before logging an abandonment warning.</summary>
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(10);

    private readonly ILanDeviceProbe? _probe;
    private readonly ILanDeviceStore _store;
    private readonly IOuiVendorLookup _vendorLookup;
    private readonly IEventStore _eventStore;
    private readonly IOptionsMonitor<ScannerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LanScannerService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _scanLoop;
    private bool _disposed;

    // Lifetime-cumulative counters; logged after every scan tick. Exposed
    // internally so tests can poll until a scan completes (probe call count
    // increments before processing finishes; this counter increments after).
    private long _totalScans;
    private long _totalObservations;
    private long _totalFirstSeen;
    private long _totalMacChanged;

    internal long TotalScansCompleted => Interlocked.Read(ref _totalScans);

    public LanScannerService(
        ILanDeviceStore store,
        IOuiVendorLookup vendorLookup,
        IEventStore eventStore,
        IOptionsMonitor<ScannerOptions> options,
        TimeProvider timeProvider,
        ILogger<LanScannerService> logger,
        ILanDeviceProbe? probe = null
    ) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(vendorLookup);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _vendorLookup = vendorLookup;
        _eventStore = eventStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _probe = probe;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_probe is null) {
            _logger.LogWarning(
                "ILanDeviceProbe not registered (Linux daemon or disabled platform); LAN scanner is inactive");
            return Task.CompletedTask;
        }

        var interval = ResolveInterval();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Detach from the StartAsync caller's scope — the loop lives for the daemon's lifetime.
        _scanLoop = Task.Run(() => ScanLoopAsync(_cts.Token), CancellationToken.None);

        _logger.LogInformation("LAN scanner started (interval {Seconds} s)", interval.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (_cts is null) return;

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_scanLoop is not null) {
            try {
                await _scanLoop.WaitAsync(StopGracePeriod, cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // Expected — either the loop honored our cancel or the outer token fired.
            } catch (TimeoutException) {
                _logger.LogWarning(
                    "LAN scanner did not stop within {Seconds} s; abandoning",
                    StopGracePeriod.TotalSeconds);
            }
        }

        _logger.LogInformation(
            "LAN scanner stopped (totalScans={Scans}, observations={Observations}, firstSeen={FirstSeen}, macChanged={MacChanged})",
            Interlocked.Read(ref _totalScans),
            Interlocked.Read(ref _totalObservations),
            Interlocked.Read(ref _totalFirstSeen),
            Interlocked.Read(ref _totalMacChanged));
    }

    private async Task ScanLoopAsync(CancellationToken cancellationToken) {
        // PeriodicTimer(TimeSpan, TimeProvider) ctor is .NET 8+; bound to the
        // injected TimeProvider so tests can advance time deterministically via
        // FakeTimeProvider.Advance() rather than sleeping.
        var interval = ResolveInterval();
        using var timer = new PeriodicTimer(interval, _timeProvider);

        // Run an immediate first scan so the daemon log shows activity on
        // startup rather than waiting a full ScanIntervalSeconds.
        await SafeRunOnceAsync(cancellationToken).ConfigureAwait(false);

        try {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
                await SafeRunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // Expected on StopAsync.
        }
    }

    private async Task SafeRunOnceAsync(CancellationToken cancellationToken) {
        try {
            await RunOnceAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "LAN scan tick failed; will retry on next interval");
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken) {
        var observations = await _probe!.ScanAsync(cancellationToken).ConfigureAwait(false);

        var firstSeenThisTick = 0;
        var macChangedThisTick = 0;

        foreach (var obs in observations) {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ProcessObservationAsync(obs, cancellationToken).ConfigureAwait(false);
            if (result == ObservationResult.FirstSeen) firstSeenThisTick++;
            else if (result == ObservationResult.MacChanged) macChangedThisTick++;
        }

        Interlocked.Increment(ref _totalScans);
        Interlocked.Add(ref _totalObservations, observations.Count);
        Interlocked.Add(ref _totalFirstSeen, firstSeenThisTick);
        Interlocked.Add(ref _totalMacChanged, macChangedThisTick);

        _logger.LogInformation(
            "LAN scanner: {Observations} devices observed, {FirstSeen} first-seen, {MacChanged} mac-changed",
            observations.Count, firstSeenThisTick, macChangedThisTick);
    }

    private async Task<ObservationResult> ProcessObservationAsync(
        LanDeviceObservation obs, CancellationToken cancellationToken
    ) {
        try {
            var vendor = _vendorLookup.GetVendor(obs.Mac);
            var existingByMac = await _store.GetByMacAsync(obs.Mac, cancellationToken).ConfigureAwait(false);
            var result = ObservationResult.KnownDevice;

            if (existingByMac is null) {
                var existingByIp = await _store.GetByIpAsync(obs.Ip, cancellationToken).ConfigureAwait(false);
                if (existingByIp is not null && existingByIp.Mac != obs.Mac) {
                    await TryEmitChainEventAsync(
                        EventKind.LanDeviceMacChanged,
                        LanDevicePayloadEncoder.EncodeMacChanged(
                            obs.Ip, existingByIp.Mac, obs.Mac, existingByIp.FirstSeen),
                        cancellationToken).ConfigureAwait(false);
                    result = ObservationResult.MacChanged;
                } else {
                    await TryEmitChainEventAsync(
                        EventKind.LanDeviceFirstSeen,
                        LanDevicePayloadEncoder.EncodeFirstSeen(obs.Mac, obs.Ip, vendor, obs.Hostname),
                        cancellationToken).ConfigureAwait(false);
                    result = ObservationResult.FirstSeen;
                }
            }

            await _store.UpsertAsync(new LanDevice(
                Mac: obs.Mac,
                Ip: obs.Ip,
                Vendor: vendor,
                Hostname: obs.Hostname,
                FirstSeen: existingByMac?.FirstSeen ?? obs.ObservedAt,
                LastSeen: obs.ObservedAt), cancellationToken).ConfigureAwait(false);

            return result;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex,
                "LAN scanner failed to process observation mac={Mac} ip={Ip}; continuing",
                obs.Mac, obs.Ip);
            return ObservationResult.Failed;
        }
    }

    private async Task TryEmitChainEventAsync(
        EventKind kind, byte[] payload, CancellationToken cancellationToken
    ) {
        try {
            await _eventStore.AppendAsync(kind, payload, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            // Chain write failed but the store upsert below still runs — better
            // to have the lan_device row reflect reality than to skip both.
            _logger.LogError(ex, "LAN scanner chain write failed (kind={Kind}); continuing", kind);
        }
    }

    private TimeSpan ResolveInterval() {
        var seconds = Math.Max(_options.CurrentValue.ScanIntervalSeconds, MinIntervalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
    }

    private enum ObservationResult {
        KnownDevice,
        FirstSeen,
        MacChanged,
        Failed,
    }
}
