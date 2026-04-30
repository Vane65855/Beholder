using System;
using System.Threading;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Beholder.Ui.ViewModels;

internal partial class MainWindowViewModel : ViewModelBase, INavigationService, IDisposable {
    private readonly IDaemonClient _daemonClient;
    private readonly IDispatcher _dispatcher;
    private readonly TrafficTabViewModel _trafficTab;
    private readonly FirewallTabViewModel _firewallTab;
    private readonly AlertsTabViewModel _alertsTab;
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
        HistoricalChartLoader historicalChartLoader,
        IDispatcher dispatcher) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(processStateService);
        ArgumentNullException.ThrowIfNull(streamSubscriber);
        ArgumentNullException.ThrowIfNull(statusStripVm);
        ArgumentNullException.ThrowIfNull(historicalChartLoader);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _daemonClient = daemonClient;
        _dispatcher = dispatcher;
        _trafficTab = new TrafficTabViewModel(daemonClient, processStateService, historicalChartLoader, dispatcher);
        _firewallTab = new FirewallTabViewModel(daemonClient, processStateService, streamSubscriber, dispatcher);
        // Pass NavigateToFirewallRule as the AlertsTabViewModel's deep-link
        // delegate so its ADD RULE button can switch tabs + highlight the
        // matching rule row (Phase 6.7).
        _alertsTab = new AlertsTabViewModel(daemonClient, streamSubscriber, dispatcher, NavigateToFirewallRule);
        StatusStripVm = statusStripVm;
        ActiveTabContent = _trafficTab;
        _daemonClient.StateChanged += OnDaemonStateChanged;
    }

    /// <summary>
    /// Switch the active tab to Firewall and surface the rule row matching
    /// <paramref name="processPath"/>. Implementation of
    /// <see cref="INavigationService"/>; passed by the constructor as a
    /// delegate to <see cref="AlertsTabViewModel"/> so the alerts tab can
    /// deep-link without holding a back-reference to <c>this</c>.
    /// </summary>
    public void NavigateToFirewallRule(string processPath) {
        ActiveTab = TabKind.Firewall;
        // Ensure the Firewall tab has been activated (its rule list populated)
        // before we try to highlight a row. ActivateAsync is idempotent.
        _ = _firewallTab.ActivateAsync(CancellationToken.None);
        _firewallTab.HighlightRow(processPath);
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
        _dispatcher.Post(() => {
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
        // Lazy load: each tab's ActivateAsync fires the first time the tab
        // is shown, then short-circuits on subsequent switches. Without this
        // call, AlertsTabViewModel's snapshot fetch never runs in production
        // and the list only ever shows alerts that arrive via the live
        // broadcast stream — historic alerts in event_log stay invisible
        // until a fresh broadcast event happens to land. Fire-and-forget —
        // failures surface in each tab's own banner.
        if (value == TabKind.Firewall) {
            _ = _firewallTab.ActivateAsync(CancellationToken.None);
        } else if (value == TabKind.Alerts) {
            _ = _alertsTab.ActivateAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private void SwitchTab(TabKind tab) => ActiveTab = tab;
}
