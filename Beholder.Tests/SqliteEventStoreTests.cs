using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public sealed class SqliteEventStoreTests : IDisposable {
    private static readonly DateTimeOffset FixedTime = new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly long ExpectedTs = FixedTime.ToUnixTimeMilliseconds() * 1_000_000L;

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly ConnectionFactory _connectionFactory;
    private readonly SqliteEventStore _store;

    public SqliteEventStoreTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        _store = new SqliteEventStore(_connectionFactory, new FixedTimeProvider(FixedTime));
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_NullConnectionFactory_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => new SqliteEventStore(null!, TimeProvider.System));
    }

    [Fact]
    public void Constructor_NullTimeProvider_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => new SqliteEventStore(_connectionFactory, null!));
    }

    [Fact]
    public async Task AppendAsync_SingleEvent_InsertsRowWithValidChain() {
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        await _store.AppendAsync(EventKind.Counter, payload, CancellationToken.None);

        var rows = ReadAllRows();
        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(0L, row.Seq);
        Assert.Equal(ExpectedTs, row.TimestampUnixNs);
        Assert.Equal("Counter", row.Kind);
        Assert.Equal(payload, row.Payload);
        Assert.Equal(ChainHasher.ZeroPrevHash, row.PrevHash);

        var expectedHash = ChainHasher.ComputeRowHash(0L, ExpectedTs, EventKind.Counter, payload, ChainHasher.ZeroPrevHash);
        Assert.Equal(expectedHash, row.RowHash);
    }

    [Fact]
    public async Task AppendAsync_ThreeEvents_ChainLinksCorrectly() {
        await _store.AppendAsync(EventKind.Counter, new byte[] { 0xAA }, CancellationToken.None);
        await _store.AppendAsync(EventKind.NewProcess, new byte[] { 0xBB, 0xCC }, CancellationToken.None);
        await _store.AppendAsync(EventKind.HashChanged, new byte[] { 0xDD, 0xEE, 0xFF }, CancellationToken.None);

        var rows = ReadAllRows();
        Assert.Equal(3, rows.Count);
        Assert.Equal(ChainHasher.ZeroPrevHash, rows[0].PrevHash);
        Assert.Equal(rows[0].RowHash, rows[1].PrevHash);
        Assert.Equal(rows[1].RowHash, rows[2].PrevHash);
    }

    [Fact]
    public async Task VerifyAsync_ValidChain_ReturnsSuccess() {
        for (var i = 0; i < 5; i++) {
            await _store.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }

        var result = await _store.VerifyAsync(CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(5L, result.RowsVerified);
        Assert.Null(result.FailedAtSeq);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_EmptyDatabase_ReturnsSuccessWithZeroRows() {
        var result = await _store.VerifyAsync(CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(0L, result.RowsVerified);
        Assert.Null(result.FailedAtSeq);
    }

    [Fact]
    public async Task VerifyAsync_CorruptedRowHash_ReturnsFailure() {
        for (var i = 0; i < 3; i++) {
            await _store.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }

        CorruptColumn(seq: 1L, column: "row_hash");

        var result = await _store.VerifyAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(1L, result.FailedAtSeq);
        Assert.Equal(1L, result.RowsVerified);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("row_hash mismatch", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_CorruptedPrevHash_ReturnsFailure() {
        for (var i = 0; i < 3; i++) {
            await _store.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }

        CorruptColumn(seq: 2L, column: "prev_hash");

        var result = await _store.VerifyAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(2L, result.FailedAtSeq);
        Assert.Equal(2L, result.RowsVerified);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("prev_hash mismatch", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_CorruptedPayload_ReturnsFailure() {
        for (var i = 0; i < 3; i++) {
            await _store.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }

        CorruptColumn(seq: 1L, column: "payload");

        var result = await _store.VerifyAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(1L, result.FailedAtSeq);
        Assert.Equal(1L, result.RowsVerified);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("row_hash mismatch", result.ErrorMessage);
    }

    [Fact]
    public async Task AppendAsync_ConcurrentAppends_AreSerializedCorrectly() {
        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++) {
            var payload = new byte[] { (byte)i };
            tasks.Add(_store.AppendAsync(EventKind.Counter, payload, CancellationToken.None));
        }
        await Task.WhenAll(tasks);

        var verify = await _store.VerifyAsync(CancellationToken.None);
        Assert.True(verify.IsValid);
        Assert.Equal(100L, verify.RowsVerified);

        var rows = ReadAllRows();
        Assert.Equal(100, rows.Count);
        for (var i = 0; i < 100; i++) {
            Assert.Equal((long)i, rows[i].Seq);
        }
    }

    [Fact]
    public async Task AppendAsync_UsesTimeProvider_NotSystemClock() {
        await _store.AppendAsync(EventKind.Counter, new byte[] { 0x42 }, CancellationToken.None);

        var rows = ReadAllRows();
        Assert.Single(rows);
        Assert.Equal(ExpectedTs, rows[0].TimestampUnixNs);
    }

    [Fact]
    public async Task TryGetChainHeadAsync_EmptyChain_ReturnsNull() {
        var head = await _store.TryGetChainHeadAsync(CancellationToken.None);
        Assert.Null(head);
    }

    [Fact]
    public async Task TryGetChainHeadAsync_PopulatedChain_ReturnsLatestRow() {
        for (var i = 0; i < 3; i++) {
            await _store.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }

        var head = await _store.TryGetChainHeadAsync(CancellationToken.None);

        Assert.NotNull(head);
        Assert.Equal(2L, head.Seq);
        Assert.Equal(FixedTime.ToUnixTimeMilliseconds(), head.Timestamp.ToUnixTimeMilliseconds());
        var rows = ReadAllRows();
        Assert.Equal(rows[^1].RowHash, head.RowHash);
    }

    [Fact]
    public async Task TryGetChainHeadAsync_SingleRow_ReturnsThatRow() {
        var payload = new byte[] { 0xAB, 0xCD };
        await _store.AppendAsync(EventKind.NewProcess, payload, CancellationToken.None);

        var head = await _store.TryGetChainHeadAsync(CancellationToken.None);

        Assert.NotNull(head);
        Assert.Equal(0L, head.Seq);
        var expectedHash = ChainHasher.ComputeRowHash(
            0L, ExpectedTs, EventKind.NewProcess, payload, ChainHasher.ZeroPrevHash);
        Assert.Equal(expectedHash, head.RowHash);
    }

    [Fact]
    public async Task VerifyFromAsync_FromGenesis_EqualsFullVerify() {
        for (var i = 0; i < 4; i++) {
            await _store.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }

        var result = await _store.VerifyFromAsync(0, ChainHasher.ZeroPrevHash, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(4, result.RowsVerified);
    }

    [Fact]
    public async Task VerifyFromAsync_PartialWalk_VerifiesSubsetFromAnchor() {
        for (var i = 0; i < 5; i++) {
            await _store.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }
        var anchorRowHash = (await _store.TryGetRowHashAsync(2, CancellationToken.None))!;

        var result = await _store.VerifyFromAsync(3, anchorRowHash, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.RowsVerified);   // only seqs 3 and 4
    }

    [Fact]
    public async Task VerifyFromAsync_WrongExpectedPrev_FailsAtFirstWalkedRow() {
        for (var i = 0; i < 5; i++) {
            await _store.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }
        var wrongPrev = new byte[ChainHasher.HashSize];
        Array.Fill(wrongPrev, (byte)0x99);

        var result = await _store.VerifyFromAsync(3, wrongPrev, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FailedAtSeq);
        Assert.Contains("prev_hash mismatch", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyFromAsync_WrongLengthExpectedPrev_Throws() {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.VerifyFromAsync(0, new byte[8], CancellationToken.None));
    }

    [Fact]
    public async Task TryGetRowHashAsync_ExistingSeq_ReturnsStoredHash() {
        var payload = new byte[] { 0x11, 0x22 };
        await _store.AppendAsync(EventKind.Counter, payload, CancellationToken.None);

        var rowHash = await _store.TryGetRowHashAsync(0, CancellationToken.None);

        Assert.NotNull(rowHash);
        var expected = ChainHasher.ComputeRowHash(
            0L, ExpectedTs, EventKind.Counter, payload, ChainHasher.ZeroPrevHash);
        Assert.Equal(expected, rowHash);
    }

    [Fact]
    public async Task TryGetRowHashAsync_AbsentSeq_ReturnsNull() {
        await _store.AppendAsync(EventKind.Counter, new byte[] { 0x01 }, CancellationToken.None);

        var rowHash = await _store.TryGetRowHashAsync(999, CancellationToken.None);

        Assert.Null(rowHash);
    }

    private IReadOnlyList<EventRow> ReadAllRows() {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, ts_unix_ns, kind, payload, prev_hash, row_hash
            FROM event_log
            ORDER BY seq ASC;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<EventRow>();
        while (reader.Read()) {
            rows.Add(new EventRow(
                Seq: reader.GetInt64(0),
                TimestampUnixNs: reader.GetInt64(1),
                Kind: reader.GetString(2),
                Payload: (byte[])reader.GetValue(3),
                PrevHash: (byte[])reader.GetValue(4),
                RowHash: (byte[])reader.GetValue(5)
            ));
        }
        return rows;
    }

    private void CorruptColumn(long seq, string column) {
        // Whitelist column names — this helper is only ever called from test code, but
        // string interpolation into SQL warrants a guard regardless.
        if (column != "row_hash" && column != "prev_hash" && column != "payload") {
            throw new ArgumentException($"Unsupported column for corruption: {column}", nameof(column));
        }

        byte[] garbage;
        if (column == "payload") {
            garbage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        } else {
            garbage = new byte[ChainHasher.HashSize];
            Array.Fill(garbage, (byte)0xEE);
        }

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE event_log SET {column} = $bytes WHERE seq = $seq;";
        command.Parameters.AddWithValue("$bytes", garbage);
        command.Parameters.AddWithValue("$seq", seq);
        command.ExecuteNonQuery();
    }

    private sealed class FixedTimeProvider : TimeProvider {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed record EventRow(
        long Seq,
        long TimestampUnixNs,
        string Kind,
        byte[] Payload,
        byte[] PrevHash,
        byte[] RowHash
    );
}
