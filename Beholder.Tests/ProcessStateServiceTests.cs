using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public class ProcessStateServiceTests {
    private const long NsPerSecond = 1_000_000_000;

    private static readonly DateTimeOffset SeedNow =
        new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly long SeedNowUnixNs =
        SeedNow.ToUnixTimeMilliseconds() * 1_000_000;

    private static (ProcessStateService Service, DaemonStreamSubscriber Subscriber) CreateService() {
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
            TimeProvider.System,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient, TimeProvider.System);
        return (service, subscriber);
    }

    [Fact]
    public void Ctor_NullSubscriber_Throws() =>
        Assert.Throws<ArgumentNullException>("subscriber",
            () => new ProcessStateService(null!, new FakeDaemonClient(), TimeProvider.System));

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
            TimeProvider.System,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient, TimeProvider.System);
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
    public void Dispose_DoesNotThrow() {
        // Smoke: Dispose unsubscribes from the subscriber's CounterBatchReceived
        // event. The symmetry is verified by code review; this test guards that
        // the Dispose path is at least reachable without throwing.
        var (service, _) = CreateServiceWithClient();
        var exception = Record.Exception(() => service.Dispose());
        Assert.Null(exception);
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

    // ---- ADR 017: tick-timestamp gap-fill (1 buffer sample = 1 second) ----

    private static CounterBatch BuildTickedBatch(long tickUnixNs, params (string Path, long DeltaIn)[] snapshots) {
        var batch = new CounterBatch { TickTimestampUnixNs = tickUnixNs };
        foreach (var (path, deltaIn) in snapshots) {
            batch.Snapshots.Add(new CounterSnapshot {
                ProcessPath = path, ProcessName = path,
                TotalBytesIn = long.MaxValue / 2,  // constant: never triggers restart detection
                DeltaBytesIn = deltaIn,
            });
        }
        return batch;
    }

    [Fact]
    public void OnCounterBatch_TickGap_BackfillsZeroSamples() {
        // Two batches 4 seconds apart mean 3 seconds passed with no batch —
        // the buffer must gain 3 zero samples between them so 1 sample keeps
        // meaning 1 wall-clock second.
        var (service, _) = CreateService();
        service.OnCounterBatch(BuildTickedBatch(SeedNowUnixNs, ("test.exe", 42)));

        service.OnCounterBatch(BuildTickedBatch(SeedNowUnixNs + 4 * NsPerSecond, ("test.exe", 10)));

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(BuildTickedBatch(SeedNowUnixNs + 5 * NsPerSecond, ("test.exe", 7)));

        Assert.NotNull(received);
        var buffer = received["test.exe"].RecentDeltaIn;
        Assert.Equal(6, buffer.Count);
        Assert.Equal(42, buffer[0]);
        Assert.Equal(0, buffer[1]);
        Assert.Equal(0, buffer[2]);
        Assert.Equal(0, buffer[3]);
        Assert.Equal(10, buffer[4]);
        Assert.Equal(7, buffer[5]);
    }

    [Fact]
    public void OnCounterBatch_TickGap_BackfillsProcessesAbsentFromTheBatch() {
        var (service, _) = CreateService();
        service.OnCounterBatch(BuildTickedBatch(
            SeedNowUnixNs, ("a.exe", 1), ("b.exe", 2)));

        // 3 missed seconds; b.exe is also absent from the new batch, so it
        // gets 3 gap zeros plus the idle-process zero for this tick.
        service.OnCounterBatch(BuildTickedBatch(SeedNowUnixNs + 4 * NsPerSecond, ("a.exe", 3)));

        Assert.Equal(2, service.TrackedProcessCount);
        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(BuildTickedBatch(SeedNowUnixNs + 5 * NsPerSecond, ("a.exe", 4)));

        Assert.NotNull(received);
        Assert.Equal(6, received["a.exe"].RecentDeltaIn.Count);
        Assert.Equal(6, received["b.exe"].RecentDeltaIn.Count);
    }

    [Fact]
    public void OnCounterBatch_FirstBatchWithTick_DoesNotBackfill() {
        var (service, _) = CreateService();

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(BuildTickedBatch(SeedNowUnixNs, ("test.exe", 42)));

        Assert.NotNull(received);
        Assert.Equal(1, received["test.exe"].RecentDeltaIn.Count);
    }

    [Fact]
    public void OnCounterBatch_BackwardsClockStep_DoesNotBackfill() {
        var (service, _) = CreateService();
        service.OnCounterBatch(BuildTickedBatch(SeedNowUnixNs, ("test.exe", 42)));

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(BuildTickedBatch(SeedNowUnixNs - 10 * NsPerSecond, ("test.exe", 10)));

        Assert.NotNull(received);
        Assert.Equal(2, received["test.exe"].RecentDeltaIn.Count);
    }

    [Fact]
    public void OnCounterBatch_HugeTickGap_CapsBackfillAtWindowSize() {
        // A gap longer than the 5-minute window (sleep/resume) zeroes the
        // whole visible history; the fill must not spin for hours of zeros.
        var (service, _) = CreateService();
        service.OnCounterBatch(BuildTickedBatch(SeedNowUnixNs, ("test.exe", 42)));

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(BuildTickedBatch(
            SeedNowUnixNs + 10_000 * NsPerSecond, ("test.exe", 10)));

        Assert.NotNull(received);
        var buffer = received["test.exe"].RecentDeltaIn;
        Assert.Equal(ProcessState.RecentWindowSampleCount, buffer.Count);
        Assert.Equal(10, buffer[buffer.Count - 1]);
        Assert.Equal(0, buffer[buffer.Count - 2]);
    }

    // ---- ADR 017: seed alignment (buffer right edge = now) ----

    private static (ProcessStateService Service, FakeDaemonClient Client) CreateSeededServiceAtSeedNow() {
        var fakeClient = new FakeDaemonClient();
        var time = new FakeTimeProvider(SeedNow);
        var subscriber = new DaemonStreamSubscriber(
            fakeClient, time, NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient, time);
        return (service, fakeClient);
    }

    private static GetProcessTimelineResponse BuildTimeline(params (long AgeSeconds, long BytesIn)[] points) {
        var response = new GetProcessTimelineResponse();
        foreach (var (ageSeconds, bytesIn) in points) {
            response.Points.Add(new TrafficTimePoint {
                TimestampUnixNs = SeedNowUnixNs - ageSeconds * NsPerSecond,
                BytesIn = bytesIn,
                BytesOut = 0,
            });
        }
        return response;
    }

    [Fact]
    public async Task SeedAsync_TrailingIdleSeconds_PadsBufferToNow() {
        // The process last moved bytes 60 seconds ago. Without trailing
        // padding its seeded buffer would end at that sample and the chart
        // would draw minute-old traffic at the "now" edge.
        var (service, client) = CreateSeededServiceAtSeedNow();
        var snapshot = new GetSnapshotResponse();
        snapshot.Snapshots.Add(new CounterSnapshot { ProcessPath = "a.exe", ProcessName = "a.exe" });
        client.SnapshotResponse = snapshot;
        client.ProcessTimelineResponder = _ => BuildTimeline((60, 100), (59, 200));

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        await service.SeedAsync(CancellationToken.None);

        Assert.NotNull(received);
        var buffer = received["a.exe"].RecentDeltaIn;
        // 2 data samples + 58 trailing zeros = the last bucket sits 59
        // samples back from the right edge, i.e. 59 seconds before now.
        Assert.Equal(60, buffer.Count);
        Assert.Equal(100, buffer[0]);
        Assert.Equal(200, buffer[1]);
        Assert.Equal(0, buffer[2]);
        Assert.Equal(0, buffer[59]);
    }

    [Fact]
    public async Task SeedAsync_SetsTickBaseline_FirstLiveBatchGapFillsFromSeedTime() {
        // Seed aligns buffers to `now`; the first live batch 10 seconds later
        // must add 9 gap zeros + its own sample — measured from the seed
        // instant, not treated as an unknown baseline.
        var (service, client) = CreateSeededServiceAtSeedNow();
        var snapshot = new GetSnapshotResponse();
        snapshot.Snapshots.Add(new CounterSnapshot { ProcessPath = "a.exe", ProcessName = "a.exe" });
        client.SnapshotResponse = snapshot;
        client.ProcessTimelineResponder = _ => BuildTimeline((1, 100));
        await service.SeedAsync(CancellationToken.None);

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(BuildTickedBatch(
            SeedNowUnixNs + 10 * NsPerSecond, ("a.exe", 7)));

        Assert.NotNull(received);
        var buffer = received["a.exe"].RecentDeltaIn;
        Assert.Equal(11, buffer.Count);
        Assert.Equal(100, buffer[0]);
        Assert.Equal(0, buffer[1]);
        Assert.Equal(7, buffer[10]);
    }

    [Fact]
    public void OnCounterBatch_PopulatesActiveConnectionCount() {
        // The Firewall tab's HOSTS column reads ActiveConnectionCount as a
        // proxy for "live destinations" — the value must round-trip from
        // CounterSnapshot through ProcessState faithfully.
        var (service, _) = CreateService();
        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "test.exe",
            ProcessName = "test.exe",
            TotalBytesIn = 1000,
            TotalBytesOut = 2000,
            ActiveConnectionCount = 7,
        });

        IReadOnlyDictionary<string, ProcessState>? received = null;
        service.ProcessStatesUpdated += states => received = states;
        service.OnCounterBatch(batch);

        Assert.NotNull(received);
        Assert.Equal(7, received["test.exe"].ActiveConnectionCount);
    }
}
