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
