using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// (Data Storage, Maintenance, About). Phase 13.1.1 added the visual polish:
/// grouped traffic-tier table, proportional bars, chain-status pill, ASCII
/// brand mark, MOTD status strip, clickable links, copy-to-clipboard
/// buttons. Interactive sections (Storage retention preset, Recording,
/// Hostname Resolution, Alerts, Scanner) ship in 13.2–13.6.
/// </summary>
/// <remarks>
/// <para>One round-trip on tab activate (<c>GetStorageStats</c>) loads
/// per-table row counts + the database file size + the cached chain status
/// + daemon-start + chain-first-event + LAN-device count in a single
/// response. The manual "Refresh" button on Data Storage re-fires the same
/// RPC. The "Verify chain integrity now" button on Maintenance fires
/// <c>VerifyChain</c> directly and translates its result straight into
/// <see cref="ChainStatus"/> — both writers (the periodic monitor's
/// broadcast surfaced via the next <c>GetStorageStats</c>, and the
/// user-triggered verify) update the same shared state.</para>
/// <para>This class is intentionally large (~400 LOC) — above CLAUDE.md's
/// soft ~200-LOC threshold. Every concern is single-tab and tightly
/// related (state machine + 6 commands + cascade-ratio derivations +
/// ticker-driven label refresh). A split would mostly move pure plumbing
/// into a child VM with no real seam, increasing surface area without
/// improving readability. Re-evaluate if the tab grows past the polish
/// pass.</para>
/// </remarks>
internal sealed partial class SettingsTabViewModel : ViewModelBase, IDisposable {
    /// <summary>How long the verify-status transient banner stays visible
    /// before auto-clearing. Matches the Scanner tab's TriggerScan banner.</summary>
    private static readonly TimeSpan VerifyStatusVisibleFor = TimeSpan.FromSeconds(4);

    /// <summary>Tick interval for the relative-time / uptime label refresh.
    /// Drives ChainStatus.LastVerifiedAtLabel, LastRefreshedAtLabel, and
    /// UptimeLabel notifications.</summary>
    private static readonly TimeSpan RelativeTimeTickInterval = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> TrafficTierNames = new(StringComparer.Ordinal) {
        "traffic_raw",
        "traffic_buckets_10s",
        "traffic_buckets_1m",
        "traffic_buckets_10m",
        "traffic_buckets_1h",
    };

    private const string EventLogTableName = "event_log";

    private readonly IDaemonClient _daemonClient;
    private readonly IDispatcher _dispatcher;
    private readonly IShellOpener _shellOpener;
    private readonly IClipboardWriter _clipboardWriter;
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _activationCts;
    private CancellationTokenSource? _verifyStatusCts;
    private ITimer? _relativeTimeTicker;
    private bool _disposed;

    /// <summary>In-flight (or completed) activation task. Cold-start race
    /// pattern: concurrent callers hand back the same Task.</summary>
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
    [NotifyPropertyChangedFor(nameof(LanDeviceCountLabel))]
    [NotifyPropertyChangedFor(nameof(UptimeLabel))]
    [NotifyPropertyChangedFor(nameof(WatchingSinceLabel))]
    private GetStorageStatsResponse? _storageStats;

    /// <summary>Traffic-tier rows (the 5 rollup-cascade tables) in logical
    /// order — finest tier first (traffic_raw → 10s → 1m → 10m → 1h).</summary>
    public ObservableCollection<TableStatsRow> TrafficTables { get; } = new();

    /// <summary>All non-traffic-tier tables (audit chain, registry, etc.)
    /// in alphabetical order by metadata sort key.</summary>
    public ObservableCollection<TableStatsRow> FlatTables { get; } = new();

    public ChainStatusRow ChainStatus { get; }
    public AboutInfo AboutInfo { get; }

    /// <summary>Cyan-tinted share of the stacked total bar — sum of all
    /// traffic-tier rows divided by grand total. Falls back to 0 when the
    /// database has no rows.</summary>
    [ObservableProperty]
    private double _trafficTierRatio;

    /// <summary>Violet/purple-tinted share — event_log row count divided
    /// by grand total. Visually separates the security-significant audit
    /// chain from miscellaneous state tables.</summary>
    [ObservableProperty]
    private double _auditChainRatio;

    /// <summary>Neutral-tinted share — everything else (registry, rules,
    /// DNS cache, etc.).</summary>
    [ObservableProperty]
    private double _otherTablesRatio;

    /// <summary>Five proportional ratios for the cascade sparkline,
    /// finest→coarsest. Each is the tier's row count divided by the max
    /// of the five so the largest tier's bar fills the full height.</summary>
    [ObservableProperty]
    private IReadOnlyList<double> _cascadeRatios = Array.Empty<double>();

    [ObservableProperty]
    private DateTimeOffset? _lastRefreshedAt;

    public string DatabasePath => StorageStats?.DatabasePath ?? string.Empty;
    public string DatabaseBytesTotalFormatted =>
        StorageStats is null ? string.Empty : FormatBytes(StorageStats.DatabaseBytesTotal);
    public bool HasOpenableDataFolder => !string.IsNullOrEmpty(DatabasePath);

    /// <summary>"4h 12m" / "3d 14h" / "12m". Recomputed on every ticker
    /// notification + on each StorageStats arrival.</summary>
    public string UptimeLabel => StorageStats is null
        ? string.Empty
        : FormatUptime(
            FromUnixNs(StorageStats.DaemonStartedUnixNs),
            _timeProvider.GetUtcNow());

    /// <summary>"since 2026-04-10 (45 days)" — derived from the earliest
    /// row in the audit chain. Empty when the chain is empty (fresh
    /// install).</summary>
    public string WatchingSinceLabel {
        get {
            if (StorageStats is null || StorageStats.ChainFirstEventUnixNs == 0) return string.Empty;
            var firstEvent = FromUnixNs(StorageStats.ChainFirstEventUnixNs);
            var days = (int)Math.Max(0, (_timeProvider.GetUtcNow() - firstEvent).TotalDays);
            var dateText = firstEvent.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return days switch {
                0 => $"since {dateText} (today)",
                1 => $"since {dateText} (yesterday)",
                _ => $"since {dateText} ({days} days)",
            };
        }
    }

    public string LanDeviceCountLabel {
        get {
            if (StorageStats is null) return string.Empty;
            var count = StorageStats.LanDeviceCount;
            return count == 1 ? "1 LAN device tracked" : $"{count} LAN devices tracked";
        }
    }

    /// <summary>"Last refreshed 30s ago" / "just now" / null when never
    /// refreshed. Refreshed every ticker tick so the label stays live
    /// without re-fetching.</summary>
    public string LastRefreshedAtLabel => LastRefreshedAt is null
        ? string.Empty
        : $"Last refreshed {Converters.RelativeTimeAgoConverter.Format(LastRefreshedAt.Value, _timeProvider.GetUtcNow())}";

    public bool ShowLoadingState => IsLoading && StorageStats is null;
    public bool ShowPopulatedState => !IsLoading && StorageStats is not null;

    public SettingsTabViewModel(
        IDaemonClient daemonClient,
        IDispatcher dispatcher,
        IShellOpener shellOpener,
        IClipboardWriter clipboardWriter,
        TimeProvider timeProvider
    ) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(shellOpener);
        ArgumentNullException.ThrowIfNull(clipboardWriter);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _daemonClient = daemonClient;
        _dispatcher = dispatcher;
        _shellOpener = shellOpener;
        _clipboardWriter = clipboardWriter;
        _timeProvider = timeProvider;

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

        // Bucket tables into traffic-tier vs flat. The proto returns them
        // alphabetically; we re-group and re-sort by the row's SortKey so
        // traffic_raw → traffic_buckets_10s → ... → 1h appears in cascade
        // order rather than dictionary order.
        var trafficProtos = new List<TableStats>();
        var flatProtos = new List<TableStats>();
        long trafficSum = 0;
        long flatSum = 0;
        foreach (var table in response.Tables) {
            if (TrafficTierNames.Contains(table.Name)) {
                trafficProtos.Add(table);
                trafficSum += table.RowCount;
            } else {
                flatProtos.Add(table);
                flatSum += table.RowCount;
            }
        }
        var trafficMax = trafficProtos.Count > 0 ? trafficProtos.Max(t => t.RowCount) : 0;
        var flatMax = flatProtos.Count > 0 ? flatProtos.Max(t => t.RowCount) : 0;

        TrafficTables.Clear();
        foreach (var proto in trafficProtos
            .Select(p => TableStatsRow.FromProto(p, trafficMax))
            .OrderBy(r => r.SortKey)) {
            TrafficTables.Add(proto);
        }
        FlatTables.Clear();
        foreach (var proto in flatProtos
            .Select(p => TableStatsRow.FromProto(p, flatMax))
            .OrderBy(r => r.SortKey)) {
            FlatTables.Add(proto);
        }

        // Stacked total bar — three slices: traffic-tier sum, audit chain
        // (event_log specifically — the security-significant row), and
        // everything else. Sum to 1.0 when grand total > 0; all-zero
        // otherwise (degrades to empty bar, not a divide-by-zero crash).
        var auditRows = response.Tables.FirstOrDefault(t => t.Name == EventLogTableName)?.RowCount ?? 0;
        var grandTotal = trafficSum + flatSum;
        if (grandTotal > 0) {
            TrafficTierRatio = (double)trafficSum / grandTotal;
            AuditChainRatio = (double)auditRows / grandTotal;
            OtherTablesRatio = (double)(flatSum - auditRows) / grandTotal;
        } else {
            TrafficTierRatio = 0;
            AuditChainRatio = 0;
            OtherTablesRatio = 0;
        }

        // Cascade sparkline — one height ratio per traffic tier, in
        // cascade order. Empty when no traffic tiers exist (defensive).
        CascadeRatios = trafficProtos.Count > 0
            ? trafficProtos
                .OrderBy(p => TableStatsRow.FromProto(p, trafficMax).SortKey)
                .Select(p => trafficMax > 0 ? (double)p.RowCount / trafficMax : 0.0)
                .ToArray()
            : Array.Empty<double>();

        ChainStatus.UpdateFromProto(
            response.HasChainStatus ? response.ChainStatus : null,
            _timeProvider);

        LastRefreshedAt = _timeProvider.GetUtcNow();
        OnPropertyChanged(nameof(LastRefreshedAtLabel));
    }

    [RelayCommand]
    private Task RefreshStorageStats() => LoadStorageStatsAsync(CancellationToken.None, isInitialLoad: false);

    [RelayCommand]
    private async Task VerifyChain() {
        if (IsVerifyingChain) return;
        IsVerifyingChain = true;
        ClearVerifyStatus();
        try {
            var response = await _daemonClient.VerifyChainAsync(
                new VerifyChainRequest(), CancellationToken.None);

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

    [RelayCommand]
    private void OpenDataFolder() {
        if (string.IsNullOrEmpty(DatabasePath)) return;
        try {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (string.IsNullOrEmpty(directory)) return;
            _shellOpener.Open(directory);
        } catch (Exception ex) {
            SetVerifyStatus($"Failed to open data folder: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// User clicked a hyperlink in the About section (project URL or an
    /// attribution URL). Hands the URL off to the OS's default browser via
    /// <see cref="IShellOpener"/>. Failures (unlikely — the browser is the
    /// shell's default handler for http/https) surface via the verify-status
    /// banner since the About section has no banner of its own.
    /// </summary>
    [RelayCommand]
    private void OpenUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return;
        try {
            _shellOpener.Open(url);
        } catch (Exception ex) {
            SetVerifyStatus($"Failed to open link: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// User clicked the copy-icon button next to a copyable value (the
    /// data-folder path or the project URL). Writes the value to the OS
    /// clipboard via <see cref="IClipboardWriter"/>. Shows a brief
    /// confirmation in the verify-status banner so the user has feedback
    /// that the copy succeeded.
    /// </summary>
    [RelayCommand]
    private async Task CopyToClipboard(string? text) {
        if (string.IsNullOrEmpty(text)) return;
        try {
            await _clipboardWriter.WriteTextAsync(text, CancellationToken.None);
            SetVerifyStatus("Copied to clipboard", isError: false);
        } catch (Exception ex) {
            SetVerifyStatus($"Failed to copy: {ex.Message}", isError: true);
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
            // Superseded by a new SetVerifyStatus.
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
            _ => _dispatcher.Post(RefreshTickerLabels),
            state: null,
            dueTime: RelativeTimeTickInterval,
            period: RelativeTimeTickInterval);
    }

    private void RefreshTickerLabels() {
        ChainStatus.RefreshRelativeLabel(_timeProvider);
        OnPropertyChanged(nameof(LastRefreshedAtLabel));
        OnPropertyChanged(nameof(UptimeLabel));
        OnPropertyChanged(nameof(WatchingSinceLabel));
    }

    private static DateTimeOffset FromUnixNs(long unixNs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixNs / 1_000_000L);

    /// <summary>
    /// Human-readable uptime: "12m" under an hour, "4h 12m" under a day,
    /// "3d 14h" under a week, "9d" otherwise. Drops trailing zero
    /// sub-units ("4h" not "4h 0m"; "1d" not "1d 0h").
    /// </summary>
    internal static string FormatUptime(DateTimeOffset startedAt, DateTimeOffset now) {
        var elapsed = now - startedAt;
        if (elapsed.TotalSeconds < 0) return "0s";
        if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed.TotalDays < 1) {
            var hours = (int)elapsed.TotalHours;
            var minutes = (int)(elapsed.TotalMinutes - hours * 60);
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }
        if (elapsed.TotalDays < 7) {
            var days = (int)elapsed.TotalDays;
            var hours = (int)(elapsed.TotalHours - days * 24);
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }
        return $"{(int)elapsed.TotalDays}d";
    }

    /// <summary>
    /// Formats a byte count as "X.X UNIT" with binary (1024-based) units
    /// up to GB.
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
