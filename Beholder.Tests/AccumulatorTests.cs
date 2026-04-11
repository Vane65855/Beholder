using System.Net;
using System.Threading.Channels;
using Beholder.Core;
using Beholder.Daemon.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public class AccumulatorTests {
    private static readonly DateTimeOffset StartInstant =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

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
        var accumulator = new Accumulator(channel.Reader, fakeTime, NullLogger<Accumulator>.Instance);
        using var cts = new CancellationTokenSource();

        var runTask = accumulator.RunAsync(cts.Token);
        cts.Cancel();

        await runTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(runTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void Constructor_NullReader_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new Accumulator(
            reader: null!,
            timeProvider: new FakeTimeProvider(StartInstant),
            logger: NullLogger<Accumulator>.Instance));
    }

    [Fact]
    public void Constructor_NullTimeProvider_ThrowsArgumentNullException() {
        var channel = Channel.CreateUnbounded<FlowEvent>();
        Assert.Throws<ArgumentNullException>(() => new Accumulator(
            reader: channel.Reader,
            timeProvider: null!,
            logger: NullLogger<Accumulator>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException() {
        var channel = Channel.CreateUnbounded<FlowEvent>();
        Assert.Throws<ArgumentNullException>(() => new Accumulator(
            reader: channel.Reader,
            timeProvider: new FakeTimeProvider(StartInstant),
            logger: null!));
    }

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
        public List<IReadOnlyList<CounterSnapshot>> ReceivedBatches { get; } = new();

        private Fixture(Channel<FlowEvent> channel, FakeTimeProvider fakeTime, Accumulator accumulator) {
            _channel = channel;
            FakeTime = fakeTime;
            _cts = new CancellationTokenSource();

            accumulator.OnSnapshotBatch += batch => {
                lock (ReceivedBatches) {
                    ReceivedBatches.Add(batch);
                    _pendingBatchTcs?.TrySetResult(batch);
                }
            };
            _runTask = accumulator.RunAsync(_cts.Token);
        }

        private TaskCompletionSource<IReadOnlyList<CounterSnapshot>>? _pendingBatchTcs;

        public static Fixture Start() {
            var channel = Channel.CreateUnbounded<FlowEvent>();
            var fakeTime = new FakeTimeProvider(StartInstant);
            var accumulator = new Accumulator(channel.Reader, fakeTime, NullLogger<Accumulator>.Instance);
            return new Fixture(channel, fakeTime, accumulator);
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

            await Task.Yield();
            await Task.Yield();

            FakeTime.Advance(FlushInterval);

            return await batchTcs.Task.WaitAsync(TestTimeout);
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
