using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using Beholder.Protocol.Local;
using Beholder.Ui.Helpers;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

internal sealed partial class StatusStripViewModel : ViewModelBase {
    private const double BarTotalWidth = 80;

    // LERP smoothing factor — 0.3 per tick reaches target in ~3–4 ticks,
    // preventing the bar from jittering during bursty traffic patterns.
    private const double SmoothingFactor = 0.3;

    private double _displayedOutRatio = 0.5;

    private readonly Dictionary<string, ProcessTotals> _perProcessTotals = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string _outboundTotalLabel = "0 B";

    [ObservableProperty]
    private string _inboundTotalLabel = "0 B";

    [ObservableProperty]
    private string _outboundRateLabel = "0 B/s";

    [ObservableProperty]
    private string _inboundRateLabel = "0 B/s";

    [ObservableProperty]
    private string _wanTotalLabel = "0 B";

    [ObservableProperty]
    private double _outboundBarWidth;

    [ObservableProperty]
    private double _inboundBarWidth;

    [ObservableProperty]
    private string _deviceIdLabel = "DEV-0000";

    public StatusStripViewModel(DaemonStreamSubscriber subscriber) {
        ArgumentNullException.ThrowIfNull(subscriber);
        subscriber.CounterBatchReceived += OnCounterBatch;
    }

    private void OnCounterBatch(CounterBatch batch) {
        Dispatcher.UIThread.Post(() => UpdateFromBatch(batch));
    }

    internal void UpdateFromBatch(CounterBatch batch) {
        // Detect daemon restart: if any snapshot's total is less than what we
        // stored, the daemon reset its counters — clear stale state.
        foreach (var snapshot in batch.Snapshots) {
            if (_perProcessTotals.TryGetValue(snapshot.ProcessPath, out var existing)
                && snapshot.TotalBytesIn < existing.TotalIn) {
                _perProcessTotals.Clear();
                break;
            }
        }

        // Upsert per-process lifetime totals from this batch
        foreach (var snapshot in batch.Snapshots) {
            _perProcessTotals[snapshot.ProcessPath] = new ProcessTotals(
                snapshot.TotalBytesIn,
                snapshot.TotalBytesOut);
        }

        // Machine-wide totals: sum over ALL known processes, not just this batch
        long totalIn = 0, totalOut = 0;
        foreach (var p in _perProcessTotals.Values) {
            totalIn += p.TotalIn;
            totalOut += p.TotalOut;
        }

        // Rates: sum deltas from this batch only (idle processes have zero delta)
        var totalDeltaIn = batch.Snapshots.Sum(s => s.DeltaBytesIn);
        var totalDeltaOut = batch.Snapshots.Sum(s => s.DeltaBytesOut);

        OutboundTotalLabel = ByteFormatter.FormatBytes(totalOut);
        InboundTotalLabel = ByteFormatter.FormatBytes(totalIn);
        OutboundRateLabel = ByteFormatter.FormatRate(totalDeltaOut);
        InboundRateLabel = ByteFormatter.FormatRate(totalDeltaIn);
        WanTotalLabel = ByteFormatter.FormatBytes(totalIn + totalOut);

        UpdateRatioBar(totalDeltaOut, totalDeltaIn);
    }

    private void UpdateRatioBar(long outRate, long inRate) {
        var total = outRate + inRate;
        if (total == 0) {
            _displayedOutRatio = 0.5;
            OutboundBarWidth = 0;
            InboundBarWidth = 0;
            return;
        }

        var targetOutRatio = outRate / (double)total;
        _displayedOutRatio = _displayedOutRatio * (1 - SmoothingFactor) + targetOutRatio * SmoothingFactor;

        OutboundBarWidth = _displayedOutRatio * BarTotalWidth;
        InboundBarWidth = (1 - _displayedOutRatio) * BarTotalWidth;
    }

    internal double DisplayedOutRatio => _displayedOutRatio;

    internal int TrackedProcessCount => _perProcessTotals.Count;

    private record ProcessTotals(long TotalIn, long TotalOut);
}
