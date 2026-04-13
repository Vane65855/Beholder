using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using Beholder.Protocol.Local;
using Beholder.Ui.Helpers;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

internal sealed partial class StatusStripViewModel : ViewModelBase {
    private const int MaxSparklineSamples = 60;
    private const double SparklineWidth = 200;
    private const double SparklineHeight = 14;

    private readonly List<double> _throughputHistory = new();

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
    private string _wanSparklinePoints = "";

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
        var totalDeltaIn = batch.Snapshots.Sum(s => s.DeltaBytesIn);
        var totalDeltaOut = batch.Snapshots.Sum(s => s.DeltaBytesOut);
        var totalIn = batch.Snapshots.Sum(s => s.TotalBytesIn);
        var totalOut = batch.Snapshots.Sum(s => s.TotalBytesOut);

        OutboundTotalLabel = ByteFormatter.FormatBytes(totalOut);
        InboundTotalLabel = ByteFormatter.FormatBytes(totalIn);
        OutboundRateLabel = ByteFormatter.FormatRate(totalDeltaOut);
        InboundRateLabel = ByteFormatter.FormatRate(totalDeltaIn);
        WanTotalLabel = ByteFormatter.FormatBytes(totalIn + totalOut);

        UpdateSparkline(totalDeltaIn + totalDeltaOut);
    }

    private void UpdateSparkline(long totalRate) {
        _throughputHistory.Add(totalRate);
        while (_throughputHistory.Count > MaxSparklineSamples)
            _throughputHistory.RemoveAt(0);

        RecomputeSparklinePoints();
    }

    private void RecomputeSparklinePoints() {
        if (_throughputHistory.Count < 2) {
            WanSparklinePoints = "";
            return;
        }

        var max = _throughputHistory.Max();
        if (max == 0) max = 1;

        var step = SparklineWidth / (_throughputHistory.Count - 1);
        var sb = new StringBuilder();

        for (int i = 0; i < _throughputHistory.Count; i++) {
            var x = i * step;
            var y = SparklineHeight - (_throughputHistory[i] / max * SparklineHeight);
            if (i > 0) sb.Append(' ');
            sb.Append(FormattableString.Invariant($"{x:F1},{y:F1}"));
        }

        WanSparklinePoints = sb.ToString();
    }

    internal int SparklineSampleCount => _throughputHistory.Count;
}
