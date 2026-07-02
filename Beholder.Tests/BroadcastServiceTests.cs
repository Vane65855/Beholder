using Beholder.Core;
using Beholder.Daemon.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public class BroadcastServiceTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 11, 15, 30, 45, TimeSpan.Zero);

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void Ctor_NullSource_Throws() {
        Assert.Throws<ArgumentNullException>("source", () => new BroadcastService(
            source: null!,
            timeProvider: new FakeTimeProvider(FixedTimestamp),
            logger: NullLogger<BroadcastService>.Instance));
    }

    [Fact]
    public void Ctor_NullTimeProvider_Throws() {
        Assert.Throws<ArgumentNullException>("timeProvider", () => new BroadcastService(
            source: new FakeSnapshotBatchSource(),
            timeProvider: null!,
            logger: NullLogger<BroadcastService>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws() {
        Assert.Throws<ArgumentNullException>("logger", () => new BroadcastService(
            source: new FakeSnapshotBatchSource(),
            timeProvider: new FakeTimeProvider(FixedTimestamp),
            logger: null!));
    }

    [Fact]
    public async Task SubscribeAsync_SingleSubscriber_ReceivesBatch() {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source,
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        await using var enumerator = broadcaster.SubscribeAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var move = enumerator.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 1, "subscriber registered", ct);

        source.Fire(BuildBatch("firefox.exe"));

        Assert.True(await move.WaitAsync(WaitTimeout, ct));
        var daemonEvent = enumerator.Current;
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.CounterBatch, daemonEvent.PayloadCase);
        var snapshot = Assert.Single(daemonEvent.CounterBatch.Snapshots);
        Assert.Equal("firefox.exe", snapshot.ProcessName);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    [Fact]
    public async Task SubscribeAsync_EmptyHeartbeatBatch_ReachesSubscriberWithTickTimestamp() {
        // Idle-tick heartbeat (ADR 017): an empty snapshot list still fans out
        // as a CounterBatch so UI clients can advance their per-second sample
        // buffers — the batch's tick timestamp is the clock signal.
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source,
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        await using var enumerator = broadcaster.SubscribeAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var move = enumerator.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 1, "subscriber registered", ct);

        source.Fire(Array.Empty<CounterSnapshot>());

        Assert.True(await move.WaitAsync(WaitTimeout, ct));
        var daemonEvent = enumerator.Current;
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.CounterBatch, daemonEvent.PayloadCase);
        Assert.Empty(daemonEvent.CounterBatch.Snapshots);
        Assert.Equal(
            FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000,
            daemonEvent.CounterBatch.TickTimestampUnixNs);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    [Fact]
    public async Task SubscribeAsync_MultipleSubscribers_AllReceiveBatch() {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source,
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        await using var e1 = broadcaster.SubscribeAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var e2 = broadcaster.SubscribeAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var m1 = e1.MoveNextAsync().AsTask();
        var m2 = e2.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 2, "two subscribers registered", ct);

        source.Fire(BuildBatch("chrome.exe"));

        Assert.True(await m1.WaitAsync(WaitTimeout, ct));
        Assert.True(await m2.WaitAsync(WaitTimeout, ct));
        Assert.Equal("chrome.exe", e1.Current.CounterBatch.Snapshots[0].ProcessName);
        Assert.Equal("chrome.exe", e2.Current.CounterBatch.Snapshots[0].ProcessName);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    [Fact]
    public async Task SubscribeAsync_SlowSubscriber_DropsOldestNotNewest() {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source,
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance,
            subscriberChannelCapacity: 2);
        await broadcaster.StartAsync(ct);

        await using var enumerator = broadcaster.SubscribeAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var firstMove = enumerator.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 1, "subscriber registered", ct);

        // Wake the suspended reader once with a sentinel batch so it transitions
        // out of WaitToReadAsync. After this point the iterator is paused at
        // `yield return` and is no longer registered as a waiting reader, so the
        // five subsequent Fire calls all enqueue into the bounded buffer and the
        // drop-oldest policy is exercised deterministically.
        source.Fire(BuildBatch("init"));
        Assert.True(await firstMove.WaitAsync(WaitTimeout, ct));
        Assert.Equal("init", enumerator.Current.CounterBatch.Snapshots[0].ProcessName);

        for (var i = 0; i < 5; i++) {
            source.Fire(BuildBatch($"p{i}"));
        }

        // Channel state after the burst (capacity 2, DropOldest): [p3, p4].
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, ct));
        Assert.Equal("p3", enumerator.Current.CounterBatch.Snapshots[0].ProcessName);
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, ct));
        Assert.Equal("p4", enumerator.Current.CounterBatch.Snapshots[0].ProcessName);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    [Fact]
    public async Task SubscribeAsync_Cancellation_RemovesSubscriberCleanly() {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source,
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var enumerator = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var move = enumerator.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 1, "subscriber registered", ct);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        await enumerator.DisposeAsync();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 0, "subscriber removed", ct);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    [Fact]
    public async Task StopAsync_CompletesAllSubscriberChannels() {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source,
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        await using var e1 = broadcaster.SubscribeAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var e2 = broadcaster.SubscribeAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var m1 = e1.MoveNextAsync().AsTask();
        var m2 = e2.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 2, "two subscribers registered", ct);

        await broadcaster.StopAsync(ct);

        Assert.False(await m1.WaitAsync(WaitTimeout, ct));
        Assert.False(await m2.WaitAsync(WaitTimeout, ct));
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 0, "subscribers removed", ct);

        broadcaster.Dispose();
    }

    [Fact]
    public async Task BroadcastAlert_FansEventToAllSubscribers() {
        // Phase 7's detectors (out of scope here) will call BroadcastAlert
        // after appending an alert's chain row. This test pins the fan-out
        // shape so Phase 7's wiring lands without surprises.
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source,
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        await using var e1 = broadcaster.SubscribeAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var e2 = broadcaster.SubscribeAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var m1 = e1.MoveNextAsync().AsTask();
        var m2 = e2.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 2, "two subscribers registered", ct);

        var alert = new Alert(
            seq: 42,
            kind: AlertKind.NewProcess,
            processPath: @"C:\bin\firefox.exe",
            summary: "firefox.exe first observed making a network connection",
            timestamp: FixedTimestamp,
            firstViewedAt: null);
        broadcaster.BroadcastAlert(alert);

        Assert.True(await m1.WaitAsync(WaitTimeout, ct));
        Assert.True(await m2.WaitAsync(WaitTimeout, ct));
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.Alert, e1.Current.PayloadCase);
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.Alert, e2.Current.PayloadCase);
        // AlertEvent wraps a single Local.Alert; both subscribers see the
        // same seq, kind, and process path round-tripped through ToProto.
        Assert.Equal(42, e1.Current.Alert.Alert.Seq);
        Assert.Equal(Local.AlertKind.NewProcess, e1.Current.Alert.Alert.Kind);
        Assert.Equal(@"C:\bin\firefox.exe", e1.Current.Alert.Alert.ProcessPath);
        Assert.Equal(42, e2.Current.Alert.Alert.Seq);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    [Fact]
    public async Task BroadcastAlert_NoSubscribers_DoesNotThrow() {
        // Edge case mirroring BroadcastRuleChange semantics: calling the
        // broadcast method before any UI client has subscribed (or after
        // every subscriber has disconnected) must be a silent no-op.
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source,
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        var alert = new Alert(
            seq: 1,
            kind: AlertKind.ChainError,
            processPath: "",
            summary: "Chain verification failed at row 47",
            timestamp: FixedTimestamp,
            firstViewedAt: null);

        // Should not throw, should not block.
        var exception = Record.Exception(() => broadcaster.BroadcastAlert(alert));
        Assert.Null(exception);
        Assert.Equal(0, broadcaster.ActiveSubscriberCount);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    // --- Phase 9.3: LAN device broadcast methods ---

    [Fact]
    public void BroadcastLanDeviceFirstSeen_NullDevice_ThrowsArgumentNullException() {
        using var broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(),
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        Assert.Throws<ArgumentNullException>(() => broadcaster.BroadcastLanDeviceFirstSeen(null!));
    }

    [Fact]
    public async Task BroadcastLanDeviceFirstSeen_AllSubscribersReceiveEvent() {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source, new FakeTimeProvider(FixedTimestamp), NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        await using var e1 = broadcaster.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        await using var e2 = broadcaster.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var m1 = e1.MoveNextAsync().AsTask();
        var m2 = e2.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 2, "two subscribers registered", ct);

        var device = new LanDevice(
            Mac: "aa:bb:cc:dd:ee:01",
            Ip: "192.168.1.20",
            Vendor: "TestVendor",
            Hostname: "test-host",
            FirstSeen: FixedTimestamp,
            LastSeen: FixedTimestamp,
            Label: null);
        broadcaster.BroadcastLanDeviceFirstSeen(device);

        Assert.True(await m1.WaitAsync(WaitTimeout, ct));
        Assert.True(await m2.WaitAsync(WaitTimeout, ct));
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.LanDeviceFirstSeen, e1.Current.PayloadCase);
        Assert.Equal("aa:bb:cc:dd:ee:01", e1.Current.LanDeviceFirstSeen.Device.Mac);
        Assert.Equal("aa:bb:cc:dd:ee:01", e2.Current.LanDeviceFirstSeen.Device.Mac);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    [Fact]
    public void BroadcastLanDeviceMacChanged_NullDevice_ThrowsArgumentNullException() {
        using var broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(),
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        Assert.Throws<ArgumentNullException>(
            () => broadcaster.BroadcastLanDeviceMacChanged("aa:bb:cc:dd:ee:01", null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BroadcastLanDeviceMacChanged_NullOrEmptyPreviousMac_ThrowsArgumentException(string? previousMac) {
        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException for
        // null and ArgumentException for empty. xUnit's Assert.Throws is exact
        // match — use ThrowsAny so the Theory covers both shapes with one
        // assertion, since either is an acceptable "rejected bad input" signal.
        using var broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(),
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        var device = new LanDevice(
            Mac: "aa:bb:cc:dd:ee:01",
            Ip: "10.0.0.1",
            Vendor: null,
            Hostname: null,
            FirstSeen: FixedTimestamp,
            LastSeen: FixedTimestamp,
            Label: null);
        Assert.ThrowsAny<ArgumentException>(
            () => broadcaster.BroadcastLanDeviceMacChanged(previousMac!, device));
    }

    [Fact]
    public async Task BroadcastLanDeviceMacChanged_AllSubscribersReceiveEventWithPreviousMac() {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source, new FakeTimeProvider(FixedTimestamp), NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        await using var enumerator = broadcaster.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var move = enumerator.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 1, "subscriber registered", ct);

        var device = new LanDevice(
            Mac: "22:22:22:22:22:22",
            Ip: "192.168.1.50",
            Vendor: "NewVendor",
            Hostname: "new-device",
            FirstSeen: FixedTimestamp,
            LastSeen: FixedTimestamp,
            Label: null);
        broadcaster.BroadcastLanDeviceMacChanged("11:11:11:11:11:11", device);

        Assert.True(await move.WaitAsync(WaitTimeout, ct));
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.LanDeviceMacChanged, enumerator.Current.PayloadCase);
        Assert.Equal("11:11:11:11:11:11", enumerator.Current.LanDeviceMacChanged.PreviousMac);
        Assert.Equal("22:22:22:22:22:22", enumerator.Current.LanDeviceMacChanged.Device.Mac);
        Assert.Equal("192.168.1.50", enumerator.Current.LanDeviceMacChanged.Device.Ip);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    // ---- Phase 9.5: LanDeviceLabelChanged ----

    [Fact]
    public void BroadcastLanDeviceLabelChanged_NullDevice_ThrowsArgumentNullException() {
        using var broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(),
            new FakeTimeProvider(FixedTimestamp),
            NullLogger<BroadcastService>.Instance);
        Assert.Throws<ArgumentNullException>(
            () => broadcaster.BroadcastLanDeviceLabelChanged(null!));
    }

    [Fact]
    public async Task BroadcastLanDeviceLabelChanged_AllSubscribersReceiveEvent() {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakeSnapshotBatchSource();
        var broadcaster = new BroadcastService(
            source, new FakeTimeProvider(FixedTimestamp), NullLogger<BroadcastService>.Instance);
        await broadcaster.StartAsync(ct);

        await using var enumerator = broadcaster.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var move = enumerator.MoveNextAsync().AsTask();
        await WaitForAsync(() => broadcaster.ActiveSubscriberCount == 1, "subscriber registered", ct);

        var device = new LanDevice(
            Mac: "aa:bb:cc:dd:ee:99",
            Ip: "192.168.1.99",
            Vendor: "Acme",
            Hostname: "router.lan",
            FirstSeen: FixedTimestamp,
            LastSeen: FixedTimestamp,
            Label: "Living Room TV");
        broadcaster.BroadcastLanDeviceLabelChanged(device);

        Assert.True(await move.WaitAsync(WaitTimeout, ct));
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.LanDeviceLabelChanged, enumerator.Current.PayloadCase);
        Assert.Equal("aa:bb:cc:dd:ee:99", enumerator.Current.LanDeviceLabelChanged.Device.Mac);
        Assert.Equal("Living Room TV", enumerator.Current.LanDeviceLabelChanged.Device.Label);

        await broadcaster.StopAsync(ct);
        broadcaster.Dispose();
    }

    private static IReadOnlyList<CounterSnapshot> BuildBatch(string processName) {
        return new[] {
            new CounterSnapshot(
                processName: processName,
                processPath: $@"C:\bin\{processName}",
                totalBytesIn: 0,
                totalBytesOut: 0,
                deltaBytesIn: 0,
                deltaBytesOut: 0,
                activeConnectionCount: 0,
                bytesOutByCountry: new Dictionary<CountryCode, long>(),
                timestamp: FixedTimestamp),
        };
    }

    private static async Task WaitForAsync(Func<bool> predicate, string description, CancellationToken cancellationToken) {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline) {
            await Task.Delay(10, cancellationToken);
        }
        if (!predicate()) throw new TimeoutException($"Timed out waiting for: {description}");
    }

    private sealed class FakeSnapshotBatchSource : ISnapshotBatchSource {
        public event Action<IReadOnlyList<CounterSnapshot>>? OnSnapshotBatch;
        public void Fire(IReadOnlyList<CounterSnapshot> batch) => OnSnapshotBatch?.Invoke(batch);
    }
}
