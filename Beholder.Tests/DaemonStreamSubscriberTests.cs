using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class DaemonStreamSubscriberTests {
    [Fact]
    public void Ctor_NullDaemonClient_Throws() =>
        Assert.Throws<ArgumentNullException>("daemonClient",
            () => new DaemonStreamSubscriber(
                null!,
                TimeProvider.System,
                NullLogger<DaemonStreamSubscriber>.Instance));

    [Fact]
    public void Ctor_NullLogger_Throws() {
        var fakeClient = new FakeDaemonClient();
        Assert.Throws<ArgumentNullException>("logger",
            () => new DaemonStreamSubscriber(fakeClient, TimeProvider.System, null!));
    }

    [Fact]
    public async Task Start_OnDaemonConnected_DispatchesCounterBatchEvent() {
        var ct = TestContext.Current.CancellationToken;
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
            TimeProvider.System,
            NullLogger<DaemonStreamSubscriber>.Instance);

        CounterBatch? received = null;
        var receivedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.CounterBatchReceived += batch => {
            received = batch;
            receivedSignal.TrySetResult();
        };

        await subscriber.StartAsync(ct);

        fakeClient.SimulateConnected();

        var daemonEvent = new DaemonEvent {
            CounterBatch = new CounterBatch {
                TickTimestampUnixNs = 1000,
            },
        };
        daemonEvent.CounterBatch.Snapshots.Add(new CounterSnapshot {
            ProcessName = "test.exe",
            DeltaBytesOut = 512,
        });
        fakeClient.PushEvent(daemonEvent);

        var completed = await Task.WhenAny(
            receivedSignal.Task,
            Task.Delay(TimeSpan.FromSeconds(5), ct));
        Assert.True(completed == receivedSignal.Task, "Timed out waiting for CounterBatchReceived");

        Assert.NotNull(received);
        Assert.Single(received.Snapshots);
        Assert.Equal("test.exe", received.Snapshots[0].ProcessName);

        await subscriber.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_LanDeviceFirstSeenEvent_RaisesEventHandler() {
        var ct = TestContext.Current.CancellationToken;
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);

        LanDeviceFirstSeenEvent? received = null;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.LanDeviceFirstSeenReceived += ev => {
            received = ev;
            signal.TrySetResult();
        };

        await subscriber.StartAsync(ct);
        fakeClient.SimulateConnected();

        var daemonEvent = new DaemonEvent {
            LanDeviceFirstSeen = new LanDeviceFirstSeenEvent {
                Device = new LanDevice {
                    Mac = "aa:bb:cc:dd:ee:01",
                    Ip = "192.168.1.10",
                    Vendor = "Acme",
                    Hostname = "kitchen-tv",
                    FirstSeenUnixNs = 1_000_000_000,
                    LastSeenUnixNs = 2_000_000_000,
                },
            },
        };
        fakeClient.PushEvent(daemonEvent);

        var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
        Assert.True(completed == signal.Task, "Timed out waiting for LanDeviceFirstSeenReceived");

        Assert.NotNull(received);
        Assert.Equal("aa:bb:cc:dd:ee:01", received.Device.Mac);
        Assert.Equal("192.168.1.10", received.Device.Ip);
        Assert.Equal("kitchen-tv", received.Device.Hostname);

        await subscriber.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_LanDeviceLabelChangedEvent_RaisesEventHandler() {
        var ct = TestContext.Current.CancellationToken;
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);

        LanDeviceLabelChangedEvent? received = null;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.LanDeviceLabelChangedReceived += ev => {
            received = ev;
            signal.TrySetResult();
        };

        await subscriber.StartAsync(ct);
        fakeClient.SimulateConnected();

        var daemonEvent = new DaemonEvent {
            LanDeviceLabelChanged = new LanDeviceLabelChangedEvent {
                Device = new LanDevice {
                    Mac = "aa:bb:cc:dd:ee:42",
                    Ip = "192.168.1.42",
                    Label = "Living Room TV",
                    FirstSeenUnixNs = 1_000_000_000,
                    LastSeenUnixNs = 2_000_000_000,
                },
            },
        };
        fakeClient.PushEvent(daemonEvent);

        var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
        Assert.True(completed == signal.Task, "Timed out waiting for LanDeviceLabelChangedReceived");

        Assert.NotNull(received);
        Assert.Equal("aa:bb:cc:dd:ee:42", received.Device.Mac);
        Assert.Equal("Living Room TV", received.Device.Label);

        await subscriber.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_LanDeviceMacChangedEvent_RaisesEventHandler() {
        var ct = TestContext.Current.CancellationToken;
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);

        LanDeviceMacChangedEvent? received = null;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.LanDeviceMacChangedReceived += ev => {
            received = ev;
            signal.TrySetResult();
        };

        await subscriber.StartAsync(ct);
        fakeClient.SimulateConnected();

        var daemonEvent = new DaemonEvent {
            LanDeviceMacChanged = new LanDeviceMacChangedEvent {
                PreviousMac = "11:11:11:11:11:11",
                Device = new LanDevice {
                    Mac = "22:22:22:22:22:22",
                    Ip = "192.168.1.50",
                    FirstSeenUnixNs = 1_000_000_000,
                    LastSeenUnixNs = 2_000_000_000,
                },
            },
        };
        fakeClient.PushEvent(daemonEvent);

        var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
        Assert.True(completed == signal.Task, "Timed out waiting for LanDeviceMacChangedReceived");

        Assert.NotNull(received);
        Assert.Equal("11:11:11:11:11:11", received.PreviousMac);
        Assert.Equal("22:22:22:22:22:22", received.Device.Mac);
        Assert.Equal("192.168.1.50", received.Device.Ip);

        await subscriber.DisposeAsync();
    }
}
