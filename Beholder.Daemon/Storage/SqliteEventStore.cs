using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IEventStore"/>. Each append reads the
/// current chain head, computes the new row's SHA-256 hash via <see cref="ChainHasher"/>,
/// and writes the row inside a transaction. Verification walks the entire chain in
/// sequence order, recomputing every row's hash and confirming that each row's stored
/// <c>prev_hash</c> matches its predecessor's <c>row_hash</c>.
///
/// In-process appends are serialized through a <see cref="SemaphoreSlim"/>: SQLite's
/// WAL mode would otherwise let two callers read the same chain head before either
/// commits, producing rows whose <c>prev_hash</c> values do not actually link.
/// Verification holds no lock — WAL gives readers a stable snapshot of the database
/// for the duration of their query, so a concurrent append cannot corrupt a verify pass.
/// </summary>
internal sealed class SqliteEventStore : IEventStore {
    private readonly ConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Constructs a store that opens fresh connections via
    /// <paramref name="connectionFactory"/> and stamps each event with the current time
    /// from <paramref name="timeProvider"/>.
    /// </summary>
    public SqliteEventStore(ConnectionFactory connectionFactory, TimeProvider timeProvider) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<long> AppendAsync(EventKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            using var connection = _connectionFactory.CreateConnection();
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var (lastSeq, prevHash) = await ReadLastRowAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var newSeq = lastSeq + 1;
            var timestampUnixNs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            var rowHash = ChainHasher.ComputeRowHash(newSeq, timestampUnixNs, kind, payload.Span, prevHash);

            await InsertRowAsync(connection, transaction, newSeq, timestampUnixNs, kind, payload, prevHash, rowHash, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return newSeq;
        } finally {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken) =>
        VerifyFromAsync(0, ChainHasher.ZeroPrevHash, cancellationToken);

    /// <inheritdoc />
    public async Task<ChainVerificationResult> VerifyFromAsync(
        long fromSeq, byte[] expectedPrevHash, CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(expectedPrevHash);
        if (expectedPrevHash.Length != ChainHasher.HashSize) {
            throw new ArgumentException(
                $"expectedPrevHash must be exactly {ChainHasher.HashSize} bytes.", nameof(expectedPrevHash));
        }

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, ts_unix_ns, kind, payload, prev_hash, row_hash
            FROM event_log
            WHERE seq >= $fromSeq
            ORDER BY seq ASC;
            """;
        command.Parameters.AddWithValue("$fromSeq", fromSeq);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        byte[] expectedPrev = expectedPrevHash;
        var rowsVerified = 0L;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            var seq = reader.GetInt64(0);
            var timestampUnixNs = reader.GetInt64(1);
            var kind = Enum.Parse<EventKind>(reader.GetString(2));
            var payload = (byte[])reader.GetValue(3);
            var storedPrev = (byte[])reader.GetValue(4);
            var storedRowHash = (byte[])reader.GetValue(5);

            if (!storedPrev.AsSpan().SequenceEqual(expectedPrev)) {
                return ChainVerificationResult.Failure(rowsVerified, seq, $"prev_hash mismatch at seq {seq}");
            }

            if (!ChainHasher.Verify(seq, timestampUnixNs, kind, payload, expectedPrev, storedRowHash)) {
                return ChainVerificationResult.Failure(rowsVerified, seq, $"row_hash mismatch at seq {seq}");
            }

            expectedPrev = storedRowHash;
            rowsVerified++;
        }

        return ChainVerificationResult.Success(rowsVerified);
    }

    /// <inheritdoc />
    public async Task<byte[]?> TryGetRowHashAsync(long seq, CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_hash FROM event_log WHERE seq = $seq LIMIT 1;";
        command.Parameters.AddWithValue("$seq", seq);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return (byte[])reader.GetValue(0);
    }

    private static async Task<(long LastSeq, byte[] PrevHash)> ReadLastRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT seq, row_hash FROM event_log ORDER BY seq DESC LIMIT 1;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            return (-1L, ChainHasher.ZeroPrevHash);
        }

        var lastSeq = reader.GetInt64(0);
        var rowHash = (byte[])reader.GetValue(1);
        return (lastSeq, rowHash);
    }

    /// <inheritdoc />
    public async Task<ChainHead?> TryGetChainHeadAsync(CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        // No transaction: this is a side-effect-free read, used by the Phase 11
        // checkpoint signer to know what to attest. WAL gives readers a stable
        // snapshot for the duration of the query, so a concurrent AppendAsync
        // can't tear the result.
        command.CommandText = "SELECT seq, row_hash, ts_unix_ns FROM event_log ORDER BY seq DESC LIMIT 1;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        var seq = reader.GetInt64(0);
        var rowHash = (byte[])reader.GetValue(1);
        var timestampUnixNs = reader.GetInt64(2);
        return new ChainHead(
            seq,
            rowHash,
            DateTimeOffset.FromUnixTimeMilliseconds(timestampUnixNs / 1_000_000L));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventLogEntry>> ListByKindsAsync(
        IReadOnlyCollection<EventKind> kinds, int limit, CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(kinds);
        if (limit <= 0 || kinds.Count == 0) return Array.Empty<EventLogEntry>();

        // SQLite has no native list parameter; build a comma-separated set of
        // named parameters. Kind names come from the EventKind enum and are
        // therefore safe to include literally — but we still bind via
        // parameters to keep the schema-injection surface zero.
        var paramNames = new string[kinds.Count];
        var i = 0;
        foreach (var _ in kinds) {
            paramNames[i] = $"$k{i}";
            i++;
        }
        var inClause = string.Join(",", paramNames);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT seq, ts_unix_ns, kind, payload
            FROM event_log
            WHERE kind IN ({inClause})
            ORDER BY seq DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        i = 0;
        foreach (var kind in kinds) {
            command.Parameters.AddWithValue(paramNames[i], kind.ToString());
            i++;
        }

        var results = new List<EventLogEntry>(Math.Min(limit, 64));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            var seq = reader.GetInt64(0);
            var timestampUnixNs = reader.GetInt64(1);
            var kind = Enum.Parse<EventKind>(reader.GetString(2));
            var payload = (byte[])reader.GetValue(3);
            results.Add(new EventLogEntry(
                seq,
                kind,
                DateTimeOffset.FromUnixTimeMilliseconds(timestampUnixNs / 1_000_000L),
                payload));
        }
        return results;
    }

    private static async Task InsertRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long seq,
        long timestampUnixNs,
        EventKind kind,
        ReadOnlyMemory<byte> payload,
        byte[] prevHash,
        byte[] rowHash,
        CancellationToken cancellationToken
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO event_log (seq, ts_unix_ns, kind, payload, prev_hash, row_hash)
            VALUES ($seq, $ts, $kind, $payload, $prev, $row);
            """;
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$ts", timestampUnixNs);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$payload", payload.ToArray());
        command.Parameters.AddWithValue("$prev", prevHash);
        command.Parameters.AddWithValue("$row", rowHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
