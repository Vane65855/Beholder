using System.Threading.Channels;
using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class AlertEmitterTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly ConnectionFactory _connectionFactory;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeSnapshotBatchSource _snapshotSource;
    private readonly BroadcastService _broadcaster;
    private readonly FakeTimeProvider _timeProvider;
    private readonly AlertEmitter _emitter;

    public AlertEmitterTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        _eventStore = new SqliteEventStore(_connectionFactory, _timeProvider);
        _snapshotSource = new FakeSnapshotBatchSource();
        _broadcaster = new BroadcastService(
            _snapshotSource, _timeProvider, NullLogger<BroadcastService>.Instance);
        _emitter = new AlertEmitter(
            _eventStore, _broadcaster, _timeProvider,
            NullLogger<AlertEmitter>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task EmitAlertAsync_AppendsChainRow_ReturnsAssignedSeq() {
        // Chain seq is 0-based: SqliteEventStore.ReadLastRowAsync returns
        // (-1, ZeroPrevHash) when the table is empty, so newSeq = 0 for
        // the first row.
        var seq = await _emitter.EmitAlertAsync(
            AlertKind.NewProcess, @"C:\bin\app.exe",
            "app.exe accessed the network for the first time", CancellationToken.None);

        Assert.Equal(0, seq);

        var verify = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.True(verify.IsValid);
        Assert.Equal(1, verify.RowsVerified);

        var entries = await _eventStore.ListByKindsAsync(
            new[] { EventKind.NewProcess }, limit: 10, CancellationToken.None);
        var entry = Assert.Single(entries);
        Assert.Equal(EventKind.NewProcess, entry.Kind);
        Assert.Equal(seq, entry.Seq);

        var decoded = AlertPayloadEncoder.TryDecode(entry.Payload);
        Assert.NotNull(decoded);
        Assert.Equal(@"C:\bin\app.exe", decoded.Value.ProcessPath);
        Assert.Equal("app.exe accessed the network for the first time", decoded.Value.Summary);
    }

    [Fact]
    public async Task EmitAlertAsync_BroadcastsAlertEvent() {
        var ct = TestContext.Current.CancellationToken;
        await _broadcaster.StartAsync(ct);

        await using var enumerator = _broadcaster.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var moveTask = enumerator.MoveNextAsync().AsTask();
        await WaitForAsync(() => _broadcaster.ActiveSubscriberCount == 1, "subscriber registered", ct);

        await _emitter.EmitAlertAsync(
            AlertKind.HashChanged, @"C:\bin\firefox.exe",
            "firefox.exe binary changed (SHA-256 differs from prior abcd1234)", ct);

        Assert.True(await moveTask.WaitAsync(TimeSpan.FromSeconds(2), ct));
        var daemonEvent = enumerator.Current;
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.Alert, daemonEvent.PayloadCase);
        Assert.Equal(Local.AlertKind.HashChanged, daemonEvent.Alert.Alert.Kind);
        Assert.Equal(@"C:\bin\firefox.exe", daemonEvent.Alert.Alert.ProcessPath);

        await _broadcaster.StopAsync(ct);
    }

    [Fact]
    public async Task EmitAlertAsync_AllAlertKinds_MapToCorrectEventKind() {
        var s1 = await _emitter.EmitAlertAsync(
            AlertKind.NewProcess, @"C:\bin\a.exe", "a.exe seen", CancellationToken.None);
        var s2 = await _emitter.EmitAlertAsync(
            AlertKind.HashChanged, @"C:\bin\b.exe", "b.exe changed", CancellationToken.None);
        var s3 = await _emitter.EmitAlertAsync(
            AlertKind.ChainError, "", "chain failed at seq 5", CancellationToken.None);

        var allKinds = new[] { EventKind.NewProcess, EventKind.HashChanged, EventKind.ChainError };
        var entries = await _eventStore.ListByKindsAsync(
            allKinds, limit: 10, CancellationToken.None);
        Assert.Equal(3, entries.Count);
        Assert.Equal(EventKind.ChainError, entries[0].Kind);
        Assert.Equal(EventKind.HashChanged, entries[1].Kind);
        Assert.Equal(EventKind.NewProcess, entries[2].Kind);
        Assert.Equal(s1, entries[2].Seq);
        Assert.Equal(s2, entries[1].Seq);
        Assert.Equal(s3, entries[0].Seq);
    }

    [Fact]
    public async Task EmitAlertAsync_UnknownKind_Throws() {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _emitter.EmitAlertAsync(
            AlertKind.Unknown, "", "should not be reached", CancellationToken.None));
    }

    [Fact]
    public async Task EmitAlertAsync_NullProcessPath_Throws() {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _emitter.EmitAlertAsync(
            AlertKind.NewProcess, null!, "summary", CancellationToken.None));
    }

    [Fact]
    public void Constructor_NullEventStore_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new AlertEmitter(
            eventStore: null!,
            broadcaster: _broadcaster,
            timeProvider: _timeProvider,
            logger: NullLogger<AlertEmitter>.Instance));

    private static async Task WaitForAsync(
        Func<bool> predicate, string description, CancellationToken cancellationToken
    ) {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline) {
            await Task.Delay(10, cancellationToken);
        }
        if (!predicate()) throw new TimeoutException($"Timed out waiting for: {description}");
    }
}
