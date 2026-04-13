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
                NullLogger<DaemonStreamSubscriber>.Instance));

    [Fact]
    public void Ctor_NullLogger_Throws() {
        var fakeClient = new FakeDaemonClient();
        Assert.Throws<ArgumentNullException>("logger",
            () => new DaemonStreamSubscriber(fakeClient, null!));
    }

    [Fact]
    public async Task Start_OnDaemonConnected_DispatchesCounterBatchEvent() {
        var ct = TestContext.Current.CancellationToken;
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
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
}
