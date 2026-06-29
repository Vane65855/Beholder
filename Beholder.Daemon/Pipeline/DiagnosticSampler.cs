using System.Diagnostics;
using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Opt-in resource sampler for performance soaks (Phase 12.3). When
/// <see cref="DiagnosticsOptions.Enabled"/> is set it logs one structured line
/// per interval — process working set, managed heap, GC collection counts, the
/// SQLite file size, total row count, and LAN-device count — so a 24-hour run
/// can be charted for leaks or unbounded growth. Off by default, so production
/// logs stay quiet. Reuses <see cref="IStorageStatsProvider"/> for the DB
/// figures. See <c>docs/manual-tests/perf-soak.md</c>.
/// </summary>
internal sealed class DiagnosticSampler : BackgroundService {
    private const long BytesPerMb = 1024 * 1024;

    private readonly DiagnosticsOptions _options;
    private readonly IStorageStatsProvider _stats;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiagnosticSampler> _logger;

    public DiagnosticSampler(
        IOptions<DiagnosticsOptions> options,
        IStorageStatsProvider stats,
        TimeProvider timeProvider,
        ILogger<DiagnosticSampler> logger
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _stats = stats;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!_options.Enabled) return;

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));
        _logger.LogInformation("Diagnostic sampler enabled — sampling every {Seconds}s", interval.TotalSeconds);
        using var timer = new PeriodicTimer(interval, _timeProvider);
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                await SampleAsync(stoppingToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // Normal shutdown.
        }
    }

    /// <summary>Test seam: gather one resource snapshot and log it.</summary>
    internal async Task SampleAsync(CancellationToken cancellationToken) {
        StorageStats stats;
        try {
            stats = await _stats.GetAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Diagnostic sample could not read storage stats");
            return;
        }

        long totalRows = 0;
        foreach (var table in stats.Tables) totalRows += table.RowCount;

        using var process = Process.GetCurrentProcess();
        _logger.LogInformation(
            "perf-soak: workingSet={WorkingSetMb}MB managedHeap={ManagedHeapMb}MB gc=({Gc0}/{Gc1}/{Gc2}) db={DbMb}MB rows={Rows} lanDevices={LanDevices}",
            process.WorkingSet64 / BytesPerMb,
            GC.GetTotalMemory(forceFullCollection: false) / BytesPerMb,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            stats.DatabaseBytesTotal / BytesPerMb,
            totalRows,
            stats.LanDeviceCount);
    }
}
