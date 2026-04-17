using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Beholder.Ui.Helpers;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

internal sealed partial class StatusStripViewModel : ViewModelBase, IDisposable {
    // LERP smoothing factor — 0.3 per tick reaches target in ~3–4 ticks,
    // preventing the bar from jittering during bursty traffic patterns.
    private const double SmoothingFactor = 0.3;

    private readonly ProcessStateService _processStateService;

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
    private double _outboundRatio = 0.5;

    [ObservableProperty]
    private bool _hasTraffic;

    [ObservableProperty]
    private string _deviceIdLabel = "DEV-0000";

    public double InboundRatio => 1.0 - OutboundRatio;

    public StatusStripViewModel(ProcessStateService processStateService) {
        ArgumentNullException.ThrowIfNull(processStateService);
        _processStateService = processStateService;
        _processStateService.ProcessStatesUpdated += OnProcessStatesUpdated;
    }

    public void Dispose() {
        _processStateService.ProcessStatesUpdated -= OnProcessStatesUpdated;
    }

    private void OnProcessStatesUpdated(IReadOnlyDictionary<string, ProcessState> states) {
        Dispatcher.UIThread.Post(() => UpdateFromStates(states));
    }

    internal void UpdateFromStates(IReadOnlyDictionary<string, ProcessState> states) {
        long totalIn = 0, totalOut = 0;
        long totalDeltaIn = 0, totalDeltaOut = 0;

        foreach (var state in states.Values) {
            totalIn += state.TotalBytesIn;
            totalOut += state.TotalBytesOut;
            totalDeltaIn += state.DeltaBytesIn;
            totalDeltaOut += state.DeltaBytesOut;
        }

        OutboundTotalLabel = ByteFormatter.FormatBytes(totalOut);
        InboundTotalLabel = ByteFormatter.FormatBytes(totalIn);
        OutboundRateLabel = ByteFormatter.FormatRate(totalDeltaOut);
        InboundRateLabel = ByteFormatter.FormatRate(totalDeltaIn);
        WanTotalLabel = ByteFormatter.FormatBytes(totalIn + totalOut);

        UpdateRatioBar(totalDeltaOut, totalDeltaIn);
    }

    partial void OnOutboundRatioChanged(double value) {
        OnPropertyChanged(nameof(InboundRatio));
    }

    private void UpdateRatioBar(long outRate, long inRate) {
        var total = outRate + inRate;
        HasTraffic = total > 0;
        if (!HasTraffic) return;

        var targetOutRatio = outRate / (double)total;
        OutboundRatio = OutboundRatio * (1 - SmoothingFactor) + targetOutRatio * SmoothingFactor;
    }
}
