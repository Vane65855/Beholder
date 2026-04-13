using System;
using Avalonia.Threading;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Beholder.Ui.ViewModels;

internal partial class MainWindowViewModel : ViewModelBase {
    private readonly TrafficTabViewModel _trafficTab = new();
    private readonly FirewallTabViewModel _firewallTab = new();
    private readonly AlertsTabViewModel _alertsTab = new();
    private readonly MapTabViewModel _mapTab = new();
    private readonly ScannerTabViewModel _scannerTab = new();

    [ObservableProperty]
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
    public bool IsMapActive => ActiveTab == TabKind.Map;
    public bool IsScannerActive => ActiveTab == TabKind.Scanner;

    public string TrafficLabel => ActiveTab == TabKind.Traffic ? "[ TRAFFIC ]" : "TRAFFIC";
    public string FirewallLabel => ActiveTab == TabKind.Firewall ? "[ FIREWALL ]" : "FIREWALL";
    public string AlertsLabel => ActiveTab == TabKind.Alerts ? "[ ALERTS ]" : "ALERTS";
    public string MapLabel => ActiveTab == TabKind.Map ? "[ MAP ]" : "MAP";
    public string ScannerLabel => ActiveTab == TabKind.Scanner ? "[ SCANNER ]" : "SCANNER";

    public StatusStripViewModel StatusStripVm { get; }

    public MainWindowViewModel(IDaemonClient daemonClient, StatusStripViewModel statusStripVm) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(statusStripVm);
        StatusStripVm = statusStripVm;
        ActiveTabContent = _trafficTab;
        daemonClient.StateChanged += OnDaemonStateChanged;
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
        ActiveTabContent = value switch {
            TabKind.Traffic => _trafficTab,
            TabKind.Firewall => _firewallTab,
            TabKind.Alerts => _alertsTab,
            TabKind.Map => _mapTab,
            TabKind.Scanner => _scannerTab,
            _ => _trafficTab,
        };

        OnPropertyChanged(nameof(IsTrafficActive));
        OnPropertyChanged(nameof(IsFirewallActive));
        OnPropertyChanged(nameof(IsAlertsActive));
        OnPropertyChanged(nameof(IsMapActive));
        OnPropertyChanged(nameof(IsScannerActive));

        OnPropertyChanged(nameof(TrafficLabel));
        OnPropertyChanged(nameof(FirewallLabel));
        OnPropertyChanged(nameof(AlertsLabel));
        OnPropertyChanged(nameof(MapLabel));
        OnPropertyChanged(nameof(ScannerLabel));
    }

    [RelayCommand]
    private void SwitchTab(TabKind tab) => ActiveTab = tab;
}
