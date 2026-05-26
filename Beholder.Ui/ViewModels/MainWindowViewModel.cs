using System;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Core;
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
    private readonly ScannerTabViewModel _scannerTab;
    private readonly SettingsTabViewModel _settingsTab;

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
        IDispatcher dispatcher,
        INotificationService notifications,
        IShellOpener shellOpener,
        IClipboardWriter clipboardWriter) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(processStateService);
        ArgumentNullException.ThrowIfNull(streamSubscriber);
        ArgumentNullException.ThrowIfNull(statusStripVm);
        ArgumentNullException.ThrowIfNull(historicalChartLoader);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(shellOpener);
        ArgumentNullException.ThrowIfNull(clipboardWriter);
        _daemonClient = daemonClient;
        _dispatcher = dispatcher;
        _trafficTab = new TrafficTabViewModel(daemonClient, processStateService, historicalChartLoader, dispatcher);
        _firewallTab = new FirewallTabViewModel(daemonClient, processStateService, streamSubscriber, dispatcher);
        // Pass NavigateToFirewallRule as the AlertsTabViewModel's deep-link
        // delegate so its ADD RULE button can switch tabs + highlight the
        // matching rule row (Phase 6.7). Notifications go through the
        // INotificationService abstraction so the platform impl is hidden.
        _alertsTab = new AlertsTabViewModel(
            daemonClient, streamSubscriber, dispatcher, notifications, NavigateToFirewallRuleAsync);
        // Phase 9.6: pass NavigateToTrafficForRemoteAddress as the Scanner
        // tab's deep-link delegate so its VIEW IN TRAFFIC button can switch
        // tabs + filter the per-process list to the selected device's IP.
        // Same delegate-via-ctor shape as the Alerts → Firewall deep-link.
        _scannerTab = new ScannerTabViewModel(
            daemonClient, streamSubscriber, dispatcher, TimeProvider.System,
            navigateToTraffic: NavigateToTrafficForRemoteAddressAsync);
        _settingsTab = new SettingsTabViewModel(
            daemonClient, dispatcher, shellOpener, clipboardWriter, TimeProvider.System);
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
    /// <remarks>
    /// Awaits the Firewall tab's <c>ActivateAsync</c> before calling
    /// <c>HighlightRow</c>: on a cold-start deep-link (user opens the app
    /// and clicks ADD RULE on Alerts before ever visiting the Firewall
    /// tab), a fire-and-forget activation would leave <c>_rowsByPath</c>
    /// empty when the synchronous highlight ran, and the highlight + scroll
    /// would silently no-op. <c>ActivateAsync</c> is idempotent, so the
    /// already-warm path returns instantly. Mirrors the shape used by
    /// <see cref="NavigateToAlertAsync"/>.
    /// </remarks>
    public async Task NavigateToFirewallRuleAsync(string processPath) {
        ActiveTab = TabKind.Firewall;
        await _firewallTab.ActivateAsync(CancellationToken.None);
        _firewallTab.HighlightRow(processPath);
    }

    /// <summary>
    /// Switch to the Alerts tab and select the alert with chain seq
    /// <paramref name="seq"/>. Used by the notification click-activation
    /// path: the toast carries the seq, App.axaml.cs's handler restores
    /// the window and calls this. The tab's <c>ActivateAsync</c> is
    /// awaited so <c>SelectBySeq</c> sees the populated list.
    /// </summary>
    public async Task NavigateToAlertAsync(long seq) {
        ActiveTab = TabKind.Alerts;
        await _alertsTab.ActivateAsync(CancellationToken.None);
        _alertsTab.SelectBySeq(seq);
    }

    /// <summary>
    /// Phase 9.6: switch the active tab to Traffic and filter its per-process
    /// list to processes that exchanged data with <paramref name="remoteAddress"/>.
    /// Backs the Scanner-tab "VIEW IN TRAFFIC" deep-link. Awaits the Traffic
    /// tab's <c>ActivateAsync</c> for contract symmetry with the other
    /// navigation methods even though the Traffic tab is reactive (no async
    /// load to wait for today — see <c>TrafficTabViewModel.ActivateAsync</c>).
    /// </summary>
    public async Task NavigateToTrafficForRemoteAddressAsync(string remoteAddress) {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteAddress);
        ActiveTab = TabKind.Traffic;
        await _trafficTab.ActivateAsync(CancellationToken.None);
        _trafficTab.ApplyRemoteAddressFilter(remoteAddress);
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
        // is shown, then short-circuits on subsequent switches. Without the
        // Alerts case, AlertsTabViewModel's snapshot fetch never runs in
        // production and the list only ever shows alerts that arrive via
        // the live broadcast stream — historic alerts in event_log stay
        // invisible until a fresh broadcast event happens to land. Fire-
        // and-forget — failures surface in each tab's own banner.
        if (value == TabKind.Firewall) {
            _ = _firewallTab.ActivateAsync(CancellationToken.None);
        } else if (value == TabKind.Alerts) {
            _ = _alertsTab.ActivateAsync(CancellationToken.None);
        } else if (value == TabKind.Scanner) {
            _ = _scannerTab.ActivateAsync(CancellationToken.None);
        } else if (value == TabKind.Settings) {
            _ = _settingsTab.ActivateAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private void SwitchTab(TabKind tab) => ActiveTab = tab;
}
