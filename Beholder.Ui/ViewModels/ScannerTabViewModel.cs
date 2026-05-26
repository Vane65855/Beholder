using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Backs the Scanner tab. Master-detail layout: a list of LAN device rows
/// on the left (most-recently-seen first, virtualized) and a detail pane
/// on the right with the selected device's full context (MAC, IP, vendor,
/// hostname, first-seen, last-seen). A "Scan now" button in the header
/// fires the Phase 9.3 <see cref="IDaemonClient.TriggerScanAsync"/> RPC
/// for an on-demand scan; the structured response renders as a transient
/// banner showing the device count or failure message.
/// </summary>
/// <remarks>
/// <para>Two data sources:</para>
/// <list type="bullet">
/// <item>Initial fetch: <see cref="IDaemonClient.ListLanDevicesAsync"/>
///   (Phase 9.3). Server-clamped at 1000 rows; returns rows ordered
///   last-seen DESC. Empty response on Linux daemons (no probe).</item>
/// <item>Live updates: <see cref="DaemonStreamSubscriber.LanDeviceFirstSeenReceived"/>
///   and <see cref="DaemonStreamSubscriber.LanDeviceMacChangedReceived"/>
///   (Phase 9.3). Daemon broadcasts these alongside the chain write
///   <see cref="LanScannerService.ProcessObservationAsync"/> performs.</item>
/// </list>
/// <para>Mirrors <see cref="AlertsTabViewModel"/>'s single-tab state
/// ownership (no cross-tab state service) and cold-start-race pattern
/// (storing the activation task, not a bool, so concurrent callers
/// await the same load). Phase 9.5 may extract a state service if the
/// Traffic ↔ Scanner cross-link adds a second consumer.</para>
/// </remarks>
internal sealed partial class ScannerTabViewModel : ViewModelBase, IDisposable {
    /// <summary>Live-cap on the in-memory device list. Matches the
    /// server-side <c>MaxLanDeviceListLimit</c> from Phase 9.3.</summary>
    private const int MaxRetainedDevices = 1000;

    /// <summary>How long the TriggerScan transient banner stays visible
    /// before auto-clearing. CTS-cancellable so a second scan within the
    /// window resets the timer to its own banner (last-write-wins).</summary>
    private static readonly TimeSpan ScanStatusVisibleFor = TimeSpan.FromSeconds(4);

    /// <summary>Tick interval for the relative-time label refresh. Cheap
    /// because virtualization caps the visible row count to the master
    /// list's viewport (~30 rows at 1280x800).</summary>
    private static readonly TimeSpan RelativeTimeTickInterval = TimeSpan.FromSeconds(1);

    private readonly IDaemonClient _daemonClient;
    private readonly DaemonStreamSubscriber _streamSubscriber;
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, Task>? _navigateToTraffic;
    private readonly Dictionary<string, LanDeviceRow> _rowByMac = new(StringComparer.Ordinal);

    private CancellationTokenSource? _activationCts;
    private CancellationTokenSource? _scanStatusCts;
    private ITimer? _relativeTimeTicker;
    private bool _disposed;

    /// <summary>
    /// In-flight (or completed) activation task. Mirrors the cold-start race
    /// fix from <see cref="AlertsTabViewModel._activationTask"/>: concurrent
    /// callers (tab-switch fire-and-forget + any future deep-link awaited
    /// call) hand back the same underlying task instead of racing on a
    /// <c>bool _activated</c> flag.
    /// </summary>
    private Task? _activationTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowLoadingState))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetailPane))]
    private LanDeviceRow? _selectedDevice;

    /// <summary>True while the <see cref="TriggerScanCommand"/> RPC is in
    /// flight. Drives the "Scan now" button's disabled state + the
    /// "Scanning…" label override.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonLabel))]
    private bool _isScanInProgress;

    /// <summary>"SCAN NOW" while idle, "SCANNING…" while a scan is in
    /// flight. Bound to the scan button's label so the same fixed-width
    /// pill absorbs the text change without reflowing the header strip.</summary>
    public string ScanButtonLabel => IsScanInProgress ? "SCANNING…" : "SCAN NOW";

    [ObservableProperty]
    private bool _hasScanStatusMessage;

    [ObservableProperty]
    private string _scanStatusMessage = string.Empty;

    /// <summary>True when the most-recent <see cref="TriggerScanCommand"/>
    /// invocation produced <c>success=false</c> (or threw). Drives the
    /// transient banner's danger vs. info color tinting.</summary>
    [ObservableProperty]
    private bool _scanStatusIsError;

    /// <summary>
    /// Phase 9.5: true while the user is editing the selected device's
    /// custom label. Toggles the detail-pane's CUSTOM NAME row between
    /// read mode (label-or-placeholder + RENAME button) and edit mode
    /// (TextBox + SAVE + CANCEL buttons).
    /// </summary>
    [ObservableProperty]
    private bool _isEditingLabel;

    /// <summary>
    /// Backing value for the label-edit <c>TextBox</c>. Bound TwoWay so the
    /// user's keystrokes flow into the VM and the
    /// <see cref="SaveLabelCommand"/> reads the final value here. Reset to
    /// the selected device's current label on each <see cref="BeginEditLabelCommand"/>.
    /// </summary>
    [ObservableProperty]
    private string _labelEditText = string.Empty;

    /// <summary>True while the <see cref="SaveLabelCommand"/> RPC is in
    /// flight. Disables the SAVE + CANCEL buttons so the user can't double-
    /// fire the call.</summary>
    [ObservableProperty]
    private bool _isSavingLabel;

    public ObservableCollection<LanDeviceRow> Devices { get; } = new();

    public bool HasDevices => Devices.Count > 0;
    public string DeviceCountLabel =>
        Devices.Count == 1 ? "1 device" : $"{Devices.Count} devices";
    public bool ShowEmptyState => !IsLoading && !HasDevices && !HasError;
    public bool ShowLoadingState => IsLoading && !HasDevices;
    public bool ShowDetailPane => SelectedDevice is not null;

    public ScannerTabViewModel(
        IDaemonClient daemonClient,
        DaemonStreamSubscriber streamSubscriber,
        IDispatcher dispatcher,
        TimeProvider timeProvider,
        Func<string, Task>? navigateToTraffic = null
    ) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(streamSubscriber);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _daemonClient = daemonClient;
        _streamSubscriber = streamSubscriber;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        // Phase 9.6: optional deep-link delegate. Null in tests that don't
        // exercise the cross-link; in production wired by MainWindowViewModel
        // to its NavigateToTrafficForRemoteAddressAsync method.
        _navigateToTraffic = navigateToTraffic;

        _streamSubscriber.LanDeviceFirstSeenReceived += OnLanDeviceFirstSeen;
        _streamSubscriber.LanDeviceMacChangedReceived += OnLanDeviceMacChanged;
        _streamSubscriber.LanDeviceLabelChangedReceived += OnLanDeviceLabelChanged;
        Devices.CollectionChanged += (_, _) => {
            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(DeviceCountLabel));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowLoadingState));
        };
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _streamSubscriber.LanDeviceFirstSeenReceived -= OnLanDeviceFirstSeen;
        _streamSubscriber.LanDeviceMacChangedReceived -= OnLanDeviceMacChanged;
        _streamSubscriber.LanDeviceLabelChangedReceived -= OnLanDeviceLabelChanged;
        _activationCts?.Cancel();
        _activationCts?.Dispose();
        _scanStatusCts?.Cancel();
        _scanStatusCts?.Dispose();
        _relativeTimeTicker?.Dispose();
    }

    /// <summary>
    /// Initial load. Idempotent — concurrent callers all hand back the same
    /// in-flight task. Mirrors <see cref="AlertsTabViewModel.ActivateAsync"/>.
    /// </summary>
    public Task ActivateAsync(CancellationToken cancellationToken) {
        if (_activationTask is not null) return _activationTask;
        _activationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activationTask = LoadInitialDevicesAsync(_activationCts.Token);
        StartRelativeTimeTicker();
        return _activationTask;
    }

    private async Task LoadInitialDevicesAsync(CancellationToken cancellationToken) {
        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try {
            // 0 = server default (200 per Phase 9.3's DefaultLanDeviceListLimit).
            var response = await _daemonClient.ListLanDevicesAsync(
                new ListLanDevicesRequest { Limit = 0 }, cancellationToken);
            foreach (var device in response.Devices) {
                UpsertDeviceCore(device, raisePropertyChangedForCounts: false);
            }
            // Auto-select the most-recent device so the detail pane has
            // content on first paint. Skipped if the list is empty.
            if (Devices.Count > 0) {
                SelectedDevice = Devices[0];
            }
            // One bulk-notify after the seed loop instead of per-row.
            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(DeviceCountLabel));
            OnPropertyChanged(nameof(ShowEmptyState));
        } catch (OperationCanceledException) {
            // Tab disposed mid-load — drop silently.
        } catch (RpcException ex) {
            HasError = true;
            ErrorMessage = $"Failed to load LAN devices: {ex.Status.Detail}";
        } catch (Exception ex) {
            HasError = true;
            ErrorMessage = $"Failed to load LAN devices: {ex.Message}";
        } finally {
            IsLoading = false;
        }
    }

    /// <summary>
    /// User clicked "Scan now". Fires the Phase 9.3 <c>TriggerScan</c> RPC,
    /// surfaces the structured response as a transient banner (success →
    /// info tint, failure → danger tint). Cancellation during scan is
    /// silently swallowed — the daemon has its own scan-gate semaphore so
    /// the call queues behind any in-flight timer scan.
    /// </summary>
    [RelayCommand]
    private async Task TriggerScan() {
        if (IsScanInProgress) return;
        IsScanInProgress = true;
        ClearScanStatus();
        try {
            var response = await _daemonClient.TriggerScanAsync(
                new TriggerScanRequest(), CancellationToken.None);
            SetScanStatus(response.Message, isError: !response.Success);
        } catch (RpcException ex) {
            SetScanStatus($"Scan failed: {ex.Status.Detail}", isError: true);
        } catch (Exception ex) {
            SetScanStatus($"Scan failed: {ex.Message}", isError: true);
        } finally {
            IsScanInProgress = false;
        }
    }

    /// <summary>
    /// Dismiss the transient scan-status banner. Cancels any pending
    /// auto-clear timer so the user's explicit dismiss doesn't get
    /// followed by a delayed "clear" that resurrects state.
    /// </summary>
    [RelayCommand]
    private void DismissScanStatus() => ClearScanStatus();

    // ---- Phase 9.6: Scanner → Traffic cross-link ----

    /// <summary>
    /// Phase 9.6: deep-link to the Traffic tab filtered by the selected
    /// device's IP. Invokes the <c>_navigateToTraffic</c> delegate
    /// (wired by <see cref="MainWindowViewModel"/> to its
    /// <c>NavigateToTrafficForRemoteAddressAsync</c> method); the destination
    /// tab sets a sticky IP filter on its per-process list. Defensive no-op
    /// when no device is selected, when the IP is empty (proto3 string
    /// default), or when no delegate is wired (tests that don't exercise
    /// the cross-link).
    /// </summary>
    [RelayCommand]
    private async Task ViewInTraffic() {
        if (SelectedDevice is null) return;
        if (string.IsNullOrEmpty(SelectedDevice.Ip)) return;
        if (_navigateToTraffic is null) return;
        await _navigateToTraffic(SelectedDevice.Ip);
    }

    // ---- Phase 9.5: label-edit state machine ----

    /// <summary>
    /// User clicked RENAME on the detail-pane's CUSTOM NAME row. Seeds the
    /// edit text from the currently-selected device's label (empty string
    /// when no label set) and flips into edit mode. No-op when no device is
    /// selected (defensive — the button is `IsVisible=ShowDetailPane` so
    /// this shouldn't trigger from the UI anyway).
    /// </summary>
    [RelayCommand]
    private void BeginEditLabel() {
        if (SelectedDevice is null) return;
        LabelEditText = SelectedDevice.Label ?? string.Empty;
        IsEditingLabel = true;
        // Clear any stale scan-status banner — the user is moving on to a
        // new interaction and an old "Scan complete: 4 devices" message is
        // a stale signal that competes with the rename's confirmation
        // message space.
        ClearScanStatus();
    }

    /// <summary>
    /// User clicked CANCEL during edit. Returns to read mode and discards
    /// the staged text. Idempotent — re-cancelling is a no-op.
    /// </summary>
    [RelayCommand]
    private void CancelEditLabel() {
        IsEditingLabel = false;
        LabelEditText = string.Empty;
    }

    /// <summary>
    /// User clicked SAVE during edit. Calls the Phase 9.3 RPC + surfaces
    /// the structured response. Re-entry guarded by <see cref="IsSavingLabel"/>
    /// so a double-click (or fast Enter-key press) doesn't queue duplicate
    /// RPCs. On success, the broadcast leg refreshes the row naturally; we
    /// additionally clear the editing state so the read-mode pill reappears.
    /// On failure, surface the message via the existing scan-status banner
    /// (Severity=Danger) — labels are infrequent enough not to need their
    /// own banner.
    /// </summary>
    [RelayCommand]
    private async Task SaveLabel() {
        if (SelectedDevice is null || IsSavingLabel) return;
        IsSavingLabel = true;
        ClearScanStatus();
        try {
            var request = new SetLanDeviceLabelRequest {
                Mac = SelectedDevice.Mac,
                Label = LabelEditText.Trim(),
            };
            var response = await _daemonClient.SetLanDeviceLabelAsync(request, CancellationToken.None);
            if (response.Success) {
                // Optimistic: flip the row's label locally so the master-list
                // primary text updates instantly. The broadcast stream will
                // also fire and re-confirm — idempotent via Mac-keyed lookup.
                SelectedDevice.Label = string.IsNullOrWhiteSpace(LabelEditText) ? null : LabelEditText.Trim();
                IsEditingLabel = false;
                LabelEditText = string.Empty;
            } else {
                SetScanStatus(response.Message, isError: true);
            }
        } catch (RpcException ex) {
            SetScanStatus($"Failed to set label: {ex.Status.Detail}", isError: true);
        } catch (Exception ex) {
            SetScanStatus($"Failed to set label: {ex.Message}", isError: true);
        } finally {
            IsSavingLabel = false;
        }
    }

    /// <summary>
    /// Live broadcast handler: the daemon's <c>LanDeviceLabelChanged</c>
    /// stream variant. Dispatcher-marshaled. Finds the row by MAC and
    /// refreshes its label. If the changed device IS the currently-selected
    /// device, the detail pane's binding sources (which target
    /// <see cref="SelectedDevice"/>) re-read via the row's observable
    /// <see cref="LanDeviceRow.Label"/> setter.
    /// </summary>
    private void OnLanDeviceLabelChanged(LanDeviceLabelChangedEvent ev) =>
        _dispatcher.Post(() => {
            if (ev.Device is null) return;
            if (_rowByMac.TryGetValue(ev.Device.Mac, out var existing)) {
                existing.RefreshFromProto(ev.Device, _timeProvider);
            }
        });

    /// <summary>
    /// Dismiss the danger-severity error banner triggered by a failed
    /// <c>ListLanDevicesAsync</c> call. Mirrors the
    /// <see cref="AlertsTabViewModel"/> / <see cref="FirewallTabViewModel"/>
    /// dismiss pattern wired to the shared <see cref="Controls.ErrorBanner"/>
    /// control's <c>DismissCommand</c>.
    /// </summary>
    [RelayCommand]
    private void DismissError() {
        HasError = false;
        ErrorMessage = string.Empty;
    }

    private void OnLanDeviceFirstSeen(LanDeviceFirstSeenEvent ev) =>
        _dispatcher.Post(() => UpsertDeviceCore(ev.Device, raisePropertyChangedForCounts: true));

    private void OnLanDeviceMacChanged(LanDeviceMacChangedEvent ev) =>
        _dispatcher.Post(() => HandleMacChanged(ev.PreviousMac, ev.Device));

    /// <summary>
    /// Insert a new device row at index 0 (newest-first) or refresh an
    /// existing row in place. Honors <see cref="MaxRetainedDevices"/> by
    /// evicting the oldest (last in the list) when the cap is hit.
    /// </summary>
    private void UpsertDeviceCore(LanDevice proto, bool raisePropertyChangedForCounts) {
        if (proto is null) return;
        if (_rowByMac.TryGetValue(proto.Mac, out var existing)) {
            existing.RefreshFromProto(proto, _timeProvider);
            // Move the row to the front to reflect the new last-seen ordering.
            var currentIndex = Devices.IndexOf(existing);
            if (currentIndex > 0) Devices.Move(currentIndex, 0);
            return;
        }
        var row = LanDeviceRow.FromProto(proto, _timeProvider);
        _rowByMac[row.Mac] = row;
        Devices.Insert(0, row);
        if (Devices.Count > MaxRetainedDevices) {
            var evicted = Devices[^1];
            Devices.RemoveAt(Devices.Count - 1);
            _rowByMac.Remove(evicted.Mac);
        }
        if (raisePropertyChangedForCounts) {
            // The CollectionChanged handler already raises counts when
            // Insert / RemoveAt fires, but the LoadInitialDevicesAsync seed
            // loop calls this with raisePropertyChangedForCounts: false and
            // bulk-notifies after the loop instead.
            //
            // (No-op intentionally: CollectionChanged covers the live path.)
        }
    }

    /// <summary>
    /// A known IP now reports a different MAC. Remove the row owned by
    /// <paramref name="previousMac"/> (which may or may not still be in the
    /// collection) and upsert the new device row. The chain audited the
    /// transition; the UI just reflects it.
    /// </summary>
    private void HandleMacChanged(string previousMac, LanDevice newDevice) {
        if (newDevice is null) return;
        if (!string.IsNullOrEmpty(previousMac) && _rowByMac.TryGetValue(previousMac, out var oldRow)) {
            _rowByMac.Remove(previousMac);
            Devices.Remove(oldRow);
            if (SelectedDevice == oldRow) SelectedDevice = null;
        }
        UpsertDeviceCore(newDevice, raisePropertyChangedForCounts: true);
    }

    private void SetScanStatus(string message, bool isError) {
        ScanStatusMessage = message;
        ScanStatusIsError = isError;
        HasScanStatusMessage = true;
        _scanStatusCts?.Cancel();
        _scanStatusCts?.Dispose();
        _scanStatusCts = new CancellationTokenSource();
        _ = AutoClearScanStatusAsync(_scanStatusCts.Token);
    }

    private async Task AutoClearScanStatusAsync(CancellationToken cancellationToken) {
        try {
            await Task.Delay(ScanStatusVisibleFor, _timeProvider, cancellationToken);
            // Re-check the token so a brand-new SetScanStatus that cancelled
            // this one doesn't get its banner cleared by the prior timer's
            // late tick.
            if (!cancellationToken.IsCancellationRequested) {
                _dispatcher.Post(ClearScanStatus);
            }
        } catch (OperationCanceledException) {
            // A subsequent SetScanStatus cancelled this timer — fine.
        }
    }

    private void ClearScanStatus() {
        HasScanStatusMessage = false;
        ScanStatusMessage = string.Empty;
        ScanStatusIsError = false;
        _scanStatusCts?.Cancel();
    }

    private void StartRelativeTimeTicker() {
        if (_relativeTimeTicker is not null) return;
        _relativeTimeTicker = _timeProvider.CreateTimer(
            _ => _dispatcher.Post(RefreshAllRelativeLabels),
            state: null,
            dueTime: RelativeTimeTickInterval,
            period: RelativeTimeTickInterval);
    }

    private void RefreshAllRelativeLabels() {
        foreach (var row in Devices) {
            row.RefreshRelativeLabels(_timeProvider);
        }
    }
}
