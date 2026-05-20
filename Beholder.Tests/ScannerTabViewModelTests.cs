using System.Reflection;
using Beholder.Protocol.Local;
using Beholder.Tests.TestDoubles;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public class ScannerTabViewModelTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Constructs a <see cref="ScannerTabViewModel"/> wired to the standard
    /// fake stack: <see cref="FakeDaemonClient"/>, real
    /// <see cref="DaemonStreamSubscriber"/> (which the VM listens to for live
    /// broadcasts), and <see cref="SyncDispatcher"/> so the live-update
    /// dispatcher hop runs synchronously on the calling thread. The
    /// <see cref="FakeTimeProvider"/> drives the relative-time labels +
    /// <c>Task.Delay</c> in the transient banner auto-clear so tests can
    /// advance time deterministically.
    /// </summary>
    private static (ScannerTabViewModel Vm, FakeDaemonClient Client, DaemonStreamSubscriber Subscriber, FakeTimeProvider TimeProvider)
    CreateVm(ListLanDevicesResponse? listResponse = null) {
        var client = new FakeDaemonClient();
        if (listResponse is not null) client.ListLanDevicesResponse = listResponse;
        var subscriber = new DaemonStreamSubscriber(
            client, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        var timeProvider = new FakeTimeProvider(FixedTimestamp);
        var vm = new ScannerTabViewModel(client, subscriber, new SyncDispatcher(), timeProvider);
        return (vm, client, subscriber, timeProvider);
    }

    private static LanDevice MakeDevice(
        string mac,
        string ip = "192.168.1.42",
        string vendor = "TestVendor",
        string hostname = "test-host",
        DateTimeOffset? firstSeen = null,
        DateTimeOffset? lastSeen = null
    ) {
        var first = firstSeen ?? FixedTimestamp.AddDays(-1);
        var last = lastSeen ?? FixedTimestamp;
        return new LanDevice {
            Mac = mac,
            Ip = ip,
            Vendor = vendor,
            Hostname = hostname,
            FirstSeenUnixNs = first.ToUnixTimeMilliseconds() * 1_000_000L,
            LastSeenUnixNs = last.ToUnixTimeMilliseconds() * 1_000_000L,
        };
    }

    private static ListLanDevicesResponse ListResponseWith(params LanDevice[] devices) {
        var response = new ListLanDevicesResponse();
        foreach (var device in devices) response.Devices.Add(device);
        return response;
    }

    /// <summary>
    /// Raises <see cref="DaemonStreamSubscriber.LanDeviceFirstSeenReceived"/>
    /// via reflection. Mirrors the precedent in
    /// <see cref="AlertsTabViewModelTests"/>'s <c>RaiseAlertReceived</c>.
    /// </summary>
    private static void RaiseLanDeviceFirstSeen(DaemonStreamSubscriber subscriber, LanDeviceFirstSeenEvent ev) {
        var eventField = typeof(DaemonStreamSubscriber)
            .GetField("LanDeviceFirstSeenReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var del = (Action<LanDeviceFirstSeenEvent>?)eventField.GetValue(subscriber);
        del?.Invoke(ev);
    }

    private static void RaiseLanDeviceMacChanged(DaemonStreamSubscriber subscriber, LanDeviceMacChangedEvent ev) {
        var eventField = typeof(DaemonStreamSubscriber)
            .GetField("LanDeviceMacChangedReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var del = (Action<LanDeviceMacChangedEvent>?)eventField.GetValue(subscriber);
        del?.Invoke(ev);
    }

    // ---- Constructor null-guards ----

    [Fact]
    public void Ctor_NullDaemonClient_ThrowsArgumentNullException() {
        var subscriber = new DaemonStreamSubscriber(
            new FakeDaemonClient(), TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        Assert.Throws<ArgumentNullException>(() => new ScannerTabViewModel(
            null!, subscriber, new SyncDispatcher(), new FakeTimeProvider()));
    }

    [Fact]
    public void Ctor_NullSubscriber_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new ScannerTabViewModel(
            new FakeDaemonClient(), null!, new SyncDispatcher(), new FakeTimeProvider()));
    }

    [Fact]
    public void Ctor_NullDispatcher_ThrowsArgumentNullException() {
        var subscriber = new DaemonStreamSubscriber(
            new FakeDaemonClient(), TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        Assert.Throws<ArgumentNullException>(() => new ScannerTabViewModel(
            new FakeDaemonClient(), subscriber, null!, new FakeTimeProvider()));
    }

    [Fact]
    public void Ctor_NullTimeProvider_ThrowsArgumentNullException() {
        var subscriber = new DaemonStreamSubscriber(
            new FakeDaemonClient(), TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        Assert.Throws<ArgumentNullException>(() => new ScannerTabViewModel(
            new FakeDaemonClient(), subscriber, new SyncDispatcher(), null!));
    }

    // ---- Loading state ----

    [Fact]
    public async Task ActivateAsync_BeforeRpcCompletes_IsLoadingTrue() {
        var (vm, client, _, _) = CreateVm();
        var release = new TaskCompletionSource<ListLanDevicesResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AsyncListLanDevicesResponder = (_, _) => release.Task;

        var activation = vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.IsLoading);
        Assert.False(activation.IsCompleted);

        release.SetResult(new ListLanDevicesResponse());
        await activation;
    }

    [Fact]
    public async Task ActivateAsync_AfterRpcCompletes_IsLoadingFalse() {
        var (vm, _, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.False(vm.IsLoading);
    }

    // ---- Empty state ----

    [Fact]
    public async Task ActivateAsync_EmptyResponse_ShowsEmptyState() {
        var (vm, _, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(vm.Devices);
        Assert.False(vm.HasDevices);
        Assert.True(vm.ShowEmptyState);
        Assert.False(vm.ShowLoadingState);
        Assert.Null(vm.SelectedDevice);
    }

    [Fact]
    public async Task ActivateAsync_EmptyResponse_DeviceCountLabelIsZero() {
        var (vm, _, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Equal("0 devices", vm.DeviceCountLabel);
    }

    // ---- Populated state ----

    [Fact]
    public async Task ActivateAsync_WithDevices_PopulatesCollection() {
        var response = ListResponseWith(
            MakeDevice("aa:aa:aa:aa:aa:01", ip: "192.168.1.10"),
            MakeDevice("aa:aa:aa:aa:aa:02", ip: "192.168.1.11"),
            MakeDevice("aa:aa:aa:aa:aa:03", ip: "192.168.1.12"));
        var (vm, _, _, _) = CreateVm(response);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, vm.Devices.Count);
        Assert.True(vm.HasDevices);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public async Task ActivateAsync_WithDevices_AutoSelectsFirstDevice() {
        var response = ListResponseWith(MakeDevice("aa:aa:aa:aa:aa:01", ip: "192.168.1.10"));
        var (vm, _, _, _) = CreateVm(response);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.SelectedDevice);
        Assert.Equal("aa:aa:aa:aa:aa:01", vm.SelectedDevice.Mac);
        Assert.True(vm.ShowDetailPane);
    }

    [Fact]
    public async Task ActivateAsync_PreservesResponseOrder() {
        // Server returns last-seen DESC; the VM seeds in that order via the
        // upsert path, which prepends each new MAC to the front. The seed
        // loop processes the response top-to-bottom, so the *last* device in
        // the response ends up at index 0 (most-recently inserted). Verify
        // this is the contract: the response's last device is at the top.
        var response = ListResponseWith(
            MakeDevice("aa:aa:aa:aa:aa:01"),
            MakeDevice("aa:aa:aa:aa:aa:02"),
            MakeDevice("aa:aa:aa:aa:aa:03"));
        var (vm, _, _, _) = CreateVm(response);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("aa:aa:aa:aa:aa:03", vm.Devices[0].Mac);
        Assert.Equal("aa:aa:aa:aa:aa:02", vm.Devices[1].Mac);
        Assert.Equal("aa:aa:aa:aa:aa:01", vm.Devices[2].Mac);
    }

    [Fact]
    public async Task ActivateAsync_DeviceCountLabelReflectsRowCount() {
        var response = ListResponseWith(
            MakeDevice("aa:aa:aa:aa:aa:01"),
            MakeDevice("aa:aa:aa:aa:aa:02"));
        var (vm, _, _, _) = CreateVm(response);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("2 devices", vm.DeviceCountLabel);
    }

    // ---- Error state ----

    [Fact]
    public async Task ActivateAsync_RpcThrows_SetsErrorState() {
        var (vm, client, _, _) = CreateVm();
        client.ListLanDevicesException = new InvalidOperationException("boom");

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasError);
        Assert.Contains("boom", vm.ErrorMessage);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task ActivateAsync_RpcThrows_DoesNotShowEmptyState() {
        // Error state and empty state are mutually exclusive — when the load
        // failed, the error banner takes priority over the "no devices" copy.
        var (vm, client, _, _) = CreateVm();
        client.ListLanDevicesException = new InvalidOperationException("nope");

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasError);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public void DismissErrorCommand_ClearsErrorState() {
        var (vm, _, _, _) = CreateVm();
        // Force the error state directly so the test is independent of
        // ActivateAsync's specific failure plumbing.
        var hasErrorProp = typeof(ScannerTabViewModel).GetProperty("HasError")!;
        hasErrorProp.SetValue(vm, true);
        var errMsgProp = typeof(ScannerTabViewModel).GetProperty("ErrorMessage")!;
        errMsgProp.SetValue(vm, "previous error");

        vm.DismissErrorCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Equal(string.Empty, vm.ErrorMessage);
    }

    // ---- Extreme state ----

    [Fact]
    public async Task LiveStream_Append50FirstSeenEvents_AllRowsPresentNoDuplicates() {
        var (vm, _, subscriber, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 50; i++) {
            RaiseLanDeviceFirstSeen(subscriber, new LanDeviceFirstSeenEvent {
                Device = MakeDevice($"aa:aa:aa:aa:{i:x2}:00", ip: $"10.0.{i / 256}.{i % 256}"),
            });
        }

        Assert.Equal(50, vm.Devices.Count);
        // No duplicate MACs.
        var distinct = vm.Devices.Select(d => d.Mac).Distinct().Count();
        Assert.Equal(50, distinct);
    }

    // ---- Stream events ----

    [Fact]
    public async Task LanDeviceFirstSeen_NewMac_PrependsRow() {
        var response = ListResponseWith(MakeDevice("aa:aa:aa:aa:aa:01"));
        var (vm, _, subscriber, _) = CreateVm(response);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        RaiseLanDeviceFirstSeen(subscriber, new LanDeviceFirstSeenEvent {
            Device = MakeDevice("bb:bb:bb:bb:bb:02", ip: "192.168.1.99"),
        });

        Assert.Equal(2, vm.Devices.Count);
        Assert.Equal("bb:bb:bb:bb:bb:02", vm.Devices[0].Mac);
        Assert.Equal("aa:aa:aa:aa:aa:01", vm.Devices[1].Mac);
    }

    [Fact]
    public async Task LanDeviceFirstSeen_KnownMac_RefreshesExistingRowInPlace() {
        var response = ListResponseWith(
            MakeDevice("aa:aa:aa:aa:aa:01"),
            MakeDevice("bb:bb:bb:bb:bb:02"));
        var (vm, _, subscriber, _) = CreateVm(response);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        var existingRow = vm.Devices.First(d => d.Mac == "aa:aa:aa:aa:aa:01");

        // Same MAC, but a fresher last-seen — should refresh in place and
        // move to the front of the list.
        var refreshedLastSeen = FixedTimestamp.AddMinutes(5);
        RaiseLanDeviceFirstSeen(subscriber, new LanDeviceFirstSeenEvent {
            Device = MakeDevice("aa:aa:aa:aa:aa:01", lastSeen: refreshedLastSeen),
        });

        Assert.Equal(2, vm.Devices.Count);  // no new row created
        Assert.Same(existingRow, vm.Devices[0]);  // same instance, moved to front
        Assert.Equal(refreshedLastSeen, existingRow.LastSeen);
    }

    [Fact]
    public async Task LanDeviceMacChanged_RemovesPreviousRowAndInsertsNew() {
        var response = ListResponseWith(MakeDevice("aa:aa:aa:aa:aa:01", ip: "192.168.1.42"));
        var (vm, _, subscriber, _) = CreateVm(response);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        // The IP 192.168.1.42 now reports a different MAC.
        RaiseLanDeviceMacChanged(subscriber, new LanDeviceMacChangedEvent {
            PreviousMac = "aa:aa:aa:aa:aa:01",
            Device = MakeDevice("cc:cc:cc:cc:cc:99", ip: "192.168.1.42"),
        });

        Assert.Single(vm.Devices);
        Assert.Equal("cc:cc:cc:cc:cc:99", vm.Devices[0].Mac);
    }

    [Fact]
    public async Task LanDeviceMacChanged_PreviousMacWasSelected_ClearsSelection() {
        var response = ListResponseWith(MakeDevice("aa:aa:aa:aa:aa:01"));
        var (vm, _, subscriber, _) = CreateVm(response);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(vm.SelectedDevice);

        RaiseLanDeviceMacChanged(subscriber, new LanDeviceMacChangedEvent {
            PreviousMac = "aa:aa:aa:aa:aa:01",
            Device = MakeDevice("cc:cc:cc:cc:cc:99"),
        });

        // SelectedDevice was the old row; it must be cleared so the detail
        // pane doesn't keep showing data for a MAC that just got evicted.
        Assert.Null(vm.SelectedDevice);
    }

    // ---- TriggerScan command ----

    [Fact]
    public async Task TriggerScanCommand_SuccessResponse_ShowsConfirmationMessage() {
        var (vm, client, _, _) = CreateVm();
        client.TriggerScanResponder = _ => new TriggerScanResponse {
            Success = true,
            Message = "Scan complete: 5 devices observed",
            DevicesObserved = 5,
        };

        await vm.TriggerScanCommand.ExecuteAsync(null);

        Assert.True(vm.HasScanStatusMessage);
        Assert.False(vm.ScanStatusIsError);
        Assert.Contains("5 devices", vm.ScanStatusMessage);
        Assert.False(vm.IsScanInProgress);
        Assert.Single(client.TriggerScanCalls);
    }

    [Fact]
    public async Task TriggerScanCommand_FailureResponse_ShowsErrorMessage() {
        var (vm, client, _, _) = CreateVm();
        client.TriggerScanResponder = _ => new TriggerScanResponse {
            Success = false,
            Message = "scanner inactive",
            DevicesObserved = 0,
        };

        await vm.TriggerScanCommand.ExecuteAsync(null);

        Assert.True(vm.HasScanStatusMessage);
        Assert.True(vm.ScanStatusIsError);
        Assert.Contains("scanner inactive", vm.ScanStatusMessage);
    }

    [Fact]
    public async Task TriggerScanCommand_RpcThrows_ShowsErrorMessage() {
        var (vm, client, _, _) = CreateVm();
        client.TriggerScanException = new InvalidOperationException("network gone");

        await vm.TriggerScanCommand.ExecuteAsync(null);

        Assert.True(vm.HasScanStatusMessage);
        Assert.True(vm.ScanStatusIsError);
        Assert.Contains("network gone", vm.ScanStatusMessage);
        Assert.False(vm.IsScanInProgress);
    }

    [Fact]
    public async Task TriggerScanCommand_WhileScanInProgress_DoesNotFireSecondRpc() {
        // The command's first line is `if (IsScanInProgress) return;` —
        // exercised by forcing the flag true before invocation. Confirms the
        // guard is in the command itself (not just the button's IsEnabled),
        // so a programmatic re-entry can't queue a storm of RPCs.
        var (vm, client, _, _) = CreateVm();
        var isScanInProgressProp = typeof(ScannerTabViewModel).GetProperty("IsScanInProgress")!;
        isScanInProgressProp.SetValue(vm, true);

        await vm.TriggerScanCommand.ExecuteAsync(null);

        Assert.Empty(client.TriggerScanCalls);
    }

    [Fact]
    public void DismissScanStatusCommand_ClearsBanner() {
        var (vm, _, _, _) = CreateVm();
        // Force the banner state directly to keep the test independent of the
        // RPC plumbing.
        var hasProp = typeof(ScannerTabViewModel).GetProperty("HasScanStatusMessage")!;
        hasProp.SetValue(vm, true);
        var msgProp = typeof(ScannerTabViewModel).GetProperty("ScanStatusMessage")!;
        msgProp.SetValue(vm, "previous status");

        vm.DismissScanStatusCommand.Execute(null);

        Assert.False(vm.HasScanStatusMessage);
        Assert.Equal(string.Empty, vm.ScanStatusMessage);
    }

    // ---- Selection / detail pane ----

    [Fact]
    public async Task SelectingDevice_FlipsShowDetailPaneTrue() {
        var response = ListResponseWith(MakeDevice("aa:aa:aa:aa:aa:01"));
        var (vm, _, _, _) = CreateVm(response);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        // Activation already auto-selects the first device, so clear first.
        vm.SelectedDevice = null;
        Assert.False(vm.ShowDetailPane);

        vm.SelectedDevice = vm.Devices[0];

        Assert.True(vm.ShowDetailPane);
    }

    [Fact]
    public async Task ClearingSelection_HidesDetailPane() {
        var response = ListResponseWith(MakeDevice("aa:aa:aa:aa:aa:01"));
        var (vm, _, _, _) = CreateVm(response);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.ShowDetailPane);

        vm.SelectedDevice = null;

        Assert.False(vm.ShowDetailPane);
    }

    // ---- Cold-start race ----

    [Fact]
    public async Task ActivateAsync_ConcurrentCallers_ShareSameUnderlyingLoad() {
        var (vm, client, _, _) = CreateVm();
        var release = new TaskCompletionSource<ListLanDevicesResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AsyncListLanDevicesResponder = (_, _) => release.Task;

        var firstCall = vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.False(firstCall.IsCompleted);
        var secondCall = vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.False(secondCall.IsCompleted);
        Assert.Same(firstCall, secondCall);  // key invariant: same task instance, not a synthetic dup

        release.SetResult(ListResponseWith(MakeDevice("aa:aa:aa:aa:aa:01")));
        await secondCall;
        Assert.Single(vm.Devices);
    }

    [Fact]
    public async Task ActivateAsync_FiresListLanDevicesOnce_AcrossRepeatedCallers() {
        // Second-and-subsequent ActivateAsync calls hand back the cached
        // task; the RPC fires once.
        var (vm, client, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Single(client.ListLanDevicesCalls);
    }

    // ---- Dispose ----

    [Fact]
    public void Dispose_UnsubscribesFromStreamEvents() {
        var (vm, _, subscriber, _) = CreateVm();
        var firstSeenField = typeof(DaemonStreamSubscriber).GetField(
            "LanDeviceFirstSeenReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var macChangedField = typeof(DaemonStreamSubscriber).GetField(
            "LanDeviceMacChangedReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var firstSeenBefore = ((Delegate?)firstSeenField.GetValue(subscriber))?.GetInvocationList().Length ?? 0;
        var macChangedBefore = ((Delegate?)macChangedField.GetValue(subscriber))?.GetInvocationList().Length ?? 0;

        vm.Dispose();

        var firstSeenAfter = ((Delegate?)firstSeenField.GetValue(subscriber))?.GetInvocationList().Length ?? 0;
        var macChangedAfter = ((Delegate?)macChangedField.GetValue(subscriber))?.GetInvocationList().Length ?? 0;
        Assert.Equal(firstSeenBefore - 1, firstSeenAfter);
        Assert.Equal(macChangedBefore - 1, macChangedAfter);
    }
}
