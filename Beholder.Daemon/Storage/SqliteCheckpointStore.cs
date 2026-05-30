using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed persistence for signed chain checkpoints (Phase 11). One row
/// per <see cref="Checkpoint"/>; the underlying schema was defined in Phase 1
/// but unused until now. Append-only — <see cref="AppendAsync"/> is the only
/// write path, and the PRIMARY KEY constraint on <c>seq</c> guarantees the
/// signer can't accidentally duplicate an anchor.
/// </summary>
internal sealed class SqliteCheckpointStore : ICheckpointStore {
    private readonly ConnectionFactory _connectionFactory;

    public SqliteCheckpointStore(ConnectionFactory connectionFactory) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<Checkpoint?> GetLatestAsync(CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, row_hash, ts_unix_ns, signature, key_id
            FROM checkpoint
            ORDER BY seq DESC
            LIMIT 1;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return MapRow(reader);
    }

    public async Task AppendAsync(Checkpoint checkpoint, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(checkpoint);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        // The PRIMARY KEY on seq makes this fail loudly if the signer's
        // monotonic-seq invariant is ever violated — exactly the failure we
        // want surfaced rather than silently overwritten.
        command.CommandText = """
            INSERT INTO checkpoint (seq, row_hash, ts_unix_ns, signature, key_id)
            VALUES ($seq, $rowHash, $tsUnixNs, $signature, $keyId);
            """;
        command.Parameters.AddWithValue("$seq", checkpoint.Seq);
        command.Parameters.AddWithValue("$rowHash", checkpoint.RowHash);
        command.Parameters.AddWithValue("$tsUnixNs",
            checkpoint.Timestamp.ToUnixTimeMilliseconds() * 1_000_000L);
        command.Parameters.AddWithValue("$signature", checkpoint.Signature);
        command.Parameters.AddWithValue("$keyId", checkpoint.KeyId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Checkpoint MapRow(SqliteDataReader reader) {
        var seq = reader.GetInt64(0);
        var rowHash = (byte[])reader.GetValue(1);
        var timestampUnixNs = reader.GetInt64(2);
        var signature = (byte[])reader.GetValue(3);
        var keyId = reader.GetString(4);
        return new Checkpoint(
            Seq: seq,
            RowHash: rowHash,
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(timestampUnixNs / 1_000_000L),
            Signature: signature,
            KeyId: keyId);
    }
}
