using Beholder.Protocol.Local;
using Beholder.Tests.TestDoubles;
using Beholder.Ui.ViewModels;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

/// <summary>
/// Phase 13.2 coverage for the Settings tab view-model — Recording +
/// Hostname Resolution sections. Split into its own file so the existing
/// 13.1 coverage stays focused on Data Storage / Maintenance / About.
/// </summary>
public class SettingsTabViewModelTestsPhase132 {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private static (SettingsTabViewModel Vm, FakeDaemonClient Client)
    CreateVm(GetSettingsResponse? canned = null) {
        var client = new FakeDaemonClient();
        if (canned is not null) client.GetSettingsResponder = _ => canned;
        // GetStorageStats also fires on activate — give it a no-op response so
        // the parallel-task load completes cleanly.
        client.GetStorageStatsResponder = _ => new GetStorageStatsResponse {
            DatabasePath = "/tmp/test.db",
            DatabaseBytesTotal = 1024,
        };
        var vm = new SettingsTabViewModel(
            client,
            new SyncDispatcher(),
            new FakeShellOpener(),
            new FakeClipboardWriter(),
            new FakeTimeProvider(FixedTimestamp));
        return (vm, client);
    }

    private static GetSettingsResponse MakeSettings(
        bool filterSelfTraffic = true,
        bool enablePreload = true,
        bool enableReverseDnsFallback = true,
        bool enableSniCapture = true,
        bool enableNewProcessDetection = true,
        bool enableHashChangeDetection = true,
        bool enableChainIntegrityMonitor = true,
        bool enableHostnameResolution = true
    ) => new() {
        Recording = new RecordingSettingsValues { FilterSelfTraffic = filterSelfTraffic },
        HostnameResolution = new HostnameResolutionSettingsValues {
            EnablePreload = enablePreload,
            EnableReverseDnsFallback = enableReverseDnsFallback,
            EnableSniCapture = enableSniCapture,
        },
        Alerts = new AlertSettingsValues {
            EnableNewProcessDetection = enableNewProcessDetection,
            EnableHashChangeDetection = enableHashChangeDetection,
            EnableChainIntegrityMonitor = enableChainIntegrityMonitor,
        },
        Scanner = new ScannerSettingsValues {
            EnableHostnameResolution = enableHostnameResolution,
        },
    };

    [Fact]
    public async Task ActivateAsync_LoadsSettingsAlongsideStorageStats() {
        var (vm, client) = CreateVm(MakeSettings(
            filterSelfTraffic: false,
            enablePreload: false,
            enableReverseDnsFallback: true,
            enableSniCapture: false));

        await vm.ActivateAsync(CancellationToken.None);

        Assert.Single(client.GetSettingsCalls);
        Assert.Single(client.GetStorageStatsCalls);
        Assert.False(vm.Recording.FilterSelfTraffic);
        Assert.False(vm.HostnameResolution.EnablePreload);
        Assert.True(vm.HostnameResolution.EnableReverseDnsFallback);
        Assert.False(vm.HostnameResolution.EnableSniCapture);
    }

    [Fact]
    public async Task ToggleFilterSelfTraffic_Success_FlipsAndCallsRpc() {
        var (vm, client) = CreateVm(MakeSettings(filterSelfTraffic: true));
        await vm.ActivateAsync(CancellationToken.None);
        client.SetRecordingSettingsResponder = req => new SetRecordingSettingsResponse {
            Success = true,
            Values = req.Values,
        };

        await vm.ToggleFilterSelfTrafficCommand.ExecuteAsync(null);

        Assert.False(vm.Recording.FilterSelfTraffic);
        Assert.False(vm.Recording.IsSaving);
        Assert.Single(client.SetRecordingSettingsCalls);
        Assert.False(client.SetRecordingSettingsCalls[0].Values.FilterSelfTraffic);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task ToggleFilterSelfTraffic_Failure_RevertsAndShowsError() {
        var (vm, client) = CreateVm(MakeSettings(filterSelfTraffic: true));
        await vm.ActivateAsync(CancellationToken.None);
        client.SetRecordingSettingsException =
            new RpcException(new Status(StatusCode.Internal, "kaboom"));

        await vm.ToggleFilterSelfTrafficCommand.ExecuteAsync(null);

        Assert.True(vm.Recording.FilterSelfTraffic);   // reverted
        Assert.False(vm.Recording.IsSaving);
        Assert.True(vm.HasError);
        Assert.Contains("kaboom", vm.ErrorMessage);
    }

    [Fact]
    public async Task ToggleEnableReverseDnsFallback_Success_FlipsLive() {
        var (vm, client) = CreateVm(MakeSettings(enableReverseDnsFallback: true));
        await vm.ActivateAsync(CancellationToken.None);
        client.SetHostnameResolutionSettingsResponder = req =>
            new SetHostnameResolutionSettingsResponse {
                Success = true,
                Values = req.Values,
            };

        await vm.ToggleEnableReverseDnsFallbackCommand.ExecuteAsync(null);

        Assert.False(vm.HostnameResolution.EnableReverseDnsFallback);
        Assert.False(vm.HostnameResolution.IsSavingReverseDnsFallback);
        // The other two values stay where they were.
        Assert.True(vm.HostnameResolution.EnablePreload);
        Assert.True(vm.HostnameResolution.EnableSniCapture);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task ToggleEnablePreload_Failure_RevertsAndShowsError() {
        var (vm, client) = CreateVm(MakeSettings(
            enablePreload: true, enableReverseDnsFallback: true, enableSniCapture: true));
        await vm.ActivateAsync(CancellationToken.None);
        client.SetHostnameResolutionSettingsException =
            new InvalidOperationException("network down");

        await vm.ToggleEnablePreloadCommand.ExecuteAsync(null);

        Assert.True(vm.HostnameResolution.EnablePreload);   // reverted
        Assert.True(vm.HostnameResolution.EnableReverseDnsFallback);
        Assert.True(vm.HostnameResolution.EnableSniCapture);
        Assert.False(vm.HostnameResolution.IsSavingPreload);
        Assert.True(vm.HasError);
        Assert.Contains("network down", vm.ErrorMessage);
    }

    // ---- Phase 13.3: Alerts ----

    [Fact]
    public async Task ActivateAsync_LoadsAlertsAlongsideOtherSections() {
        var (vm, _) = CreateVm(MakeSettings(
            enableNewProcessDetection: false,
            enableHashChangeDetection: true,
            enableChainIntegrityMonitor: false));

        await vm.ActivateAsync(CancellationToken.None);

        Assert.False(vm.Alerts.EnableNewProcessDetection);
        Assert.True(vm.Alerts.EnableHashChangeDetection);
        Assert.False(vm.Alerts.EnableChainIntegrityMonitor);
    }

    [Fact]
    public async Task ToggleEnableNewProcessDetection_Success_FlipsAndSendsAllThreeBools() {
        var (vm, client) = CreateVm(MakeSettings(
            enableNewProcessDetection: true,
            enableHashChangeDetection: true,
            enableChainIntegrityMonitor: true));
        await vm.ActivateAsync(CancellationToken.None);
        client.SetAlertSettingsResponder = req => new SetAlertSettingsResponse {
            Success = true,
            Values = req.Values,
        };

        await vm.ToggleEnableNewProcessDetectionCommand.ExecuteAsync(null);

        Assert.Single(client.SetAlertSettingsCalls);
        var sent = client.SetAlertSettingsCalls[0].Values;
        Assert.False(sent.EnableNewProcessDetection);   // flipped
        Assert.True(sent.EnableHashChangeDetection);     // unchanged
        Assert.True(sent.EnableChainIntegrityMonitor);   // unchanged
        Assert.False(vm.Alerts.EnableNewProcessDetection);
        Assert.False(vm.Alerts.IsSavingNewProcessDetection);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task ToggleEnableHashChangeDetection_Failure_RevertsAndShowsError() {
        var (vm, client) = CreateVm(MakeSettings(
            enableNewProcessDetection: true,
            enableHashChangeDetection: true,
            enableChainIntegrityMonitor: true));
        await vm.ActivateAsync(CancellationToken.None);
        client.SetAlertSettingsException = new InvalidOperationException("daemon offline");

        await vm.ToggleEnableHashChangeDetectionCommand.ExecuteAsync(null);

        // All three reverted to pre-toggle values.
        Assert.True(vm.Alerts.EnableNewProcessDetection);
        Assert.True(vm.Alerts.EnableHashChangeDetection);
        Assert.True(vm.Alerts.EnableChainIntegrityMonitor);
        Assert.False(vm.Alerts.IsSavingHashChangeDetection);
        Assert.True(vm.HasError);
        Assert.Contains("daemon offline", vm.ErrorMessage);
    }

    [Fact]
    public async Task ToggleEnableSniCapture_Success_SendsAllThreeBoolsInRequest() {
        var (vm, client) = CreateVm(MakeSettings(
            enablePreload: false, enableReverseDnsFallback: true, enableSniCapture: true));
        await vm.ActivateAsync(CancellationToken.None);
        client.SetHostnameResolutionSettingsResponder = req =>
            new SetHostnameResolutionSettingsResponse {
                Success = true,
                Values = req.Values,
            };

        await vm.ToggleEnableSniCaptureCommand.ExecuteAsync(null);

        Assert.Single(client.SetHostnameResolutionSettingsCalls);
        var sent = client.SetHostnameResolutionSettingsCalls[0].Values;
        Assert.False(sent.EnablePreload);
        Assert.True(sent.EnableReverseDnsFallback);
        Assert.False(sent.EnableSniCapture);   // flipped from true
    }

    // ---- Phase 13.4: Scanner ----

    [Fact]
    public async Task ActivateAsync_LoadsScannerAlongsideOtherSections() {
        var (vm, _) = CreateVm(MakeSettings(enableHostnameResolution: false));

        await vm.ActivateAsync(CancellationToken.None);

        Assert.False(vm.Scanner.EnableHostnameResolution);
    }

    [Fact]
    public async Task ToggleEnableHostnameResolution_Success_FlipsAndCallsRpc() {
        var (vm, client) = CreateVm(MakeSettings(enableHostnameResolution: true));
        await vm.ActivateAsync(CancellationToken.None);
        client.SetScannerSettingsResponder = req => new SetScannerSettingsResponse {
            Success = true,
            Values = req.Values,
        };

        await vm.ToggleEnableHostnameResolutionCommand.ExecuteAsync(null);

        Assert.Single(client.SetScannerSettingsCalls);
        Assert.False(client.SetScannerSettingsCalls[0].Values.EnableHostnameResolution);
        Assert.False(vm.Scanner.EnableHostnameResolution);
        Assert.False(vm.Scanner.IsSavingHostnameResolution);
        Assert.False(vm.HasError);
    }
}
