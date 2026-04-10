using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed persistence for Beholder's firewall rules. This is the daemon's
/// memory of which rules it has been told to enforce — distinct from
/// <see cref="IFirewallController"/>, which is the OS-level enforcement surface
/// implemented by the platform projects. The daemon uses both: this store to remember
/// rules across restarts, and the controller to apply them to the live firewall.
/// </summary>
internal sealed class SqliteFirewallRuleStore {
    private readonly ConnectionFactory _connectionFactory;

    public SqliteFirewallRuleStore(ConnectionFactory connectionFactory) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<FirewallRule?> GetByProcessAndDirectionAsync(
        string processPath,
        Direction direction,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, process_path, direction, action, source, created_at, updated_at
            FROM firewall_rules
            WHERE process_path = $processPath AND direction = $direction;
            """;
        command.Parameters.AddWithValue("$processPath", processPath);
        command.Parameters.AddWithValue("$direction", direction.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return MapRow(reader);
    }

    public async Task<IReadOnlyList<FirewallRule>> ListAllAsync(CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, process_path, direction, action, source, created_at, updated_at
            FROM firewall_rules
            ORDER BY id ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rules = new List<FirewallRule>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rules.Add(MapRow(reader));
        return rules;
    }

    public async Task<FirewallRule> UpsertAsync(FirewallRule rule, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(rule);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        // created_at is deliberately omitted from DO UPDATE SET so that the original
        // creation timestamp is preserved across upserts. RETURNING lets us insert-or-
        // update and read back the materialized row in a single round-trip.
        command.CommandText = """
            INSERT INTO firewall_rules
                (process_path, direction, action, source, created_at, updated_at)
            VALUES
                ($processPath, $direction, $action, $source, $createdAt, $updatedAt)
            ON CONFLICT(process_path, direction) DO UPDATE SET
                action     = excluded.action,
                source     = excluded.source,
                updated_at = excluded.updated_at
            RETURNING id, process_path, direction, action, source, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$processPath", rule.ProcessPath);
        command.Parameters.AddWithValue("$direction", rule.Direction.ToString());
        command.Parameters.AddWithValue("$action", rule.Action.ToString());
        command.Parameters.AddWithValue("$source", rule.Source.ToString());
        command.Parameters.AddWithValue("$createdAt", rule.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updatedAt", rule.UpdatedAt.ToUnixTimeMilliseconds());

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return MapRow(reader);
    }

    public async Task<bool> RemoveAsync(string processPath, Direction direction, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM firewall_rules
            WHERE process_path = $processPath AND direction = $direction;
            """;
        command.Parameters.AddWithValue("$processPath", processPath);
        command.Parameters.AddWithValue("$direction", direction.ToString());

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affectedRows > 0;
    }

    private static FirewallRule MapRow(SqliteDataReader reader) {
        return new FirewallRule(
            id: reader.GetInt32(0),
            processPath: reader.GetString(1),
            direction: Enum.Parse<Direction>(reader.GetString(2)),
            action: Enum.Parse<FirewallAction>(reader.GetString(3)),
            source: Enum.Parse<RuleSource>(reader.GetString(4)),
            createdAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            updatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))
        );
    }
}
