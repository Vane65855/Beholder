using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Creates the SQLite schema the daemon depends on. Idempotent — every <c>CREATE</c>
/// statement uses <c>IF NOT EXISTS</c>, so calling <see cref="Initialize"/> against a
/// brand-new database, an upgrade target, or an already-current database all produce
/// the same end state. Runs synchronously at daemon startup before any async work
/// begins; SQLite DDL is fast enough that an async surface would add complexity for
/// no benefit.
/// </summary>
public sealed class DatabaseInitializer {
    private readonly string _databasePath;

    /// <summary>
    /// Constructs an initializer that targets the SQLite file at
    /// <paramref name="databasePath"/>. The directory containing the file does not
    /// need to exist; <see cref="Initialize"/> will create it.
    /// </summary>
    public DatabaseInitializer(string databasePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    /// <summary>
    /// Creates the parent directory if missing, opens the database (creating the file
    /// if absent), enables WAL journaling, and ensures every required table and index
    /// exists.
    /// </summary>
    public void Initialize() {
        EnsureDirectoryExists();

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        EnableWalMode(connection);
        CreateTables(connection);
        CreateIndexes(connection);
    }

    private void EnsureDirectoryExists() {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    private static void EnableWalMode(SqliteConnection connection) {
        Execute(connection, "PRAGMA journal_mode=WAL;");
    }

    private static void CreateTables(SqliteConnection connection) {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS event_log (
                seq           INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_unix_ns    INTEGER NOT NULL,
                kind          TEXT    NOT NULL,
                payload       BLOB    NOT NULL,
                prev_hash     BLOB    NOT NULL,
                row_hash      BLOB    NOT NULL
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS checkpoint (
                seq           INTEGER PRIMARY KEY,
                row_hash      BLOB    NOT NULL,
                ts_unix_ns    INTEGER NOT NULL,
                signature     BLOB    NOT NULL,
                key_id        TEXT    NOT NULL
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS firewall_rules (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                process_path  TEXT    NOT NULL,
                direction     TEXT    NOT NULL,
                action        TEXT    NOT NULL,
                source        TEXT    NOT NULL,
                created_at    INTEGER NOT NULL,
                updated_at    INTEGER NOT NULL,
                UNIQUE(process_path, direction)
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS process_registry (
                path          TEXT    PRIMARY KEY,
                display_name  TEXT    NOT NULL,
                sha256        BLOB,
                first_seen    INTEGER NOT NULL,
                last_seen     INTEGER NOT NULL,
                last_hash_at  INTEGER
            );
            """);
    }

    private static void CreateIndexes(SqliteConnection connection) {
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_event_log_kind ON event_log(kind);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_firewall_rules_process_path ON firewall_rules(process_path);");
    }

    private static void Execute(SqliteConnection connection, string sql) {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
