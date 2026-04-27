using System;
using System.Threading;
using Avalonia.Threading;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Beholder.Ui.ViewModels;

internal partial class MainWindowViewModel : ViewModelBase, IDisposable {
    private readonly IDaemonClient _daemonClient;
    private readonly TrafficTabViewModel _trafficTab;
    private readonly FirewallTabViewModel _firewallTab;
    private readonly AlertsTabViewModel _alertsTab = new();
    private readonly ScannerTabViewModel _scannerTab = new();
    private readonly SettingsTabViewModel _settingsTab = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrafficActive))]
    [NotifyPropertyChangedFor(nameof(IsFirewallActive))]
    [NotifyPropertyChangedFor(nameof(IsAlertsActive))]
    [NotifyPropertyChangedFor(nameof(IsScannerActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
    [NotifyPropertyChangedFor(nameof(TrafficLabel))]
    [NotifyPropertyChangedFor(nameof(FirewallLabel))]
    [NotifyPropertyChangedFor(nameof(AlertsLabel))]
    [NotifyPropertyChangedFor(nameof(ScannerLabel))]
    [NotifyPropertyChangedFor(nameof(SettingsLabel))]
    private TabKind _activeTab = TabKind.Traffic;

    [ObservableProperty]
    private object? _activeTabContent;

    [ObservableProperty]
    private string _daemonStatusLabel = "offline";

    [ObservableProperty]
    private bool _isDaemonDisconnected = true;

    [ObservableProperty]
    private bool _isDaemonConnecting;

    [ObservableProperty]
    private bool _isDaemonConnected;

    public bool IsTrafficActive => ActiveTab == TabKind.Traffic;
    public bool IsFirewallActive => ActiveTab == TabKind.Firewall;
    public bool IsAlertsActive => ActiveTab == TabKind.Alerts;
    public bool IsScannerActive => ActiveTab == TabKind.Scanner;
    public bool IsSettingsActive => ActiveTab == TabKind.Settings;

    public string TrafficLabel => ActiveTab == TabKind.Traffic ? "[ TRAFFIC ]" : "TRAFFIC";
    public string FirewallLabel => ActiveTab == TabKind.Firewall ? "[ FIREWALL ]" : "FIREWALL";
    public string AlertsLabel => ActiveTab == TabKind.Alerts ? "[ ALERTS ]" : "ALERTS";
    public string ScannerLabel => ActiveTab == TabKind.Scanner ? "[ SCANNER ]" : "SCANNER";
    public string SettingsLabel => ActiveTab == TabKind.Settings ? "[ SETTINGS ]" : "SETTINGS";

    public StatusStripViewModel StatusStripVm { get; }

    public MainWindowViewModel(
        IDaemonClient daemonClient,
        ProcessStateService processStateService,
        DaemonStreamSubscriber streamSubscriber,
        StatusStripViewModel statusStripVm,
        HistoricalChartLoader historicalChartLoader) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(processStateService);
        ArgumentNullException.ThrowIfNull(streamSubscriber);
        ArgumentNullException.ThrowIfNull(statusStripVm);
        ArgumentNullException.ThrowIfNull(historicalChartLoader);
        _daemonClient = daemonClient;
        _trafficTab = new TrafficTabViewModel(daemonClient, processStateService, historicalChartLoader);
        _firewallTab = new FirewallTabViewModel(daemonClient, processStateService, streamSubscriber);
        StatusStripVm = statusStripVm;
        ActiveTabContent = _trafficTab;
        _daemonClient.StateChanged += OnDaemonStateChanged;
    }

    public void Dispose() {
        _daemonClient.StateChanged -= OnDaemonStateChanged;
        _trafficTab.Dispose();
        _firewallTab.Dispose();
        _alertsTab.Dispose();
        _scannerTab.Dispose();
        _settingsTab.Dispose();
        StatusStripVm.Dispose();
    }

    private void OnDaemonStateChanged(DaemonStatusInfo status) {
        Dispatcher.UIThread.Post(() => {
            DaemonStatusLabel = status.Label;
            IsDaemonDisconnected = status.State is ConnectionState.Disconnected;
            IsDaemonConnecting = status.State is ConnectionState.Connecting or ConnectionState.Reconnecting;
            IsDaemonConnected = status.State is ConnectionState.Connected;
        });
    }

    partial void OnActiveTabChanged(TabKind value) {
        // The 10 derived tab-state properties (IsTrafficActive/TrafficLabel/...)
        // are notified automatically by [NotifyPropertyChangedFor] on _activeTab.
        ActiveTabContent = value switch {
            TabKind.Traffic => _trafficTab,
            TabKind.Firewall => _firewallTab,
            TabKind.Alerts => _alertsTab,
            TabKind.Scanner => _scannerTab,
            TabKind.Settings => _settingsTab,
            _ => _trafficTab,
        };
        // Lazy load: the Firewall tab's initial fetch (rules + summaries +
        // snapshot) only fires the first time the tab is shown, then
        // ActivateAsync short-circuits on subsequent switches. Fire-and-
        // forget — failures surface in the tab's own banner.
        if (value == TabKind.Firewall) {
            _ = _firewallTab.ActivateAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private void SwitchTab(TabKind tab) => ActiveTab = tab;
}
