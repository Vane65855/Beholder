using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed persistence for Phase 13.6's manual application-identity
/// rules. Backs <see cref="IAppIdentityRuleStore"/>. Same shape as
/// <see cref="SqliteFirewallRuleStore"/>: <c>INSERT … ON CONFLICT … DO
/// NOTHING … RETURNING</c> for the add path; idempotent remove; ID-ordered
/// list.
/// </summary>
internal sealed class SqliteAppIdentityRuleStore : IAppIdentityRuleStore {
    private readonly ConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteAppIdentityRuleStore(
        ConnectionFactory connectionFactory,
        TimeProvider timeProvider
    ) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public async Task<AppIdentityRule?> AddAsync(
        string anchorPath, string filename, string? displayName,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);

        // Normalize: strip trailing separator so callers don't accidentally
        // create duplicate-looking rules (`…\Discord` vs `…\Discord\`).
        var normalizedAnchor = anchorPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var createdAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        // ON CONFLICT DO NOTHING + RETURNING means: insert if unique, return
        // the row; on conflict, RETURNING yields zero rows so the reader
        // produces no result and we return null (the soft-failure signal).
        command.CommandText = """
            INSERT INTO app_identity_rule
                (anchor_path, filename, display_name, created_at)
            VALUES
                ($anchorPath, $filename, $displayName, $createdAt)
            ON CONFLICT(anchor_path, filename) DO NOTHING
            RETURNING id, anchor_path, filename, display_name, created_at;
            """;
        command.Parameters.AddWithValue("$anchorPath", normalizedAnchor);
        command.Parameters.AddWithValue("$filename", filename);
        command.Parameters.AddWithValue(
            "$displayName",
            string.IsNullOrEmpty(displayName) ? DBNull.Value : displayName);
        command.Parameters.AddWithValue("$createdAt", createdAtMs);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            return null; // conflict: duplicate rule
        }
        return MapRow(reader);
    }

    public async Task<bool> RemoveAsync(int id, CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_identity_rule WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affectedRows > 0;
    }

    public async Task<IReadOnlyList<AppIdentityRule>> ListAllAsync(CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, anchor_path, filename, display_name, created_at
            FROM app_identity_rule
            ORDER BY id ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rules = new List<AppIdentityRule>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            rules.Add(MapRow(reader));
        }
        return rules;
    }

    public async Task<AppIdentityRule?> MatchAsync(
        string filename, string fullPath, CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        // Compute the grandparent ONCE in C# — if it's null or empty (path
        // too shallow, e.g., file at a drive root with no parent's parent),
        // no rule can match.
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent)) return null;
        var grandparent = Path.GetDirectoryName(parent);
        if (string.IsNullOrEmpty(grandparent)) return null;
        var normalizedGrandparent = grandparent.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Pull all rules with this filename (filename index makes this cheap),
        // then in-C# match on the grandparent. Comparison is OS-native: case-
        // insensitive on Windows (NTFS), case-sensitive elsewhere.
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, anchor_path, filename, display_name, created_at
            FROM app_identity_rule
            WHERE filename = $filename COLLATE NOCASE
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("$filename", filename);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            var rule = MapRow(reader);
            if (normalizedGrandparent.Equals(rule.AnchorPath, pathComparison)) {
                return rule;
            }
        }
        return null;
    }

    private static AppIdentityRule MapRow(SqliteDataReader reader) {
        return new AppIdentityRule(
            Id: reader.GetInt32(0),
            AnchorPath: reader.GetString(1),
            Filename: reader.GetString(2),
            DisplayName: reader.IsDBNull(3) ? null : reader.GetString(3),
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));
    }
}
