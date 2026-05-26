using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="ISettingsOverridesStore"/>.
/// Persists user-mutated Settings values to the <c>settings_overrides</c>
/// table; reads them back at daemon startup via
/// <see cref="ListAllAsync"/>. Each row is one setting (keyed on dotted
/// section name) with a JSON value and an <c>updated_at</c> timestamp.
/// </summary>
/// <remarks>
/// The store is type-agnostic — values are JSON strings the caller serializes
/// and deserializes itself. Today that's "true" / "false" for booleans;
/// future sub-phases will store integers (slider values), enum names
/// (retention preset), or strings without schema migration.
/// </remarks>
internal sealed class SqliteSettingsOverridesStore : ISettingsOverridesStore {
    private readonly ConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteSettingsOverridesStore(
        ConnectionFactory connectionFactory,
        TimeProvider timeProvider
    ) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value_json FROM settings_overrides
            WHERE name = $name;
            """;
        command.Parameters.AddWithValue("$name", name);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : (string)result;
    }

    public async Task UpsertAsync(string name, string valueJson, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(valueJson);

        var nowUnixNs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings_overrides (name, value_json, updated_at)
            VALUES ($name, $valueJson, $updatedAt)
            ON CONFLICT(name) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$valueJson", valueJson);
        command.Parameters.AddWithValue("$updatedAt", nowUnixNs);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> ListAllAsync(
        CancellationToken cancellationToken
    ) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, value_json FROM settings_overrides;";

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }
}
