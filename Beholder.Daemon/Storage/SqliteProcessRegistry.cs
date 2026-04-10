using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IProcessRegistry"/>. Tracks every binary
/// the daemon has observed on the network, along with its most recent SHA-256 hash.
/// The alert pipeline consults this store to decide whether a binary is appearing on
/// the network for the first time (<c>NewProcess</c> alert) or whether its hash has
/// drifted since the last observation (<c>HashChanged</c> alert).
/// </summary>
internal sealed class SqliteProcessRegistry : IProcessRegistry {
    private readonly ConnectionFactory _connectionFactory;

    public SqliteProcessRegistry(ConnectionFactory connectionFactory) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<ProcessInfo?> GetByPathAsync(string path, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, display_name, sha256, first_seen, last_seen, last_hash_at
            FROM process_registry
            WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", path);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return MapRow(reader);
    }

    public async Task RegisterAsync(ProcessInfo info, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(info);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        // first_seen is deliberately omitted from DO UPDATE SET. The first time a
        // binary was observed is a historical fact — it never changes. Matches the
        // SqliteFirewallRuleStore.UpsertAsync pattern for created_at.
        command.CommandText = """
            INSERT INTO process_registry
                (path, display_name, sha256, first_seen, last_seen, last_hash_at)
            VALUES
                ($path, $displayName, $sha256, $firstSeen, $lastSeen, $lastHashAt)
            ON CONFLICT(path) DO UPDATE SET
                display_name = excluded.display_name,
                sha256       = excluded.sha256,
                last_seen    = excluded.last_seen,
                last_hash_at = excluded.last_hash_at;
            """;
        command.Parameters.AddWithValue("$path", info.Path);
        command.Parameters.AddWithValue("$displayName", info.DisplayName);
        command.Parameters.AddWithValue("$sha256", (object?)info.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$firstSeen", info.FirstSeen.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$lastSeen", info.LastSeen.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$lastHashAt",
            info.LastHashedAt.HasValue ? info.LastHashedAt.Value.ToUnixTimeMilliseconds() : (object)DBNull.Value
        );

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProcessInfo>> ListAllAsync(CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, display_name, sha256, first_seen, last_seen, last_hash_at
            FROM process_registry
            ORDER BY last_seen DESC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var processes = new List<ProcessInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) processes.Add(MapRow(reader));
        return processes;
    }

    private static ProcessInfo MapRow(SqliteDataReader reader) {
        return new ProcessInfo(
            path: reader.GetString(0),
            displayName: reader.GetString(1),
            sha256: reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2),
            firstSeen: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            lastSeen: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            lastHashedAt: reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))
        );
    }
}
