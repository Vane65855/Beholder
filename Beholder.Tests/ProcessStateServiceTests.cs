using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class ProcessStateServiceTests {
    private static (ProcessStateService Service, DaemonStreamSubscriber Subscriber) CreateService() {
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient);
        return (service, subscriber);
    }

    [Fact]
    public void Ctor_NullSubscriber_Throws() =>
        Assert.Throws<ArgumentNullException>("subscriber",
            () => new ProcessStateService(null!, new FakeDaemonClient()));

    [Fact]
    public void OnCounterBatch_SingleProcess_TracksState() {
        var (service, _) = CreateService();
        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/test.exe",
            ProcessName = "test.exe",
            TotalBytesIn = 1000,
            TotalBytesOut = 2000,
            DeltaBytesIn = 100,
            DeltaBytesOut = 200,
        });

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;

        service.OnCounterBatch(batch);

        Assert.NotNull(received);
        Assert.Single(received);
        Assert.True(received.ContainsKey("fake/test.exe"));
        Assert.Equal(1000, received["fake/test.exe"].TotalBytesIn);
        Assert.Equal(2000, received["fake/test.exe"].TotalBytesOut);
        Assert.Equal(100, received["fake/test.exe"].DeltaBytesIn);
        Assert.Equal(200, received["fake/test.exe"].DeltaBytesOut);
    }

    [Fact]
    public void OnCounterBatch_MultipleProcesses_TracksAll() {
        var (service, _) = CreateService();
        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "a.exe", ProcessName = "a.exe",
            TotalBytesIn = 100, TotalBytesOut = 200,
        });
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "b.exe", ProcessName = "b.exe",
            TotalBytesIn = 300, TotalBytesOut = 400,
        });

        service.OnCounterBatch(batch);

        Assert.Equal(2, service.TrackedProcessCount);
    }

    [Fact]
    public void OnCounterBatch_DaemonRestart_ClearsState() {
        var (service, _) = CreateService();

        var batch1 = new CounterBatch();
        batch1.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "test.exe", ProcessName = "test.exe",
            TotalBytesIn = 50_000, TotalBytesOut = 25_000,
        });
        service.OnCounterBatch(batch1);

        // Daemon restarts — totals drop below previously stored values
        var batch2 = new CounterBatch();
        batch2.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "test.exe", ProcessName = "test.exe",
            TotalBytesIn = 100, TotalBytesOut = 50,
        });

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(batch2);

        Assert.NotNull(received);
        Assert.Equal(100, received["test.exe"].TotalBytesIn);
    }

    [Fact]
    public void OnCounterBatch_IdleProcess_GetsZeroDelta() {
        var (service, _) = CreateService();

        // Batch 1: both processes active
        var batch1 = new CounterBatch();
        batch1.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "a.exe", ProcessName = "a.exe",
            TotalBytesIn = 100, DeltaBytesIn = 100,
        });
        batch1.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "b.exe", ProcessName = "b.exe",
            TotalBytesIn = 200, DeltaBytesIn = 200,
        });
        service.OnCounterBatch(batch1);

        // Batch 2: only a.exe reports
        var batch2 = new CounterBatch();
        batch2.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "a.exe", ProcessName = "a.exe",
            TotalBytesIn = 200, DeltaBytesIn = 100,
        });

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(batch2);

        Assert.NotNull(received);
        Assert.Equal(0, received["b.exe"].DeltaBytesIn);
    }

    [Fact]
    public void OnCounterBatch_PushesToRecentBuffers() {
        var (service, _) = CreateService();

        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "test.exe", ProcessName = "test.exe",
            DeltaBytesIn = 42, DeltaBytesOut = 99,
        });

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(batch);

        Assert.NotNull(received);
        var state = received["test.exe"];
        Assert.Equal(1, state.RecentDeltaIn.Count);
        Assert.Equal(42, state.RecentDeltaIn[0]);
        Assert.Equal(1, state.RecentDeltaOut.Count);
        Assert.Equal(99, state.RecentDeltaOut[0]);
    }

    [Fact]
    public void OnCounterBatch_MultipleTicks_BufferAccumulates() {
        var (service, _) = CreateService();

        for (var i = 0; i < 5; i++) {
            var batch = new CounterBatch();
            batch.Snapshots.Add(new CounterSnapshot {
                ProcessPath = "test.exe", ProcessName = "test.exe",
                DeltaBytesOut = (i + 1) * 10,
            });
            service.OnCounterBatch(batch);
        }

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        var finalBatch = new CounterBatch();
        finalBatch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "test.exe", ProcessName = "test.exe",
            DeltaBytesOut = 60,
        });
        service.OnCounterBatch(finalBatch);

        Assert.NotNull(received);
        Assert.Equal(6, received["test.exe"].RecentDeltaOut.Count);
    }

    [Fact]
    public void OnCounterBatch_FiresEvent() {
        var (service, _) = CreateService();
        var eventFired = false;
        service.ProcessStatesUpdated += _ => eventFired = true;

        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "test.exe", ProcessName = "test.exe",
        });
        service.OnCounterBatch(batch);

        Assert.True(eventFired);
    }

    // ---- SeedAsync exception-handling tests ----

    private static (ProcessStateService Service, FakeDaemonClient Client) CreateServiceWithClient() {
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient);
        return (service, fakeClient);
    }

    [Fact]
    public async Task SeedAsync_OperationCanceled_ReThrows() {
        // Cancellation during seeding must surface to the caller
        // (DaemonStreamSubscriber.OnConnected) so shutdown signals aren't muted.
        var (service, client) = CreateServiceWithClient();
        client.SnapshotException = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SeedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SeedAsync_RpcException_Swallowed() {
        // A gRPC failure during seeding is best-effort — the live stream
        // will fill in. Seeding must not throw to the caller.
        var (service, client) = CreateServiceWithClient();
        client.SnapshotException = new RpcException(
            new Status(StatusCode.Unavailable, "daemon offline"));

        await service.SeedAsync(CancellationToken.None);

        Assert.Equal(0, service.TrackedProcessCount);
    }

    [Fact]
    public async Task SeedAsync_PerProcessTimelineFails_ContinuesWithOtherProcesses() {
        // If one process's historical backfill RPC fails with RpcException,
        // the seeding loop must continue with the remaining processes. Both
        // end up in state (just without backfilled recent-window buffers).
        var (service, client) = CreateServiceWithClient();

        var snapshotResponse = new GetSnapshotResponse();
        snapshotResponse.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "a.exe",
            ProcessName = "a.exe",
            TotalBytesIn = 100,
        });
        snapshotResponse.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "b.exe",
            ProcessName = "b.exe",
            TotalBytesIn = 200,
        });
        client.SnapshotResponse = snapshotResponse;
        client.ProcessTimelineException = new RpcException(
            new Status(StatusCode.Internal, "timeline query failed"));

        await service.SeedAsync(CancellationToken.None);

        Assert.Equal(2, service.TrackedProcessCount);
    }
}
