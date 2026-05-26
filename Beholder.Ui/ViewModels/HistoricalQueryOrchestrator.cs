using System;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Ui.Models;
using Beholder.Ui.Services;
using Grpc.Core;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Owns the <see cref="CancellationTokenSource"/> for whatever historical
/// query the Traffic tab currently has in flight. Each new query cancels the
/// previous one, so rapid range/process switching doesn't leave superseded
/// daemon-side stitched queries running against discarded responses.
/// </summary>
/// <remarks>
/// Also handles the gRPC cancellation quirk: <c>grpc-dotnet</c> surfaces
/// cancellation as <see cref="RpcException"/> with <see cref="StatusCode.Cancelled"/>
/// instead of <see cref="OperationCanceledException"/>. The orchestrator
/// normalises that back to OCE so callers have a single exception type for
/// "the user moved on."
/// </remarks>
internal sealed class HistoricalQueryOrchestrator : IDisposable {
    private readonly HistoricalChartLoader _loader;
    private CancellationTokenSource? _cts;

    public HistoricalQueryOrchestrator(HistoricalChartLoader loader) {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    /// <summary>
    /// Runs a range load (aggregate timeline + per-process summaries). Cancels
    /// any prior in-flight query first. Throws <see cref="OperationCanceledException"/>
    /// if superseded by a later call. Phase 9.6: <paramref name="remoteAddress"/>
    /// is the optional IP filter on the per-process summaries query.
    /// </summary>
    public async Task<HistoricalRangeResult> LoadRangeAsync(
        TimeRangeSelection range, string? remoteAddress = null
    ) {
        ArgumentNullException.ThrowIfNull(range);
        var cancellationToken = StartNew();
        try {
            return await _loader.LoadRangeAsync(range, cancellationToken, remoteAddress)
                .ConfigureAwait(false);
        } catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
            throw new OperationCanceledException("Cancelled via gRPC status", ex);
        }
    }

    /// <summary>
    /// Runs a per-process chart load for the current range + selected process.
    /// Cancels any prior in-flight query first. <paramref name="processPath"/>
    /// is <c>null</c> for the aggregate-across-all-processes chart.
    /// </summary>
    public async Task<HistoricalChartResult> LoadProcessChartAsync(
        TimeRangeSelection range, string? processPath
    ) {
        ArgumentNullException.ThrowIfNull(range);
        var cancellationToken = StartNew();
        try {
            return await _loader.LoadProcessChartAsync(range, processPath, cancellationToken)
                .ConfigureAwait(false);
        } catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
            throw new OperationCanceledException("Cancelled via gRPC status", ex);
        }
    }

    /// <summary>
    /// Cancels any in-flight query without starting a new one. Used when
    /// switching to live mode (no follow-up query needed) and on disposal.
    /// </summary>
    public void CancelInFlight() {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => CancelInFlight();

    private CancellationToken StartNew() {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }
}
