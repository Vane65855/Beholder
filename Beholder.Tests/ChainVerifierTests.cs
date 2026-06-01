using Beholder.Core;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

/// <summary>
/// Exercises the Phase 11.2 checkpoint-anchored verification decision tree in
/// <see cref="ChainVerifier"/>. Uses a real <see cref="SqliteEventStore"/> so
/// the chain walk is faithful, a <see cref="FakeCheckpointStore"/> to seed
/// anchors, and a <see cref="FakeCheckpointKeyProvider"/> (real NSec Ed25519)
/// so signature checks exercise genuine crypto.
/// </summary>
public sealed class ChainVerifierTests : IDisposable {
    private static readonly DateTimeOffset FixedTime = new(2026, 5, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly ConnectionFactory _connectionFactory;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeCheckpointStore _checkpointStore;
    private readonly FakeCheckpointKeyProvider _keyProvider;
    private readonly ChainVerifier _verifier;

    public ChainVerifierTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        _eventStore = new SqliteEventStore(_connectionFactory, new FixedTimeProvider(FixedTime));
        _checkpointStore = new FakeCheckpointStore();
        _keyProvider = new FakeCheckpointKeyProvider();
        _verifier = new ChainVerifier(
            _eventStore, _checkpointStore, _keyProvider, NullLogger<ChainVerifier>.Instance);
    }

    public void Dispose() {
        _keyProvider.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_NullDependencies_Throw() {
        Assert.Throws<ArgumentNullException>(() => new ChainVerifier(
            null!, _checkpointStore, _keyProvider, NullLogger<ChainVerifier>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ChainVerifier(
            _eventStore, null!, _keyProvider, NullLogger<ChainVerifier>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ChainVerifier(
            _eventStore, _checkpointStore, null!, NullLogger<ChainVerifier>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ChainVerifier(
            _eventStore, _checkpointStore, _keyProvider, null!));
    }

    [Fact]
    public async Task VerifyAsync_ForceFull_WalksFromGenesis_NoAnchor() {
        await AppendEventsAsync(5);
        _checkpointStore.Seed(await SignValidCheckpointAtAsync(2));

        var result = await _verifier.VerifyAsync(forceFull: true, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(5, result.RowsVerified);
        Assert.Null(result.AnchorSeq);
        Assert.Null(result.AnchorKeyId);
    }

    [Fact]
    public async Task VerifyAsync_NoCheckpoint_FullWalk() {
        await AppendEventsAsync(3);

        var result = await _verifier.VerifyAsync(forceFull: false, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.RowsVerified);
        Assert.Null(result.AnchorSeq);
    }

    [Fact]
    public async Task VerifyAsync_InvalidSignature_FallsBackToFullWalk() {
        await AppendEventsAsync(5);
        var rowHash = (await _eventStore.TryGetRowHashAsync(2, CancellationToken.None))!;
        var badSignature = new byte[64];
        Array.Fill(badSignature, (byte)0x01);
        _checkpointStore.Seed(new Checkpoint(2, rowHash, FixedTime, badSignature, _keyProvider.KeyId));

        var result = await _verifier.VerifyAsync(forceFull: false, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(5, result.RowsVerified);   // full walk, not anchored
        Assert.Null(result.AnchorSeq);
    }

    [Fact]
    public async Task VerifyAsync_CheckpointSeqNotInChain_FallsBackToFullWalk() {
        await AppendEventsAsync(3);
        var phantomHash = new byte[ChainHasher.HashSize];
        Array.Fill(phantomHash, (byte)0x7A);
        _checkpointStore.Seed(await SignCheckpointAsync(seq: 9999, rowHash: phantomHash));

        var result = await _verifier.VerifyAsync(forceFull: false, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.RowsVerified);
        Assert.Null(result.AnchorSeq);
    }

    [Fact]
    public async Task VerifyAsync_ValidSignatureButRowHashMismatch_ReportsFailure() {
        // Cascaded-rewrite detection: the signature is authentic (the daemon
        // signed this seq), but the live row at that seq no longer hashes to
        // the signed value. Must FAIL, not silently fall back to a full walk
        // (which would pass a fully-rewritten internally-consistent chain).
        await AppendEventsAsync(5);
        var fakeRowHash = new byte[ChainHasher.HashSize];
        Array.Fill(fakeRowHash, (byte)0xAB);
        _checkpointStore.Seed(await SignCheckpointAsync(seq: 2, rowHash: fakeRowHash));

        var result = await _verifier.VerifyAsync(forceFull: false, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.FailedAtSeq);
        Assert.Contains("checkpoint row_hash mismatch", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_ValidAnchor_ReportsFullChainLength_WithAnchorMetadata() {
        await AppendEventsAsync(5);   // seqs 0..4
        _checkpointStore.Seed(await SignValidCheckpointAtAsync(2));

        var result = await _verifier.VerifyAsync(forceFull: false, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.AnchorSeq);
        Assert.Equal(_keyProvider.KeyId, result.AnchorKeyId);
        // 3 rows attested by the signature (seqs 0,1,2) + 2 re-hashed forward
        // (seqs 3,4) = the full 5-row chain.
        Assert.Equal(5, result.RowsVerified);
    }

    [Fact]
    public async Task VerifyAsync_AnchorAtHead_ReportsFullChainLength_IsValid() {
        await AppendEventsAsync(3);   // seqs 0..2, head = 2, anchor = 2 (at head)

        _checkpointStore.Seed(await SignValidCheckpointAtAsync(2));

        var result = await _verifier.VerifyAsync(forceFull: false, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.AnchorSeq);
        // Zero rows walked forward, but the signature attests all 3 — the user
        // sees the real chain length, not 0.
        Assert.Equal(3, result.RowsVerified);
    }

    [Fact]
    public async Task VerifyAsync_PostAnchorTampering_DetectedByForwardWalk() {
        await AppendEventsAsync(5);
        _checkpointStore.Seed(await SignValidCheckpointAtAsync(2));
        CorruptRowHash(seq: 4);

        var result = await _verifier.VerifyAsync(forceFull: false, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.FailedAtSeq);
        Assert.Contains("row_hash mismatch", result.ErrorMessage);
    }

    private async Task AppendEventsAsync(int count) {
        for (var i = 0; i < count; i++) {
            await _eventStore.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }
    }

    private async Task<Checkpoint> SignValidCheckpointAtAsync(long seq) {
        var rowHash = (await _eventStore.TryGetRowHashAsync(seq, CancellationToken.None))!;
        return await SignCheckpointAsync(seq, rowHash);
    }

    private Task<Checkpoint> SignCheckpointAsync(long seq, byte[] rowHash) {
        var payload = CheckpointSignaturePayload.Build(
            seq, rowHash, FixedTime.ToUnixTimeMilliseconds() * 1_000_000L);
        var signature = _keyProvider.Sign(payload);
        return Task.FromResult(new Checkpoint(seq, rowHash, FixedTime, signature, _keyProvider.KeyId));
    }

    private void CorruptRowHash(long seq) {
        var garbage = new byte[ChainHasher.HashSize];
        Array.Fill(garbage, (byte)0xEE);
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE event_log SET row_hash = $bytes WHERE seq = $seq;";
        command.Parameters.AddWithValue("$bytes", garbage);
        command.Parameters.AddWithValue("$seq", seq);
        command.ExecuteNonQuery();
    }

    private sealed class FixedTimeProvider : TimeProvider {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
