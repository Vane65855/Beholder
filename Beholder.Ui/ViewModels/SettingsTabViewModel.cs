using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Backs the Settings tab — Phase 13.1 scope: three read-only sections
/// (Data Storage, Maintenance, About). Interactive sections (Storage
/// retention preset, Recording, Hostname Resolution, Alerts, Scanner) ship
/// in 13.2–13.6 with their own runtime-mutable state singletons per the
/// <c>FirewallEnforcementState</c> precedent.
/// </summary>
/// <remarks>
/// <para>One round-trip on tab activate (<c>GetStorageStats</c>) loads
/// per-table row counts + the database file size + the cached chain status
/// in a single response. The manual "Refresh" button on Data Storage
/// re-fires the same RPC. The "Verify chain integrity now" button on
/// Maintenance fires <c>VerifyChain</c> directly and translates its result
/// straight into <see cref="ChainStatus"/> — both writers (the periodic
/// monitor's broadcast surfaced via the next <c>GetStorageStats</c>, and
/// the user-triggered verify) update the same shared state.</para>
/// <para>Mirrors <see cref="ScannerTabViewModel"/>'s cold-start race
/// pattern (<see cref="_activationTask"/>), transient-banner pattern (CTS-
/// cancellable auto-dismiss), and 1-second relative-time ticker (via
/// <see cref="TimeProvider.CreateTimer"/>).</para>
/// </remarks>
internal sealed partial class SettingsTabViewModel : ViewModelBase, IDisposable {
    /// <summary>How long the verify-status transient banner stays visible
    /// before auto-clearing. Matches the Scanner tab's TriggerScan banner.</summary>
    private static readonly TimeSpan VerifyStatusVisibleFor = TimeSpan.FromSeconds(4);

    /// <summary>Tick interval for the relative-time label refresh on the
    /// chain-status row. Same cadence as the Scanner tab's last-seen ticker.</summary>
    private static readonly TimeSpan RelativeTimeTickInterval = TimeSpan.FromSeconds(1);

    private readonly IDaemonClient _daemonClient;
    private readonly IDispatcher _dispatcher;
    private readonly IFolderOpener _folderOpener;
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _activationCts;
    private CancellationTokenSource? _verifyStatusCts;
    private ITimer? _relativeTimeTicker;
    private bool _disposed;

    /// <summary>
    /// In-flight (or completed) activation task. Mirrors
    /// <see cref="ScannerTabViewModel"/>'s cold-start race pattern: concurrent
    /// callers (tab-switch fire-and-forget + any future deep-link awaited
    /// call) hand back the same underlying task instead of racing on a bool.
    /// </summary>
    private Task? _activationTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingState))]
    [NotifyPropertyChangedFor(nameof(ShowPopulatedState))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerifyButtonLabel))]
    private bool _isVerifyingChain;

    public string VerifyButtonLabel => IsVerifyingChain ? "VERIFYING…" : "VERIFY NOW";

    [ObservableProperty]
    private bool _hasVerifyStatus;

    [ObservableProperty]
    private string _verifyStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _verifyStatusIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPopulatedState))]
    [NotifyPropertyChangedFor(nameof(DatabasePath))]
    [NotifyPropertyChangedFor(nameof(DatabaseBytesTotalFormatted))]
    [NotifyPropertyChangedFor(nameof(HasOpenableDataFolder))]
    private GetStorageStatsResponse? _storageStats;

    public ObservableCollection<TableStatsRow> Tables { get; } = new();
    public ChainStatusRow ChainStatus { get; }
    public AboutInfo AboutInfo { get; }

    public string DatabasePath => StorageStats?.DatabasePath ?? string.Empty;
    public string DatabaseBytesTotalFormatted =>
        StorageStats is null ? string.Empty : FormatBytes(StorageStats.DatabaseBytesTotal);
    public bool HasOpenableDataFolder => !string.IsNullOrEmpty(DatabasePath);

    public bool ShowLoadingState => IsLoading && StorageStats is null;
    public bool ShowPopulatedState => !IsLoading && StorageStats is not null;

    public SettingsTabViewModel(
        IDaemonClient daemonClient,
        IDispatcher dispatcher,
        IFolderOpener folderOpener,
        TimeProvider timeProvider
    ) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(folderOpener);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _daemonClient = daemonClient;
        _dispatcher = dispatcher;
        _folderOpener = folderOpener;
        _timeProvider = timeProvider;

        // The chain-status row is observable so the 1-second ticker can
        // refresh its relative-time label without rebuilding the object.
        // Initialized to the "never verified this session" placeholder.
        ChainStatus = ChainStatusRow.FromProto(null, timeProvider);
        AboutInfo = AboutInfo.FromRunningAssembly();
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _activationCts?.Cancel();
        _activationCts?.Dispose();
        _verifyStatusCts?.Cancel();
        _verifyStatusCts?.Dispose();
        _relativeTimeTicker?.Dispose();
    }

    /// <summary>
    /// Initial load. Idempotent — concurrent callers all hand back the same
    /// in-flight task. Mirrors <see cref="ScannerTabViewModel.ActivateAsync"/>.
    /// </summary>
    public Task ActivateAsync(CancellationToken cancellationToken) {
        if (_activationTask is not null) return _activationTask;
        _activationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activationTask = LoadStorageStatsAsync(_activationCts.Token, isInitialLoad: true);
        StartRelativeTimeTicker();
        return _activationTask;
    }

    private async Task LoadStorageStatsAsync(CancellationToken cancellationToken, bool isInitialLoad) {
        if (isInitialLoad) IsLoading = true;
        else IsRefreshing = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try {
            var response = await _daemonClient.GetStorageStatsAsync(
                new GetStorageStatsRequest(), cancellationToken);
            ApplyStorageStats(response);
        } catch (OperationCanceledException) {
            // Tab disposed mid-load — drop silently.
        } catch (RpcException ex) {
            HasError = true;
            ErrorMessage = $"Failed to load storage stats: {ex.Status.Detail}";
        } catch (Exception ex) {
            HasError = true;
            ErrorMessage = $"Failed to load storage stats: {ex.Message}";
        } finally {
            if (isInitialLoad) IsLoading = false;
            else IsRefreshing = false;
        }
    }

    private void ApplyStorageStats(GetStorageStatsResponse response) {
        StorageStats = response;
        Tables.Clear();
        foreach (var table in response.Tables) {
            Tables.Add(TableStatsRow.FromProto(table));
        }
        ChainStatus.UpdateFromProto(
            response.HasChainStatus ? response.ChainStatus : null,
            _timeProvider);
    }

    /// <summary>
    /// User clicked the Data Storage section's REFRESH button. Re-fires
    /// <c>GetStorageStats</c>; the populated section stays rendered with
    /// its previous values while the call is in flight (no loading overlay
    /// flash). Failures surface in the existing error banner.
    /// </summary>
    [RelayCommand]
    private Task RefreshStorageStats() => LoadStorageStatsAsync(CancellationToken.None, isInitialLoad: false);

    /// <summary>
    /// User clicked "Verify chain integrity now". Fires the existing
    /// <c>VerifyChain</c> RPC, translates its result into a
    /// <see cref="ChainStatus"/>-shaped update, and surfaces a transient
    /// banner with the outcome. Re-entry guarded by
    /// <see cref="IsVerifyingChain"/>. The daemon-side handler also writes
    /// the same cache the periodic monitor writes — so the next
    /// <c>GetStorageStats</c> refresh agrees with what we set here.
    /// </summary>
    [RelayCommand]
    private async Task VerifyChain() {
        if (IsVerifyingChain) return;
        IsVerifyingChain = true;
        ClearVerifyStatus();
        try {
            var response = await _daemonClient.VerifyChainAsync(
                new VerifyChainRequest(), CancellationToken.None);

            // Translate the verify response into a ChainStatus snapshot
            // anchored at the wall-clock time we observed it. The daemon's
            // own cache write happened a moment earlier; the small skew is
            // harmless — the next GetStorageStats refresh will reconcile.
            var chainStatusProto = new ChainStatus {
                LastVerifiedUnixNs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                IsValid = response.IsValid,
                RowsVerified = response.RowsVerified,
                FailedAtSeq = response.FailedAtSeq,
                ErrorMessage = response.ErrorMessage,
            };
            ChainStatus.UpdateFromProto(chainStatusProto, _timeProvider);

            var summary = response.IsValid
                ? $"Chain verified: {response.RowsVerified.ToString("N0", CultureInfo.InvariantCulture)} rows OK"
                : response.FailedAtSeq > 0
                    ? $"Chain failed at seq {response.FailedAtSeq}: {response.ErrorMessage}"
                    : $"Chain verification failed: {response.ErrorMessage}";
            SetVerifyStatus(summary, isError: !response.IsValid);
        } catch (RpcException ex) {
            SetVerifyStatus($"Verify failed: {ex.Status.Detail}", isError: true);
        } catch (Exception ex) {
            SetVerifyStatus($"Verify failed: {ex.Message}", isError: true);
        } finally {
            IsVerifyingChain = false;
        }
    }

    /// <summary>
    /// User clicked "Open data folder". Opens the OS file explorer at the
    /// directory containing the SQLite database file. Failures (unlikely —
    /// the path comes from the daemon and the IFolderOpener wraps a
    /// best-effort <c>Process.Start</c>) surface via the verify-status
    /// banner since the action is from the same Maintenance section.
    /// </summary>
    [RelayCommand]
    private void OpenDataFolder() {
        if (string.IsNullOrEmpty(DatabasePath)) return;
        try {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (string.IsNullOrEmpty(directory)) return;
            _folderOpener.OpenFolder(directory);
        } catch (Exception ex) {
            SetVerifyStatus($"Failed to open data folder: {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private void DismissError() {
        HasError = false;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void DismissVerifyStatus() => ClearVerifyStatus();

    private void SetVerifyStatus(string message, bool isError) {
        VerifyStatusMessage = message;
        VerifyStatusIsError = isError;
        HasVerifyStatus = true;
        _verifyStatusCts?.Cancel();
        _verifyStatusCts?.Dispose();
        _verifyStatusCts = new CancellationTokenSource();
        _ = AutoClearVerifyStatusAsync(_verifyStatusCts.Token);
    }

    private async Task AutoClearVerifyStatusAsync(CancellationToken cancellationToken) {
        try {
            await Task.Delay(VerifyStatusVisibleFor, _timeProvider, cancellationToken);
            if (!cancellationToken.IsCancellationRequested) {
                _dispatcher.Post(ClearVerifyStatus);
            }
        } catch (OperationCanceledException) {
            // A subsequent SetVerifyStatus cancelled this timer — fine.
        }
    }

    private void ClearVerifyStatus() {
        HasVerifyStatus = false;
        VerifyStatusMessage = string.Empty;
        VerifyStatusIsError = false;
        _verifyStatusCts?.Cancel();
    }

    private void StartRelativeTimeTicker() {
        if (_relativeTimeTicker is not null) return;
        _relativeTimeTicker = _timeProvider.CreateTimer(
            _ => _dispatcher.Post(() => ChainStatus.RefreshRelativeLabel(_timeProvider)),
            state: null,
            dueTime: RelativeTimeTickInterval,
            period: RelativeTimeTickInterval);
    }

    /// <summary>
    /// Formats a byte count as "X.X UNIT" with binary (1024-based) units
    /// up to GB. Test seam exposed as internal so unit tests can exercise
    /// the formatting in isolation without spinning up the full VM.
    /// </summary>
    internal static string FormatBytes(long bytes) {
        if (bytes < 0) return "0 B";
        const long kilobyte = 1024L;
        const long megabyte = kilobyte * 1024L;
        const long gigabyte = megabyte * 1024L;
        return bytes switch {
            < kilobyte => $"{bytes} B",
            < megabyte => $"{((double)bytes / kilobyte).ToString("F1", CultureInfo.InvariantCulture)} KB",
            < gigabyte => $"{((double)bytes / megabyte).ToString("F1", CultureInfo.InvariantCulture)} MB",
            _ => $"{((double)bytes / gigabyte).ToString("F2", CultureInfo.InvariantCulture)} GB",
        };
    }
}
