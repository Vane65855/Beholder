using System.Buffers.Binary;
using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public sealed class CheckpointSignerServiceTests {
    private static readonly DateTimeOffset FixedTime = new(2026, 5, 28, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task SignOnceAsync_EmptyChain_DoesNotAppend() {
        using var fixture = new Fixture();

        await fixture.Service.SignOnceAsync(CancellationToken.None);

        Assert.Empty(fixture.CheckpointStore.Appended);
    }

    [Fact]
    public async Task SignOnceAsync_PopulatedChain_AppendsCheckpointWithValidSignature() {
        using var fixture = new Fixture();
        await fixture.EventStore.AppendAsync(
            EventKind.Counter, new byte[] { 0x01 }, CancellationToken.None);

        await fixture.Service.SignOnceAsync(CancellationToken.None);

        var checkpoint = Assert.Single(fixture.CheckpointStore.Appended);
        Assert.Equal(0L, checkpoint.Seq);
        Assert.Equal(fixture.KeyProvider.KeyId, checkpoint.KeyId);
        Assert.Equal(64, checkpoint.Signature.Length);

        // Reconstruct the signed payload and verify against the key's pubkey.
        var head = (await fixture.EventStore.TryGetChainHeadAsync(CancellationToken.None))!;
        var signedPayload = BuildExpectedPayload(head);
        Assert.True(fixture.KeyProvider.Verify(signedPayload, checkpoint.Signature));
    }

    [Fact]
    public async Task SignOnceAsync_SecondTickSameSeq_DoesNotAppend() {
        using var fixture = new Fixture();
        await fixture.EventStore.AppendAsync(
            EventKind.Counter, new byte[] { 0x01 }, CancellationToken.None);
        await fixture.Service.SignOnceAsync(CancellationToken.None);
        Assert.Single(fixture.CheckpointStore.Appended);

        await fixture.Service.SignOnceAsync(CancellationToken.None);

        Assert.Single(fixture.CheckpointStore.Appended);
    }

    [Fact]
    public async Task SignOnceAsync_ChainAdvances_AppendsNewCheckpoint() {
        using var fixture = new Fixture();
        await fixture.EventStore.AppendAsync(
            EventKind.Counter, new byte[] { 0x01 }, CancellationToken.None);
        await fixture.Service.SignOnceAsync(CancellationToken.None);

        await fixture.EventStore.AppendAsync(
            EventKind.NewProcess, new byte[] { 0x02 }, CancellationToken.None);
        await fixture.Service.SignOnceAsync(CancellationToken.None);

        Assert.Equal(2, fixture.CheckpointStore.Appended.Count);
        Assert.Equal(0L, fixture.CheckpointStore.Appended[0].Seq);
        Assert.Equal(1L, fixture.CheckpointStore.Appended[1].Seq);
    }

    [Fact]
    public async Task SignOnceAsync_AppendFails_LogsAndRecoversOnNextTick() {
        using var fixture = new Fixture();
        await fixture.EventStore.AppendAsync(
            EventKind.Counter, new byte[] { 0x01 }, CancellationToken.None);
        fixture.CheckpointStore.AppendException = new InvalidOperationException("boom");

        await fixture.Service.SignOnceAsync(CancellationToken.None);
        Assert.Empty(fixture.CheckpointStore.Appended);

        // Clear the throw and try again — the next tick succeeds.
        fixture.CheckpointStore.AppendException = null;
        await fixture.Service.SignOnceAsync(CancellationToken.None);
        Assert.Single(fixture.CheckpointStore.Appended);
    }

    [Fact]
    public async Task Constructor_NullDependencies_Throw() {
        using var keyProvider = new FakeCheckpointKeyProvider();
        var eventStore = new FakeEventStore();
        var checkpointStore = new FakeCheckpointStore();
        var options = new FakeOptionsMonitor<CheckpointOptions>(new CheckpointOptions());
        var logger = NullLogger<CheckpointSignerService>.Instance;
        var time = TimeProvider.System;

        Assert.Throws<ArgumentNullException>(() =>
            new CheckpointSignerService(null!, checkpointStore, keyProvider, options, time, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new CheckpointSignerService(eventStore, null!, keyProvider, options, time, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new CheckpointSignerService(eventStore, checkpointStore, null!, options, time, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new CheckpointSignerService(eventStore, checkpointStore, keyProvider, null!, time, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new CheckpointSignerService(eventStore, checkpointStore, keyProvider, options, null!, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new CheckpointSignerService(eventStore, checkpointStore, keyProvider, options, time, null!));
    }

    private static byte[] BuildExpectedPayload(ChainHead head) {
        var buffer = new byte[8 + ChainHasher.HashSize + 8];
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(0, 8), head.Seq);
        head.RowHash.AsSpan().CopyTo(buffer.AsSpan(8, ChainHasher.HashSize));
        BinaryPrimitives.WriteInt64BigEndian(
            buffer.AsSpan(8 + ChainHasher.HashSize, 8),
            head.Timestamp.ToUnixTimeMilliseconds() * 1_000_000L);
        return buffer;
    }

    /// <summary>
    /// Test fixture wiring the signer to in-memory stores + a fake key
    /// provider backed by a real Ed25519 keypair (NSec). The fixture's
    /// EventStore uses a real SqliteEventStore so head/seq behavior matches
    /// production; checkpoints land in the FakeCheckpointStore for inspection.
    /// </summary>
    private sealed class Fixture : IDisposable {
        private readonly string _tempDir;
        private readonly Beholder.Daemon.Storage.ConnectionFactory _connectionFactory;

        public Beholder.Daemon.Storage.SqliteEventStore EventStore { get; }
        public FakeCheckpointStore CheckpointStore { get; }
        public FakeCheckpointKeyProvider KeyProvider { get; }
        public CheckpointSignerService Service { get; }

        public Fixture() {
            _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
            var databasePath = Path.Combine(_tempDir, "beholder.db");
            new Beholder.Daemon.Storage.DatabaseInitializer(databasePath, pooling: false).Initialize();
            _connectionFactory = new Beholder.Daemon.Storage.ConnectionFactory(databasePath, pooling: false);
            EventStore = new Beholder.Daemon.Storage.SqliteEventStore(
                _connectionFactory, new FixedTimeProvider(FixedTime));
            CheckpointStore = new FakeCheckpointStore();
            KeyProvider = new FakeCheckpointKeyProvider();
            var options = new FakeOptionsMonitor<CheckpointOptions>(new CheckpointOptions());
            Service = new CheckpointSignerService(
                EventStore,
                CheckpointStore,
                KeyProvider,
                options,
                new FixedTimeProvider(FixedTime),
                NullLogger<CheckpointSignerService>.Instance);
        }

        public void Dispose() {
            KeyProvider.Dispose();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
