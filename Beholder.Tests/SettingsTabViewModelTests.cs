using Beholder.Protocol.Local;
using Beholder.Tests.TestDoubles;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public class SettingsTabViewModelTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);

    private static (SettingsTabViewModel Vm, FakeDaemonClient Client, FakeShellOpener Shell, FakeClipboardWriter Clipboard, FakeTimeProvider Time)
    CreateVm(GetStorageStatsResponse? canned = null) {
        var client = new FakeDaemonClient();
        if (canned is not null) client.GetStorageStatsResponder = _ => canned;
        var shell = new FakeShellOpener();
        var clipboard = new FakeClipboardWriter();
        var time = new FakeTimeProvider(FixedTimestamp);
        var vm = new SettingsTabViewModel(
            client, new SyncDispatcher(), shell, clipboard,
            new FakeFilePicker(), new FakeFileWriter(), new FakeUiPreferencesStore(), time,
            new TotalsExclusionUiState());
        return (vm, client, shell, clipboard, time);
    }

    private static GetStorageStatsResponse MakeStats(
        string path = @"C:\daemon\data\beholder.db",
        long totalBytes = 1024L * 1024 * 42,
        bool includeChainStatus = false,
        bool chainValid = true,
        long rowsVerified = 1000,
        long? chainFirstEventUnixNs = null,
        long? daemonStartedUnixNs = null,
        long lanDeviceCount = 0
    ) {
        var response = new GetStorageStatsResponse {
            DatabasePath = path,
            DatabaseBytesTotal = totalBytes,
            HasChainStatus = includeChainStatus,
            ChainFirstEventUnixNs = chainFirstEventUnixNs ?? 0,
            DaemonStartedUnixNs = daemonStartedUnixNs ?? FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            LanDeviceCount = lanDeviceCount,
        };
        response.Tables.Add(new TableStats { Name = "event_log", RowCount = 50 });
        response.Tables.Add(new TableStats { Name = "traffic_raw", RowCount = 10_000 });
        response.Tables.Add(new TableStats { Name = "traffic_buckets_10s", RowCount = 5_000 });
        response.Tables.Add(new TableStats { Name = "traffic_buckets_1m", RowCount = 2_000 });
        response.Tables.Add(new TableStats { Name = "traffic_buckets_10m", RowCount = 800 });
        response.Tables.Add(new TableStats { Name = "traffic_buckets_1h", RowCount = 100 });
        response.Tables.Add(new TableStats { Name = "lan_device", RowCount = lanDeviceCount });
        if (includeChainStatus) {
            response.ChainStatus = new ChainStatus {
                LastVerifiedUnixNs = FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
                IsValid = chainValid,
                RowsVerified = rowsVerified,
                FailedAtSeq = chainValid ? 0 : 42,
                ErrorMessage = chainValid ? "" : "hash mismatch",
            };
        }
        return response;
    }

    // ---- Constructor guards ----

    [Fact]
    public void Constructor_NullDaemonClient_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            null!, new SyncDispatcher(), new FakeShellOpener(), new FakeClipboardWriter(),
            new FakeFilePicker(), new FakeFileWriter(), new FakeUiPreferencesStore(), new FakeTimeProvider(FixedTimestamp), new TotalsExclusionUiState()));

    [Fact]
    public void Constructor_NullDispatcher_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), null!, new FakeShellOpener(), new FakeClipboardWriter(),
            new FakeFilePicker(), new FakeFileWriter(), new FakeUiPreferencesStore(), new FakeTimeProvider(FixedTimestamp), new TotalsExclusionUiState()));

    [Fact]
    public void Constructor_NullShellOpener_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), new SyncDispatcher(), null!, new FakeClipboardWriter(),
            new FakeFilePicker(), new FakeFileWriter(), new FakeUiPreferencesStore(), new FakeTimeProvider(FixedTimestamp), new TotalsExclusionUiState()));

    [Fact]
    public void Constructor_NullClipboardWriter_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), new SyncDispatcher(), new FakeShellOpener(), null!,
            new FakeFilePicker(), new FakeFileWriter(), new FakeUiPreferencesStore(), new FakeTimeProvider(FixedTimestamp), new TotalsExclusionUiState()));

    [Fact]
    public void Constructor_NullFilePicker_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), new SyncDispatcher(), new FakeShellOpener(),
            new FakeClipboardWriter(), null!, new FakeFileWriter(), new FakeUiPreferencesStore(), new FakeTimeProvider(FixedTimestamp), new TotalsExclusionUiState()));

    [Fact]
    public void Constructor_NullFileWriter_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), new SyncDispatcher(), new FakeShellOpener(),
            new FakeClipboardWriter(), new FakeFilePicker(), null!, new FakeUiPreferencesStore(), new FakeTimeProvider(FixedTimestamp), new TotalsExclusionUiState()));

    [Fact]
    public void Constructor_NullUiPreferencesStore_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), new SyncDispatcher(), new FakeShellOpener(),
            new FakeClipboardWriter(), new FakeFilePicker(), new FakeFileWriter(), null!, new FakeTimeProvider(FixedTimestamp), new TotalsExclusionUiState()));

    [Fact]
    public void Constructor_NullTimeProvider_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), new SyncDispatcher(), new FakeShellOpener(),
            new FakeClipboardWriter(), new FakeFilePicker(), new FakeFileWriter(), new FakeUiPreferencesStore(), null!,
            new TotalsExclusionUiState()));

    [Fact]
    public void ToggleCloseToTray_FlipsAndPersistsToTheStore() {
        var store = new FakeUiPreferencesStore();   // defaults CloseToTray = true
        var vm = new SettingsTabViewModel(
            new FakeDaemonClient(), new SyncDispatcher(), new FakeShellOpener(), new FakeClipboardWriter(),
            new FakeFilePicker(), new FakeFileWriter(), store, new FakeTimeProvider(FixedTimestamp),
            new TotalsExclusionUiState());

        Assert.True(vm.Application.CloseToTray);    // seeded from the store on construction

        vm.ToggleCloseToTrayCommand.Execute(null);

        Assert.False(vm.Application.CloseToTray);    // flipped in the row
        Assert.False(store.Current.CloseToTray);     // persisted to the store
    }

    [Fact]
    public void InitialState_NoStorageStats_NoTables_DefaultPlaceholders() {
        var (vm, _, _, _, _) = CreateVm();
        Assert.Empty(vm.TrafficTables);
        Assert.Empty(vm.FlatTables);
        Assert.Null(vm.StorageStats);
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasError);
        Assert.NotNull(vm.AboutInfo);
        Assert.Equal(ChainStatusRow.NeverVerifiedLabel, vm.ChainStatus.LastVerifiedAtLabel);
    }

    // ---- ActivateAsync ----

    [Fact]
    public async Task ActivateAsync_LoadsStorageStats_AndPopulatesTables() {
        var (vm, client, _, _, _) = CreateVm(MakeStats(includeChainStatus: true));

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsLoading);
        Assert.NotNull(vm.StorageStats);
        Assert.Equal(5, vm.TrafficTables.Count);     // 5 traffic tiers
        Assert.Equal(2, vm.FlatTables.Count);        // event_log + lan_device
        Assert.True(vm.ChainStatus.HasResult);
        Assert.True(vm.ChainStatus.IsValid);
        Assert.Single(client.GetStorageStatsCalls);
    }

    [Fact]
    public async Task ActivateAsync_TrafficTables_OrderedByCascadeNotAlphabetically() {
        // The proto returns tables alphabetically: traffic_buckets_10m,
        // traffic_buckets_10s, traffic_buckets_1h, traffic_buckets_1m,
        // traffic_raw. The VM must re-sort them in cascade order:
        // raw → 10s → 1m → 10m → 1h.
        var (vm, _, _, _, _) = CreateVm(MakeStats());

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var names = vm.TrafficTables.Select(r => r.Name).ToArray();
        Assert.Equal(
            new[] { "traffic_raw", "traffic_buckets_10s", "traffic_buckets_1m", "traffic_buckets_10m", "traffic_buckets_1h" },
            names);
    }

    [Fact]
    public async Task ActivateAsync_RatioComputation_TrafficTierAndAuditChainShares() {
        // Default MakeStats() rows: traffic tiers sum to 17_900, event_log
        // is 50, lan_device is 0. Grand total 17_950. TrafficTierRatio is
        // 17_900/17_950 ≈ 0.997; AuditChainRatio is 50/17_950 ≈ 0.003;
        // OtherTablesRatio is 0 (lan_device has 0 rows).
        var (vm, _, _, _, _) = CreateVm(MakeStats());

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.InRange(vm.TrafficTierRatio + vm.AuditChainRatio + vm.OtherTablesRatio, 0.99, 1.01);
        Assert.True(vm.TrafficTierRatio > vm.AuditChainRatio);
        Assert.True(vm.AuditChainRatio > 0);
    }

    [Fact]
    public async Task ActivateAsync_CascadeRatios_NormalisedToTrafficMax() {
        // traffic_raw=10000 is the max → its ratio is 1.0; others scaled.
        var (vm, _, _, _, _) = CreateVm(MakeStats());

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, vm.CascadeRatios.Count);
        Assert.Equal(1.0, vm.CascadeRatios[0]);       // traffic_raw (max)
        Assert.Equal(0.5, vm.CascadeRatios[1]);       // _10s = 5000 / 10000
        Assert.Equal(0.2, vm.CascadeRatios[2]);       // _1m  = 2000 / 10000
        Assert.Equal(0.08, vm.CascadeRatios[3]);      // _10m =  800 / 10000
        Assert.Equal(0.01, vm.CascadeRatios[4]);      // _1h  =  100 / 10000
    }

    [Fact]
    public async Task ActivateAsync_WatchingSinceLabel_FormatsRelativeToFirstEvent() {
        // Chain first event 5 days before the fixed timestamp.
        var fiveDaysAgo = FixedTimestamp.AddDays(-5);
        var stats = MakeStats(chainFirstEventUnixNs: fiveDaysAgo.ToUnixTimeMilliseconds() * 1_000_000L);
        var (vm, _, _, _, _) = CreateVm(stats);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Contains("5 days", vm.WatchingSinceLabel);
        Assert.Contains(fiveDaysAgo.LocalDateTime.ToString("yyyy-MM-dd"), vm.WatchingSinceLabel);
    }

    [Fact]
    public async Task ActivateAsync_WatchingSinceLabel_EmptyWhenChainEmpty() {
        var (vm, _, _, _, _) = CreateVm(MakeStats(chainFirstEventUnixNs: 0));
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(vm.WatchingSinceLabel);
    }

    [Fact]
    public async Task ActivateAsync_UptimeLabel_FormatsCorrectly() {
        // Daemon started 4 hours and 12 minutes before the fixed timestamp.
        var startedAt = FixedTimestamp - TimeSpan.FromMinutes(252);
        var stats = MakeStats(daemonStartedUnixNs: startedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        var (vm, _, _, _, _) = CreateVm(stats);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("4h 12m", vm.UptimeLabel);
    }

    [Fact]
    public async Task ActivateAsync_LanDeviceCountLabel_HandlesSingular() {
        var (vm, _, _, _, _) = CreateVm(MakeStats(lanDeviceCount: 1));
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1 LAN device tracked", vm.LanDeviceCountLabel);
    }

    [Fact]
    public async Task ActivateAsync_LanDeviceCountLabel_HandlesPlural() {
        var (vm, _, _, _, _) = CreateVm(MakeStats(lanDeviceCount: 12));
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Equal("12 LAN devices tracked", vm.LanDeviceCountLabel);
    }

    [Fact]
    public async Task ActivateAsync_LastRefreshedAt_SetOnLoad() {
        var (vm, _, _, _, _) = CreateVm(MakeStats());
        Assert.Null(vm.LastRefreshedAt);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(FixedTimestamp, vm.LastRefreshedAt);
        Assert.Contains("Last refreshed", vm.LastRefreshedAtLabel);
    }

    [Fact]
    public async Task ActivateAsync_RpcThrowsRpcException_SetsErrorBanner() {
        var (vm, client, _, _, _) = CreateVm();
        client.GetStorageStatsException = new RpcException(
            new Status(StatusCode.Unavailable, "daemon offline"));

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsLoading);
        Assert.True(vm.HasError);
        Assert.Contains("daemon offline", vm.ErrorMessage);
        Assert.Null(vm.StorageStats);
    }

    [Fact]
    public async Task ActivateAsync_ConcurrentCallers_ShareSameTask() {
        var (vm, client, _, _, _) = CreateVm();
        var tcs = new TaskCompletionSource<GetStorageStatsResponse>();
        client.AsyncGetStorageStatsResponder = (_, _) => tcs.Task;

        var task1 = vm.ActivateAsync(TestContext.Current.CancellationToken);
        var task2 = vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Same(task1, task2);
        tcs.SetResult(MakeStats());
        await task1;
        Assert.Single(client.GetStorageStatsCalls);
    }

    // ---- Auto-recovery on daemon reconnect ----

    [Fact]
    public async Task ActivateAsync_AfterError_RetriesWhenCalledAgain() {
        // The cold-start-race idempotency must not prevent retry: a
        // failed initial load clears _activationTask so subsequent
        // ActivateAsync calls (e.g., user switches tabs and comes back)
        // re-attempt the load instead of handing back the cached failure.
        var (vm, client, _, _, _) = CreateVm();
        client.GetStorageStatsException = new RpcException(
            new Status(StatusCode.Unavailable, "daemon offline"));

        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.HasError);
        Assert.Single(client.GetStorageStatsCalls);

        // Clear the failure mode — daemon is now responsive.
        client.GetStorageStatsException = null;
        client.GetStorageStatsResponder = _ => MakeStats(includeChainStatus: true);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.HasError);
        Assert.NotNull(vm.StorageStats);
        Assert.Equal(2, client.GetStorageStatsCalls.Count);
    }

    [Fact]
    public async Task DaemonReconnect_AfterError_AutoRetriesLoad() {
        // The realistic scenario: UI starts before the daemon, user opens
        // Settings while disconnected, gets the "Not connected" error.
        // When the daemon comes online, the StateChanged handler fires a
        // fresh load — no user action required.
        var (vm, client, _, _, _) = CreateVm();
        client.GetStorageStatsException = new RpcException(
            new Status(StatusCode.Unavailable, "daemon offline"));

        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.HasError);

        client.GetStorageStatsException = null;
        client.GetStorageStatsResponder = _ => MakeStats(includeChainStatus: true);
        client.SimulateConnected();

        // SyncDispatcher + Task.FromResult-shaped responder means the
        // entire auto-retry chain runs synchronously inside
        // SimulateConnected.
        Assert.False(vm.HasError);
        Assert.NotNull(vm.StorageStats);
        Assert.Equal(2, client.GetStorageStatsCalls.Count);
    }

    [Fact]
    public void DaemonReconnect_BeforeActivation_DoesNotPreemptivelyLoad() {
        // If the user has never opened the Settings tab, the daemon
        // coming online must not fire an RPC for data nobody is looking
        // at — the handler short-circuits on `_hasActivatedOnce`.
        var (_, client, _, _, _) = CreateVm();

        client.SimulateConnected();

        Assert.Empty(client.GetStorageStatsCalls);
    }

    [Fact]
    public async Task DaemonReconnect_WhenHealthy_DoesNotReload() {
        // Already-healthy tab: don't refetch on every daemon reconnect.
        // The user can click REFRESH manually if they want fresh numbers.
        var (vm, client, _, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Single(client.GetStorageStatsCalls);

        client.SimulateConnected();

        Assert.Single(client.GetStorageStatsCalls);
    }

    [Fact]
    public void Dispose_UnsubscribesFromStateChanged() {
        // After Dispose, daemon reconnects must not trigger any side
        // effects — guards against a torn-down VM observing late events
        // and re-firing RPCs.
        var (vm, client, _, _, _) = CreateVm();
        // Force _hasActivatedOnce + an error so the handler would otherwise
        // act on SimulateConnected.
        client.GetStorageStatsException = new RpcException(
            new Status(StatusCode.Unavailable, "down"));
        _ = vm.ActivateAsync(CancellationToken.None);
        var callsBeforeDispose = client.GetStorageStatsCalls.Count;

        vm.Dispose();
        client.GetStorageStatsException = null;
        client.GetStorageStatsResponder = _ => MakeStats();
        client.SimulateConnected();

        Assert.Equal(callsBeforeDispose, client.GetStorageStatsCalls.Count);
    }

    // ---- RefreshStorageStatsCommand ----

    [Fact]
    public async Task RefreshCommand_ReFetches_AndRebuildsTables() {
        var (vm, client, _, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        client.GetStorageStatsResponder = _ => {
            var r = MakeStats();
            r.Tables.Clear();
            r.Tables.Add(new TableStats { Name = "lan_device", RowCount = 4 });
            return r;
        };
        await vm.RefreshStorageStatsCommand.ExecuteAsync(null);

        Assert.Empty(vm.TrafficTables);
        Assert.Single(vm.FlatTables);
        Assert.Equal("lan_device", vm.FlatTables[0].Name);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsRefreshing);
        Assert.Equal(2, client.GetStorageStatsCalls.Count);
    }

    [Fact]
    public async Task RefreshCommand_RpcFails_SetsErrorBanner_WithoutClearingStats() {
        var (vm, client, _, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        client.GetStorageStatsException = new RpcException(
            new Status(StatusCode.Internal, "disk full"));
        await vm.RefreshStorageStatsCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("disk full", vm.ErrorMessage);
        Assert.NotNull(vm.StorageStats);
        Assert.NotEmpty(vm.TrafficTables);
    }

    // ---- VerifyChainCommand ----

    [Fact]
    public async Task VerifyChainCommand_Success_UpdatesChainStatusRow() {
        var (vm, client, _, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.VerifyChainResponder = _ => new VerifyChainResponse {
            IsValid = true, RowsVerified = 1247,
        };

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.True(vm.ChainStatus.HasResult);
        Assert.True(vm.ChainStatus.IsValid);
        Assert.Equal(1247, vm.ChainStatus.RowsVerified);
        Assert.Equal("VALID", vm.ChainStatus.StatusPillLabel);
        Assert.Equal("SeveritySuccess", vm.ChainStatus.StatusPillBrushKey);
        Assert.True(vm.HasVerifyStatus);
        Assert.False(vm.VerifyStatusIsError);
    }

    [Fact]
    public async Task VerifyChainCommand_Invalid_ShowsErrorBanner() {
        var (vm, client, _, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.VerifyChainResponder = _ => new VerifyChainResponse {
            IsValid = false, RowsVerified = 99, FailedAtSeq = 50,
            ErrorMessage = "hash mismatch",
        };

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.True(vm.HasVerifyStatus);
        Assert.True(vm.VerifyStatusIsError);
        Assert.Contains("seq 50", vm.VerifyStatusMessage);
        Assert.False(vm.ChainStatus.IsValid);
        Assert.Equal(50, vm.ChainStatus.FailedAtSeq);
        Assert.Equal("INVALID", vm.ChainStatus.StatusPillLabel);
        Assert.Equal("SeverityDanger", vm.ChainStatus.StatusPillBrushKey);
    }

    [Fact]
    public async Task VerifyChainCommand_RpcThrows_ShowsErrorBanner() {
        var (vm, client, _, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.VerifyChainException = new RpcException(
            new Status(StatusCode.Internal, "verify exploded"));

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.True(vm.HasVerifyStatus);
        Assert.True(vm.VerifyStatusIsError);
        Assert.Contains("verify exploded", vm.VerifyStatusMessage);
    }

    // ---- ExportChainCommand ----

    private static (SettingsTabViewModel Vm, FakeDaemonClient Client, FakeFilePicker Picker, FakeFileWriter Writer)
    CreateVmForExport() {
        var client = new FakeDaemonClient();
        var picker = new FakeFilePicker();
        var writer = new FakeFileWriter();
        var vm = new SettingsTabViewModel(
            client, new SyncDispatcher(), new FakeShellOpener(), new FakeClipboardWriter(),
            picker, writer, new FakeUiPreferencesStore(), new FakeTimeProvider(FixedTimestamp),
            new TotalsExclusionUiState());
        return (vm, client, picker, writer);
    }

    [Fact]
    public async Task ExportChainCommand_Success_WritesBytesToPickedPathAndShowsBanner() {
        var (vm, client, picker, writer) = CreateVmForExport();
        picker.SavePickedPath = @"C:\exports\chain.json";
        var signed = new byte[] { 0x7B, 0x7D };   // "{}"
        client.ExportChainResponder = _ => new ExportChainResponse {
            SignedExport = Google.Protobuf.ByteString.CopyFrom(signed), EventCount = 42,
        };

        await vm.ExportChainCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\exports\chain.json", writer.LastPath);
        Assert.Equal(signed, writer.LastBytes);
        Assert.True(vm.HasVerifyStatus);
        Assert.False(vm.VerifyStatusIsError);
        Assert.Contains("42", vm.VerifyStatusMessage);
        Assert.Contains(@"C:\exports\chain.json", vm.VerifyStatusMessage);
    }

    [Fact]
    public async Task ExportChainCommand_RequestsFullChainWithSuggestedFileName() {
        var (vm, client, picker, _) = CreateVmForExport();
        picker.SavePickedPath = @"C:\exports\chain.json";

        await vm.ExportChainCommand.ExecuteAsync(null);

        Assert.Equal("beholder-chain-export.json", picker.LastSuggestedFileName);
        var request = Assert.Single(client.ExportChainCalls);
        Assert.Equal(0, request.FromSeq);
        Assert.Equal(0, request.ToSeq);   // v1 button exports the whole chain
    }

    [Fact]
    public async Task ExportChainCommand_UserCancels_NoRpcNoWriteNoBanner() {
        var (vm, client, picker, writer) = CreateVmForExport();
        picker.SavePickedPath = null;   // user dismissed the save dialog

        await vm.ExportChainCommand.ExecuteAsync(null);

        Assert.Empty(client.ExportChainCalls);
        Assert.Equal(0, writer.CallCount);
        Assert.False(vm.HasVerifyStatus);
    }

    [Fact]
    public async Task ExportChainCommand_RpcThrows_ShowsErrorBannerAndDoesNotWrite() {
        var (vm, client, picker, writer) = CreateVmForExport();
        picker.SavePickedPath = @"C:\exports\chain.json";
        client.ExportChainException = new RpcException(
            new Status(StatusCode.Internal, "export exploded"));

        await vm.ExportChainCommand.ExecuteAsync(null);

        Assert.True(vm.HasVerifyStatus);
        Assert.True(vm.VerifyStatusIsError);
        Assert.Contains("export exploded", vm.VerifyStatusMessage);
        Assert.Equal(0, writer.CallCount);
    }

    [Fact]
    public async Task ExportChainCommand_FileWriteThrows_ShowsErrorBanner() {
        var (vm, client, picker, writer) = CreateVmForExport();
        picker.SavePickedPath = @"C:\exports\chain.json";
        client.ExportChainResponder = _ => new ExportChainResponse {
            SignedExport = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x01 }), EventCount = 1,
        };
        writer.Exception = new IOException("disk full");

        await vm.ExportChainCommand.ExecuteAsync(null);

        Assert.True(vm.HasVerifyStatus);
        Assert.True(vm.VerifyStatusIsError);
        Assert.Contains("disk full", vm.VerifyStatusMessage);
    }

    // ---- OpenDataFolderCommand ----

    [Fact]
    public async Task OpenDataFolderCommand_CallsShellOpener_WithParentDirectory() {
        var (vm, _, shell, _, _) = CreateVm(MakeStats(path: @"C:\daemon\data\beholder.db"));
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        vm.OpenDataFolderCommand.Execute(null);

        var opened = Assert.Single(shell.OpenedTargets);
        Assert.Equal(@"C:\daemon\data", opened);
    }

    [Fact]
    public void OpenDataFolderCommand_NoStatsLoaded_DoesNothing() {
        var (vm, _, shell, _, _) = CreateVm();
        vm.OpenDataFolderCommand.Execute(null);
        Assert.Empty(shell.OpenedTargets);
    }

    [Fact]
    public async Task OpenDataFolderCommand_OpenerThrows_SurfacesViaVerifyBanner() {
        var (vm, _, shell, _, _) = CreateVm(MakeStats(path: @"C:\nonexistent\beholder.db"));
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        shell.Exception = new InvalidOperationException("shell failed");

        vm.OpenDataFolderCommand.Execute(null);

        Assert.True(vm.HasVerifyStatus);
        Assert.True(vm.VerifyStatusIsError);
        Assert.Contains("shell failed", vm.VerifyStatusMessage);
    }

    // ---- OpenUrlCommand ----

    [Fact]
    public void OpenUrlCommand_HappyPath_CallsShellOpener() {
        var (vm, _, shell, _, _) = CreateVm();

        vm.OpenUrlCommand.Execute("https://github.com/Vane65855/Beholder");

        var target = Assert.Single(shell.OpenedTargets);
        Assert.Equal("https://github.com/Vane65855/Beholder", target);
    }

    [Fact]
    public void OpenUrlCommand_NullOrEmpty_IsNoop() {
        var (vm, _, shell, _, _) = CreateVm();
        vm.OpenUrlCommand.Execute(null);
        vm.OpenUrlCommand.Execute("");
        vm.OpenUrlCommand.Execute("   ");
        Assert.Empty(shell.OpenedTargets);
    }

    [Fact]
    public void OpenUrlCommand_OpenerThrows_SurfacesViaVerifyBanner() {
        var (vm, _, shell, _, _) = CreateVm();
        shell.Exception = new InvalidOperationException("browser missing");

        vm.OpenUrlCommand.Execute("https://example.com");

        Assert.True(vm.HasVerifyStatus);
        Assert.True(vm.VerifyStatusIsError);
        Assert.Contains("browser missing", vm.VerifyStatusMessage);
    }

    // ---- CopyToClipboardCommand ----

    [Fact]
    public async Task CopyToClipboardCommand_HappyPath_WritesAndConfirms() {
        var (vm, _, _, clipboard, _) = CreateVm();

        await vm.CopyToClipboardCommand.ExecuteAsync(@"C:\daemon\data\beholder.db");

        var written = Assert.Single(clipboard.Writes);
        Assert.Equal(@"C:\daemon\data\beholder.db", written);
        Assert.True(vm.HasVerifyStatus);
        Assert.False(vm.VerifyStatusIsError);
        Assert.Equal("Copied to clipboard", vm.VerifyStatusMessage);
    }

    [Fact]
    public async Task CopyToClipboardCommand_NullOrEmpty_IsNoop() {
        var (vm, _, _, clipboard, _) = CreateVm();
        await vm.CopyToClipboardCommand.ExecuteAsync(null);
        await vm.CopyToClipboardCommand.ExecuteAsync("");
        Assert.Empty(clipboard.Writes);
        Assert.False(vm.HasVerifyStatus);
    }

    [Fact]
    public async Task CopyToClipboardCommand_WriterThrows_SurfacesViaVerifyBanner() {
        var (vm, _, _, clipboard, _) = CreateVm();
        clipboard.Exception = new InvalidOperationException("clipboard busy");

        await vm.CopyToClipboardCommand.ExecuteAsync("test");

        Assert.True(vm.HasVerifyStatus);
        Assert.True(vm.VerifyStatusIsError);
        Assert.Contains("clipboard busy", vm.VerifyStatusMessage);
    }

    // ---- Dismiss commands ----

    [Fact]
    public async Task DismissErrorCommand_ClearsHasError() {
        var (vm, client, _, _, _) = CreateVm();
        client.GetStorageStatsException = new RpcException(
            new Status(StatusCode.Unavailable, "down"));
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        vm.DismissErrorCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Empty(vm.ErrorMessage);
    }

    // ---- FormatBytes helper ----

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1024L * 1024, "1.0 MB")]
    [InlineData(1024L * 1024 * 142 + 1024L * 300, "142.3 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    public void FormatBytes_FormatsHumanReadable(long bytes, string expected) =>
        Assert.Equal(expected, SettingsTabViewModel.FormatBytes(bytes));

    // ---- FormatUptime helper ----

    [Theory]
    [InlineData(45.0, "45s")]
    [InlineData(60.0, "1m")]
    [InlineData(125.0, "2m")]
    [InlineData(3700.0, "1h 1m")]
    [InlineData(3600.0, "1h")]
    [InlineData(252.0 * 60, "4h 12m")]
    [InlineData(86400.0, "1d")]
    [InlineData(90000.0, "1d 1h")]
    [InlineData(7 * 86400.0, "7d")]
    public void FormatUptime_FormatsCommonDurations(double elapsedSeconds, string expected) {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = start + TimeSpan.FromSeconds(elapsedSeconds);
        Assert.Equal(expected, SettingsTabViewModel.FormatUptime(start, now));
    }

    [Fact]
    public void FormatUptime_NegativeElapsed_ReturnsZero() {
        var start = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var now = start - TimeSpan.FromMinutes(5);
        Assert.Equal("0s", SettingsTabViewModel.FormatUptime(start, now));
    }

    // ---- Traffic Totals: "Exclude from totals" ----

    private static (SettingsTabViewModel Vm, FakeDaemonClient Client, FakeFilePicker Picker,
        FakeUiPreferencesStore Prefs, TotalsExclusionUiState Exclusions) CreateTotalsVm() {
        var client = new FakeDaemonClient();
        var picker = new FakeFilePicker();
        var prefs = new FakeUiPreferencesStore();
        var exclusions = new TotalsExclusionUiState();
        var vm = new SettingsTabViewModel(
            client, new SyncDispatcher(), new FakeShellOpener(), new FakeClipboardWriter(),
            picker, new FakeFileWriter(), prefs, new FakeTimeProvider(FixedTimestamp), exclusions);
        return (vm, client, picker, prefs, exclusions);
    }

    [Fact]
    public async Task AddTotalsExclusion_PickedFile_SendsWholeListAndAppliesEcho() {
        var (vm, client, picker, _, exclusions) = CreateTotalsVm();
        picker.PickedPath = @"C:\vpn\wireguard.exe";

        await vm.AddTotalsExclusionCommand.ExecuteAsync(null);

        var call = Assert.Single(client.SetTotalsSettingsCalls);
        Assert.Equal([@"C:\vpn\wireguard.exe"], call.Values.ExcludedProcessPaths);
        Assert.Equal([@"C:\vpn\wireguard.exe"], vm.Totals.ExcludedPaths);
        Assert.True(exclusions.IsExcluded(@"C:\vpn\wireguard.exe"));
        Assert.False(vm.Totals.IsSaving);
    }

    [Fact]
    public async Task AddTotalsExclusion_UserCancels_NoRpc() {
        var (vm, client, picker, _, _) = CreateTotalsVm();
        picker.PickedPath = null;

        await vm.AddTotalsExclusionCommand.ExecuteAsync(null);

        Assert.Empty(client.SetTotalsSettingsCalls);
    }

    [Fact]
    public async Task AddTotalsExclusion_AlreadyExcluded_NoRpc() {
        var (vm, client, picker, _, _) = CreateTotalsVm();
        vm.Totals.ExcludedPaths.Add(@"C:\vpn\wireguard.exe");
        picker.PickedPath = @"C:\VPN\WIREGUARD.EXE";

        await vm.AddTotalsExclusionCommand.ExecuteAsync(null);

        Assert.Empty(client.SetTotalsSettingsCalls);
    }

    [Fact]
    public async Task RemoveTotalsExclusion_SendsListWithoutPath() {
        var (vm, client, _, _, exclusions) = CreateTotalsVm();
        vm.Totals.ExcludedPaths.Add(@"C:\vpn\wireguard.exe");
        vm.Totals.ExcludedPaths.Add(@"C:\docker\backend.exe");

        await vm.RemoveTotalsExclusionCommand.ExecuteAsync(@"C:\vpn\wireguard.exe");

        var call = Assert.Single(client.SetTotalsSettingsCalls);
        Assert.Equal([@"C:\docker\backend.exe"], call.Values.ExcludedProcessPaths);
        Assert.Equal([@"C:\docker\backend.exe"], vm.Totals.ExcludedPaths);
        Assert.False(exclusions.IsExcluded(@"C:\vpn\wireguard.exe"));
    }

    [Fact]
    public async Task SaveTotalsExclusions_SoftFailure_SurfacesErrorAndKeepsRowList() {
        var (vm, client, picker, _, _) = CreateTotalsVm();
        picker.PickedPath = @"C:\vpn\wireguard.exe";
        client.SetTotalsSettingsResponder = _ => new SetTotalsSettingsResponse {
            Success = false,
            Message = "persistence failed",
        };

        await vm.AddTotalsExclusionCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("persistence failed", vm.ErrorMessage);
        Assert.Empty(vm.Totals.ExcludedPaths);   // echo not applied on failure
    }

    [Fact]
    public void ToggleShowExcludedProcesses_FlipsRowPrefsAndSharedState() {
        var (vm, _, _, prefs, exclusions) = CreateTotalsVm();
        Assert.False(vm.Totals.ShowExcluded);

        vm.ToggleShowExcludedProcessesCommand.Execute(null);

        Assert.True(vm.Totals.ShowExcluded);
        Assert.True(prefs.Load().ShowExcludedProcesses);
        Assert.True(exclusions.ShowExcluded);
    }

    [Fact]
    public async Task ApplySettings_TotalsBundle_SyncsRowAndSharedState() {
        var (vm, client, _, _, exclusions) = CreateTotalsVm();
        var totals = new TotalsSettingsValues();
        totals.ExcludedProcessPaths.Add(@"C:\vpn\wireguard.exe");
        client.GetSettingsResponder = _ => new GetSettingsResponse { Totals = totals };
        client.GetStorageStatsResponder = _ => new GetStorageStatsResponse {
            DatabasePath = "/tmp/test.db",
            DatabaseBytesTotal = 1024,
        };

        await vm.ActivateAsync(CancellationToken.None);

        Assert.Equal([@"C:\vpn\wireguard.exe"], vm.Totals.ExcludedPaths);
        Assert.True(exclusions.IsExcluded(@"C:\vpn\wireguard.exe"));
    }

    // ---- Dispose ----

    [Fact]
    public void Dispose_IsIdempotent() {
        var (vm, _, _, _, _) = CreateVm();
        vm.Dispose();
        vm.Dispose();   // second dispose is a no-op
    }
}
