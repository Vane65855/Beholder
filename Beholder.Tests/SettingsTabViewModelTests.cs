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

    private static (SettingsTabViewModel Vm, FakeDaemonClient Client, FakeFolderOpener Folder, FakeTimeProvider Time)
    CreateVm(GetStorageStatsResponse? canned = null) {
        var client = new FakeDaemonClient();
        if (canned is not null) client.GetStorageStatsResponder = _ => canned;
        var folder = new FakeFolderOpener();
        var time = new FakeTimeProvider(FixedTimestamp);
        var vm = new SettingsTabViewModel(client, new SyncDispatcher(), folder, time);
        return (vm, client, folder, time);
    }

    private static GetStorageStatsResponse MakeStats(
        string path = @"C:\daemon\data\beholder.db",
        long totalBytes = 1024L * 1024 * 42,
        bool includeChainStatus = false,
        bool chainValid = true,
        long rowsVerified = 1000
    ) {
        var response = new GetStorageStatsResponse {
            DatabasePath = path,
            DatabaseBytesTotal = totalBytes,
            HasChainStatus = includeChainStatus,
        };
        response.Tables.Add(new TableStats { Name = "event_log", RowCount = 50 });
        response.Tables.Add(new TableStats { Name = "traffic_raw", RowCount = 10_000 });
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
            null!, new SyncDispatcher(), new FakeFolderOpener(), new FakeTimeProvider(FixedTimestamp)));

    [Fact]
    public void Constructor_NullDispatcher_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), null!, new FakeFolderOpener(), new FakeTimeProvider(FixedTimestamp)));

    [Fact]
    public void Constructor_NullFolderOpener_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), new SyncDispatcher(), null!, new FakeTimeProvider(FixedTimestamp)));

    [Fact]
    public void Constructor_NullTimeProvider_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsTabViewModel(
            new FakeDaemonClient(), new SyncDispatcher(), new FakeFolderOpener(), null!));

    [Fact]
    public void InitialState_NoStorageStats_NoTables_DefaultPlaceholders() {
        var (vm, _, _, _) = CreateVm();
        Assert.Empty(vm.Tables);
        Assert.Null(vm.StorageStats);
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasError);
        Assert.NotNull(vm.AboutInfo);
        Assert.Equal(ChainStatusRow.NeverVerifiedLabel, vm.ChainStatus.LastVerifiedAtLabel);
    }

    // ---- ActivateAsync ----

    [Fact]
    public async Task ActivateAsync_LoadsStorageStats_AndPopulatesTables() {
        var (vm, client, _, _) = CreateVm(MakeStats(includeChainStatus: true));

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsLoading);
        Assert.NotNull(vm.StorageStats);
        Assert.Equal(2, vm.Tables.Count);
        Assert.True(vm.ChainStatus.HasResult);
        Assert.True(vm.ChainStatus.IsValid);
        Assert.Single(client.GetStorageStatsCalls);
    }

    [Fact]
    public async Task ActivateAsync_RpcThrowsRpcException_SetsErrorBanner() {
        var (vm, client, _, _) = CreateVm();
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
        var (vm, client, _, _) = CreateVm();
        var tcs = new TaskCompletionSource<GetStorageStatsResponse>();
        // Hold the response until we observe both callers in flight. The
        // async responder shape returns a Task so this awaits naturally
        // rather than blocking the test thread synchronously.
        client.AsyncGetStorageStatsResponder = (_, _) => tcs.Task;

        var task1 = vm.ActivateAsync(TestContext.Current.CancellationToken);
        var task2 = vm.ActivateAsync(TestContext.Current.CancellationToken);

        // Cold-start race contract: both callers must hand back the same
        // Task instance so they observe the same load attempt.
        Assert.Same(task1, task2);
        tcs.SetResult(MakeStats());
        await task1;
        Assert.Single(client.GetStorageStatsCalls);
    }

    // ---- RefreshStorageStatsCommand ----

    [Fact]
    public async Task RefreshCommand_ReFetches_AndRebuildsTables() {
        var (vm, client, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        client.GetStorageStatsResponder = _ => {
            var r = MakeStats();
            r.Tables.Clear();
            r.Tables.Add(new TableStats { Name = "lan_device", RowCount = 4 });
            return r;
        };
        await vm.RefreshStorageStatsCommand.ExecuteAsync(null);

        var only = Assert.Single(vm.Tables);
        Assert.Equal("lan_device", only.Name);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsRefreshing);
        Assert.Equal(2, client.GetStorageStatsCalls.Count);
    }

    [Fact]
    public async Task RefreshCommand_RpcFails_SetsErrorBanner_WithoutClearingStats() {
        var (vm, client, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        client.GetStorageStatsException = new RpcException(
            new Status(StatusCode.Internal, "disk full"));
        await vm.RefreshStorageStatsCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("disk full", vm.ErrorMessage);
        // Stats from the successful initial load are preserved — the
        // failed refresh shouldn't wipe what's already on screen.
        Assert.NotNull(vm.StorageStats);
        Assert.Equal(2, vm.Tables.Count);
    }

    // ---- VerifyChainCommand ----

    [Fact]
    public async Task VerifyChainCommand_Success_UpdatesChainStatusRow() {
        var (vm, client, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.VerifyChainResponder = _ => new VerifyChainResponse {
            IsValid = true, RowsVerified = 1247,
        };

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.True(vm.ChainStatus.HasResult);
        Assert.True(vm.ChainStatus.IsValid);
        Assert.Equal(1247, vm.ChainStatus.RowsVerified);
        Assert.True(vm.HasVerifyStatus);
        Assert.False(vm.VerifyStatusIsError);
    }

    [Fact]
    public async Task VerifyChainCommand_Invalid_ShowsErrorBanner() {
        var (vm, client, _, _) = CreateVm(MakeStats());
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
    }

    [Fact]
    public async Task VerifyChainCommand_RpcThrows_ShowsErrorBanner() {
        var (vm, client, _, _) = CreateVm(MakeStats());
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.VerifyChainException = new RpcException(
            new Status(StatusCode.Internal, "verify exploded"));

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.True(vm.HasVerifyStatus);
        Assert.True(vm.VerifyStatusIsError);
        Assert.Contains("verify exploded", vm.VerifyStatusMessage);
    }

    // ---- OpenDataFolderCommand ----

    [Fact]
    public async Task OpenDataFolderCommand_CallsFolderOpener_WithParentDirectory() {
        var (vm, _, folder, _) = CreateVm(MakeStats(path: @"C:\daemon\data\beholder.db"));
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        vm.OpenDataFolderCommand.Execute(null);

        var opened = Assert.Single(folder.OpenedPaths);
        Assert.Equal(@"C:\daemon\data", opened);
    }

    [Fact]
    public void OpenDataFolderCommand_NoStatsLoaded_DoesNothing() {
        var (vm, _, folder, _) = CreateVm();
        vm.OpenDataFolderCommand.Execute(null);
        Assert.Empty(folder.OpenedPaths);
    }

    [Fact]
    public async Task OpenDataFolderCommand_OpenerThrows_SurfacesViaVerifyBanner() {
        var (vm, _, folder, _) = CreateVm(MakeStats(path: @"C:\nonexistent\beholder.db"));
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        folder.Exception = new InvalidOperationException("shell failed");

        vm.OpenDataFolderCommand.Execute(null);

        Assert.True(vm.HasVerifyStatus);
        Assert.True(vm.VerifyStatusIsError);
        Assert.Contains("shell failed", vm.VerifyStatusMessage);
    }

    // ---- Dismiss commands ----

    [Fact]
    public async Task DismissErrorCommand_ClearsHasError() {
        var (vm, client, _, _) = CreateVm();
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

    // ---- Dispose ----

    [Fact]
    public void Dispose_IsIdempotent() {
        var (vm, _, _, _) = CreateVm();
        vm.Dispose();
        vm.Dispose();   // second dispose is a no-op
    }
}
