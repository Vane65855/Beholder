using System.Net;
using System.Threading.Channels;
using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public class TrafficEngineTests {
    private static readonly DateTimeOffset StartInstant =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    // --- 1-second tick / CounterSnapshot tests (preserving Accumulator contract) ---

    [Fact]
    public async Task SingleFlowEvent_ProducesSnapshotWithCorrectBytes() {
        await using var fixture = Fixture.Start();

        var batch = await fixture.DriveTickAsync(BuildFlow(bytesOut: 100));

        var snapshot = Assert.Single(batch);
        Assert.Equal(100, snapshot.DeltaBytesOut);
        Assert.Equal(100, snapshot.TotalBytesOut);
        Assert.Equal(0, snapshot.DeltaBytesIn);
        Assert.Equal(0, snapshot.TotalBytesIn);
    }

    [Fact]
    public async Task MultipleEventsSameProcess_AggregateCorrectly() {
        await using var fixture = Fixture.Start();

        var batch = await fixture.DriveTickAsync(
            BuildFlow(bytesOut: 50),
            BuildFlow(bytesOut: 75),
            BuildFlow(bytesOut: 25));

        var snapshot = Assert.Single(batch);
        Assert.Equal(150, snapshot.DeltaBytesOut);
        Assert.Equal(150, snapshot.TotalBytesOut);
    }

    [Fact]
    public async Task MultipleProcesses_ProduceSeparateSnapshots() {
        await using var fixture = Fixture.Start();

        var batch = await fixture.DriveTickAsync(
            BuildFlow(processName: "curl.exe", processPath: @"C:\curl.exe", bytesOut: 100),
            BuildFlow(processName: "firefox.exe", processPath: @"C:\firefox.exe", bytesOut: 200));

        Assert.Equal(2, batch.Count);
        var curl = Assert.Single(batch, s => s.ProcessPath == @"C:\curl.exe");
        var firefox = Assert.Single(batch, s => s.ProcessPath == @"C:\firefox.exe");
        Assert.Equal(100, curl.DeltaBytesOut);
        Assert.Equal(200, firefox.DeltaBytesOut);
    }

    [Fact]
    public async Task TotalsAreMonotonic_DeltasResetEachTick() {
        await using var fixture = Fixture.Start();

        var firstBatch = await fixture.DriveTickAsync(BuildFlow(bytesOut: 100));
        var firstSnapshot = Assert.Single(firstBatch);
        Assert.Equal(100, firstSnapshot.DeltaBytesOut);
        Assert.Equal(100, firstSnapshot.TotalBytesOut);

        var secondBatch = await fixture.DriveTickAsync(BuildFlow(bytesOut: 50));
        var secondSnapshot = Assert.Single(secondBatch);
        Assert.Equal(50, secondSnapshot.DeltaBytesOut);
        Assert.Equal(150, secondSnapshot.TotalBytesOut);
    }

    [Fact]
    public async Task InactiveProcessesOmittedFromBatch() {
        await using var fixture = Fixture.Start();

        var firstBatch = await fixture.DriveTickAsync(
            BuildFlow(processName: "a.exe", processPath: @"C:\a.exe", bytesOut: 100));
        Assert.Single(firstBatch);
        Assert.Equal(@"C:\a.exe", firstBatch[0].ProcessPath);

        var secondBatch = await fixture.DriveTickAsync(
            BuildFlow(processName: "b.exe", processPath: @"C:\b.exe", bytesOut: 200));
        var only = Assert.Single(secondBatch);
        Assert.Equal(@"C:\b.exe", only.ProcessPath);
        Assert.Equal(200, only.DeltaBytesOut);
    }

    [Fact]
    public async Task ActiveConnectionCount_TracksUniqueRemoteEndpoints() {
        await using var fixture = Fixture.Start();

        var batch = await fixture.DriveTickAsync(
            BuildFlow(remoteIp: "1.1.1.1", remotePort: 443, bytesOut: 10),
            BuildFlow(remoteIp: "1.1.1.1", remotePort: 443, bytesOut: 10),
            BuildFlow(remoteIp: "2.2.2.2", remotePort: 443, bytesOut: 10));

        var snapshot = Assert.Single(batch);
        Assert.Equal(2, snapshot.ActiveConnectionCount);
    }

    [Fact]
    public async Task BytesOutByCountry_AggregatesCorrectly() {
        await using var fixture = Fixture.Start();

        var us = CountryCode.FromAlpha2("US");
        var de = CountryCode.FromAlpha2("DE");

        var batch = await fixture.DriveTickAsync(
            BuildFlow(bytesOut: 100, country: us),
            BuildFlow(bytesOut: 50, country: de),
            BuildFlow(bytesOut: 25, country: us));

        var snapshot = Assert.Single(batch);
        Assert.Equal(125, snapshot.BytesOutByCountry[us]);
        Assert.Equal(50, snapshot.BytesOutByCountry[de]);
    }

    [Fact]
    public async Task BytesOutByCountry_ResetsEachTick() {
        await using var fixture = Fixture.Start();

        var us = CountryCode.FromAlpha2("US");
        var de = CountryCode.FromAlpha2("DE");

        var firstBatch = await fixture.DriveTickAsync(BuildFlow(bytesOut: 100, country: us));
        var firstSnapshot = Assert.Single(firstBatch);
        Assert.Equal(100, firstSnapshot.BytesOutByCountry[us]);

        var secondBatch = await fixture.DriveTickAsync(BuildFlow(bytesOut: 50, country: de));
        var secondSnapshot = Assert.Single(secondBatch);
        Assert.Equal(50, secondSnapshot.BytesOutByCountry[de]);
        Assert.False(secondSnapshot.BytesOutByCountry.ContainsKey(us));
    }

    [Fact]
    public async Task TotalBytesIn_AccumulatesAcrossTicks() {
        await using var fixture = Fixture.Start();

        var firstBatch = await fixture.DriveTickAsync(BuildFlow(bytesIn: 100));
        var firstSnapshot = Assert.Single(firstBatch);
        Assert.Equal(100, firstSnapshot.DeltaBytesIn);
        Assert.Equal(100, firstSnapshot.TotalBytesIn);

        var secondBatch = await fixture.DriveTickAsync(BuildFlow(bytesIn: 50));
        var secondSnapshot = Assert.Single(secondBatch);
        Assert.Equal(50, secondSnapshot.DeltaBytesIn);
        Assert.Equal(150, secondSnapshot.TotalBytesIn);
    }

    [Fact]
    public async Task MixedInboundOutbound_SingleTick_AggregatesBoth() {
        await using var fixture = Fixture.Start();

        var batch = await fixture.DriveTickAsync(
            BuildFlow(bytesIn: 100),
            BuildFlow(bytesOut: 200));

        var snapshot = Assert.Single(batch);
        Assert.Equal(100, snapshot.DeltaBytesIn);
        Assert.Equal(200, snapshot.DeltaBytesOut);
    }

    [Fact]
    public async Task EmptyTick_ProducesNoBatch() {
        await using var fixture = Fixture.Start();

        fixture.FakeTime.Advance(FlushInterval);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Assert.Empty(fixture.ReceivedBatches);
    }

    [Fact]
    public async Task CancellationToken_StopsTheLoop() {
        var channel = Channel.CreateUnbounded<FlowEvent>();
        var fakeTime = new FakeTimeProvider(StartInstant);
        var engine = new TrafficEngine(
            channel.Reader, fakeTime,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            NullLogger<TrafficEngine>.Instance);
        using var cts = new CancellationTokenSource();

        var runTask = engine.RunAsync(cts.Token);
        cts.Cancel();

        await runTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(runTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task GetCurrentSnapshotsAsync_EmptyEngine_ReturnsEmpty() {
        await using var fixture = Fixture.Start();

        var snapshots = await fixture.Engine.GetCurrentSnapshotsAsync(
            TestContext.Current.CancellationToken);

        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task GetCurrentSnapshotsAsync_AfterTick_ReturnsCurrentState() {
        await using var fixture = Fixture.Start();

        await fixture.DriveTickAsync(BuildFlow(bytesOut: 500));

        var snapshots = await fixture.Engine.GetCurrentSnapshotsAsync(
            TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(500, snapshot.TotalBytesOut);
        Assert.Equal(0, snapshot.DeltaBytesOut);
    }

    [Fact]
    public async Task GetCurrentSnapshotsAsync_IncludesInactiveProcesses() {
        await using var fixture = Fixture.Start();

        await fixture.DriveTickAsync(
            BuildFlow(processName: "a.exe", processPath: @"C:\a.exe", bytesOut: 100));
        await fixture.DriveTickAsync(
            BuildFlow(processName: "b.exe", processPath: @"C:\b.exe", bytesOut: 200));

        var snapshots = await fixture.Engine.GetCurrentSnapshotsAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshots.Count);
        var paths = snapshots.Select(s => s.ProcessPath).ToHashSet();
        Assert.Contains(@"C:\a.exe", paths);
        Assert.Contains(@"C:\b.exe", paths);
    }

    // --- 1-second raw flush / SQLite persistence tests ---

    [Fact]
    public async Task RawFlush_WritesOneBucketPerTickPerDestination() {
        var trafficStore = new FakeTrafficStore();
        await using var fixture = Fixture.Start(trafficStore: trafficStore);

        await fixture.DriveTickAsync(BuildFlow(bytesIn: 100, bytesOut: 50));

        var bucket = Assert.Single(trafficStore.WrittenBuckets);
        Assert.Equal(@"C:\Windows\System32\curl.exe", bucket.ProcessPath);
        Assert.Equal("curl.exe", bucket.ProcessName);
        Assert.Equal(100, bucket.BytesIn);
        Assert.Equal(50, bucket.BytesOut);
        Assert.Equal(1, bucket.BucketSeconds);
    }

    [Fact]
    public async Task RawFlush_MultipleDestinations_WritesOneBucketPerDestinationPerTick() {
        var trafficStore = new FakeTrafficStore();
        await using var fixture = Fixture.Start(trafficStore: trafficStore);

        await fixture.DriveTickAsync(
            BuildFlow(remoteIp: "1.1.1.1", bytesIn: 100, bytesOut: 50),
            BuildFlow(remoteIp: "2.2.2.2", bytesIn: 200, bytesOut: 100));

        Assert.Equal(2, trafficStore.WrittenBuckets.Count);
        var addr1 = trafficStore.WrittenBuckets.Single(b => b.RemoteAddress == "1.1.1.1");
        var addr2 = trafficStore.WrittenBuckets.Single(b => b.RemoteAddress == "2.2.2.2");
        Assert.Equal(100, addr1.BytesIn);
        Assert.Equal(200, addr2.BytesIn);
    }

    [Fact]
    public async Task RawFlush_AcrossMultipleTicks_ProducesOneBucketPerTick() {
        var trafficStore = new FakeTrafficStore();
        await using var fixture = Fixture.Start(trafficStore: trafficStore);

        // Drive three ticks with different bytes each time.
        await fixture.DriveTickAsync(BuildFlow(bytesIn: 100, bytesOut: 50));
        await fixture.DriveTickAsync(BuildFlow(bytesIn: 200, bytesOut: 100));
        await fixture.DriveTickAsync(BuildFlow(bytesIn: 300, bytesOut: 150));

        Assert.Equal(3, trafficStore.WrittenBuckets.Count);
        Assert.Equal(100, trafficStore.WrittenBuckets[0].BytesIn);
        Assert.Equal(200, trafficStore.WrittenBuckets[1].BytesIn);
        Assert.Equal(300, trafficStore.WrittenBuckets[2].BytesIn);
    }

    [Fact]
    public async Task RawFlush_JoinsHostnameFromDnsCache() {
        var dnsCache = new FakeDnsCache();
        dnsCache.Add("1.1.1.1", "one.one.one.one");
        var trafficStore = new FakeTrafficStore();
        await using var fixture = Fixture.Start(trafficStore: trafficStore, dnsCache: dnsCache);

        await fixture.DriveTickAsync(BuildFlow(remoteIp: "1.1.1.1", bytesIn: 100));

        var bucket = Assert.Single(trafficStore.WrittenBuckets);
        Assert.Equal("one.one.one.one", bucket.Hostname);
    }

    [Fact]
    public async Task RawFlush_PersistsDnsCacheEntries() {
        var dnsCache = new FakeDnsCache();
        dnsCache.Add("1.1.1.1", "one.one.one.one");
        var dnsCacheStore = new FakeDnsCacheStore();
        var trafficStore = new FakeTrafficStore();
        await using var fixture = Fixture.Start(
            trafficStore: trafficStore, dnsCacheStore: dnsCacheStore, dnsCache: dnsCache);

        await fixture.DriveTickAsync(BuildFlow(remoteIp: "1.1.1.1", bytesIn: 100));

        Assert.Contains(dnsCacheStore.UpsertedEntries, e =>
            e.Address == "1.1.1.1" && e.Hostname == "one.one.one.one");
    }

    [Fact]
    public async Task RawFlush_NullHostname_NoHostnameInBucket() {
        var trafficStore = new FakeTrafficStore();
        await using var fixture = Fixture.Start(trafficStore: trafficStore);

        await fixture.DriveTickAsync(BuildFlow(remoteIp: "1.1.1.1", bytesIn: 100));

        var bucket = Assert.Single(trafficStore.WrittenBuckets);
        Assert.Null(bucket.Hostname);
    }

    // --- Constructor validation ---

    [Fact]
    public void Constructor_NullReader_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new TrafficEngine(
            reader: null!,
            timeProvider: new FakeTimeProvider(StartInstant),
            trafficStore: new FakeTrafficStore(),
            dnsCacheStore: new FakeDnsCacheStore(),
            dnsCache: new FakeDnsCache(),
            options: new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            logger: NullLogger<TrafficEngine>.Instance));
    }

    [Fact]
    public void Constructor_NullTimeProvider_ThrowsArgumentNullException() {
        var channel = Channel.CreateUnbounded<FlowEvent>();
        Assert.Throws<ArgumentNullException>(() => new TrafficEngine(
            reader: channel.Reader,
            timeProvider: null!,
            trafficStore: new FakeTrafficStore(),
            dnsCacheStore: new FakeDnsCacheStore(),
            dnsCache: new FakeDnsCache(),
            options: new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            logger: NullLogger<TrafficEngine>.Instance));
    }

    [Fact]
    public void Constructor_NullTrafficStore_ThrowsArgumentNullException() {
        var channel = Channel.CreateUnbounded<FlowEvent>();
        Assert.Throws<ArgumentNullException>(() => new TrafficEngine(
            reader: channel.Reader,
            timeProvider: new FakeTimeProvider(StartInstant),
            trafficStore: null!,
            dnsCacheStore: new FakeDnsCacheStore(),
            dnsCache: new FakeDnsCache(),
            options: new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            logger: NullLogger<TrafficEngine>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException() {
        var channel = Channel.CreateUnbounded<FlowEvent>();
        Assert.Throws<ArgumentNullException>(() => new TrafficEngine(
            reader: channel.Reader,
            timeProvider: new FakeTimeProvider(StartInstant),
            trafficStore: new FakeTrafficStore(),
            dnsCacheStore: new FakeDnsCacheStore(),
            dnsCache: new FakeDnsCache(),
            options: new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            logger: null!));
    }

    // --- Helpers ---

    private static FlowEvent BuildFlow(
        string processName = "curl.exe",
        string processPath = @"C:\Windows\System32\curl.exe",
        string remoteIp = "1.1.1.1",
        int remotePort = 443,
        long bytesIn = 0,
        long bytesOut = 0,
        CountryCode? country = null
    ) {
        return new FlowEvent(
            processId: 4242,
            processName: processName,
            processPath: processPath,
            remoteAddress: IPAddress.Parse(remoteIp),
            remotePort: remotePort,
            bytesIn: bytesIn,
            bytesOut: bytesOut,
            country: country ?? CountryCode.FromAlpha2("US"),
            timestamp: StartInstant);
    }

    private sealed class Fixture : IAsyncDisposable {
        private readonly Channel<FlowEvent> _channel;
        private readonly CancellationTokenSource _cts;
        private readonly Task _runTask;

        public FakeTimeProvider FakeTime { get; }
        public TrafficEngine Engine { get; }
        public List<IReadOnlyList<CounterSnapshot>> ReceivedBatches { get; } = new();

        private Fixture(
            Channel<FlowEvent> channel,
            FakeTimeProvider fakeTime,
            TrafficEngine engine
        ) {
            _channel = channel;
            FakeTime = fakeTime;
            Engine = engine;
            _cts = new CancellationTokenSource();

            engine.OnSnapshotBatch += batch => {
                lock (ReceivedBatches) {
                    ReceivedBatches.Add(batch);
                    _pendingBatchTcs?.TrySetResult(batch);
                }
            };
            _runTask = engine.RunAsync(_cts.Token);
        }

        private TaskCompletionSource<IReadOnlyList<CounterSnapshot>>? _pendingBatchTcs;

        public static Fixture Start(
            FakeTrafficStore? trafficStore = null,
            FakeDnsCacheStore? dnsCacheStore = null,
            FakeDnsCache? dnsCache = null
        ) {
            var channel = Channel.CreateUnbounded<FlowEvent>();
            var fakeTime = new FakeTimeProvider(StartInstant);
            var engine = new TrafficEngine(
                channel.Reader, fakeTime,
                trafficStore ?? new FakeTrafficStore(),
                dnsCacheStore ?? new FakeDnsCacheStore(),
                dnsCache ?? new FakeDnsCache(),
                new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
                NullLogger<TrafficEngine>.Instance);
            return new Fixture(channel, fakeTime, engine);
        }

        public async Task<IReadOnlyList<CounterSnapshot>> DriveTickAsync(params FlowEvent[] events) {
            TaskCompletionSource<IReadOnlyList<CounterSnapshot>> batchTcs;
            lock (ReceivedBatches) {
                batchTcs = new TaskCompletionSource<IReadOnlyList<CounterSnapshot>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingBatchTcs = batchTcs;
            }

            foreach (var flowEvent in events) {
                Assert.True(_channel.Writer.TryWrite(flowEvent));
            }

            var waitSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Engine.SetWaitSignal(waitSignal);
            await waitSignal.Task.WaitAsync(TestTimeout);

            var settleSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Engine.SetWaitSignal(settleSignal);

            FakeTime.Advance(FlushInterval);

            var result = await batchTcs.Task.WaitAsync(TestTimeout);
            await settleSignal.Task.WaitAsync(TestTimeout);

            return result;
        }

        public async ValueTask DisposeAsync() {
            _cts.Cancel();
            try {
                await _runTask.WaitAsync(TestTimeout);
            } catch (OperationCanceledException) {
                // Expected on shutdown.
            }
            _cts.Dispose();
        }
    }
}
