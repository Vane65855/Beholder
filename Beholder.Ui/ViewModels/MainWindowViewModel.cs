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

    public MainWindowViewModel() {
        ActiveTabContent = _trafficTab;
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
