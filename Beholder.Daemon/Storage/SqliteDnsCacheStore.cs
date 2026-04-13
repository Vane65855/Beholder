using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IDnsCacheStore"/>. Persists DNS
/// hostname-to-address mappings across daemon restarts so that traffic records
/// written before a DNS response arrived can still be backfilled with hostnames.
/// </summary>
internal sealed class SqliteDnsCacheStore : IDnsCacheStore {
    private readonly ConnectionFactory _connectionFactory;

    public SqliteDnsCacheStore(ConnectionFactory connectionFactory) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task UpsertBatchAsync(
        IReadOnlyList<(string Address, string Hostname)> entries,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;

        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dns_cache (address, hostname, updated_at)
            VALUES ($address, $hostname, $updatedAt)
            ON CONFLICT(address) DO UPDATE SET
                hostname   = excluded.hostname,
                updated_at = excluded.updated_at;
            """;

        var pAddress = command.Parameters.Add("$address", SqliteType.Text);
        var pHostname = command.Parameters.Add("$hostname", SqliteType.Text);
        var pUpdatedAt = command.Parameters.Add("$updatedAt", SqliteType.Integer);

        command.Prepare();

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (address, hostname) in entries) {
            pAddress.Value = address;
            pHostname.Value = hostname;
            pUpdatedAt.Value = nowMs;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ResolveAsync(string address, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hostname FROM dns_cache WHERE address = $address;";
        command.Parameters.AddWithValue("$address", address);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    public async Task<long> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dns_cache WHERE updated_at < $cutoffMs;";
        command.Parameters.AddWithValue("$cutoffMs", cutoff.ToUnixTimeMilliseconds());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
