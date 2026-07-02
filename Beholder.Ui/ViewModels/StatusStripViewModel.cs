using System;
using System.Collections.Generic;
using Beholder.Ui.Helpers;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

internal sealed partial class StatusStripViewModel : ViewModelBase, IDisposable {
    // LERP smoothing factor — 0.3 per tick reaches target in ~3–4 ticks,
    // preventing the bar from jittering during bursty traffic patterns.
    private const double SmoothingFactor = 0.3;

    private readonly ProcessStateService _processStateService;
    private readonly IDispatcher _dispatcher;
    private readonly TotalsExclusionUiState _totalsExclusions;

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
    private string _deviceIdLabel;

    public double InboundRatio => 1.0 - OutboundRatio;

    public StatusStripViewModel(
        ProcessStateService processStateService, IDispatcher dispatcher, BuildVersion buildVersion,
        TotalsExclusionUiState totalsExclusions) {
        ArgumentNullException.ThrowIfNull(processStateService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(buildVersion);
        ArgumentNullException.ThrowIfNull(totalsExclusions);
        _processStateService = processStateService;
        _dispatcher = dispatcher;
        _totalsExclusions = totalsExclusions;
        _deviceIdLabel = buildVersion.DeviceLabel;
        _processStateService.ProcessStatesUpdated += OnProcessStatesUpdated;
    }

    public void Dispose() {
        _processStateService.ProcessStatesUpdated -= OnProcessStatesUpdated;
    }

    private void OnProcessStatesUpdated(IReadOnlyDictionary<string, ProcessState> states) {
        _dispatcher.Post(() => UpdateFromStates(states));
    }

    private void UpdateFromStates(IReadOnlyDictionary<string, ProcessState> states) {
        long totalIn = 0, totalOut = 0;
        long totalDeltaIn = 0, totalDeltaOut = 0;

        foreach (var state in states.Values) {
            // "Exclude from totals": skipped from every strip figure. The
            // per-process data still exists — this is a display aggregation.
            if (_totalsExclusions.IsExcluded(state.ProcessPath)) continue;
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
