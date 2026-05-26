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
    private readonly bool _pooling;

    /// <summary>
    /// Constructs an initializer that targets the SQLite file at
    /// <paramref name="databasePath"/>. The directory containing the file does not
    /// need to exist; <see cref="Initialize"/> will create it.
    /// </summary>
    public DatabaseInitializer(string databasePath, bool pooling = true) {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        _pooling = pooling;
    }

    /// <summary>
    /// Creates the parent directory if missing, opens the database (creating the file
    /// if absent), enables WAL journaling, and ensures every required table and index
    /// exists.
    /// </summary>
    public void Initialize() {
        EnsureDirectoryExists();

        var builder = new SqliteConnectionStringBuilder {
            DataSource = _databasePath,
            Pooling = _pooling
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        EnableWalMode(connection);
        CreateTables(connection);
        MigrateProcessRegistryFor75(connection);
        MigrateLanDeviceFor95(connection);
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
            CREATE TABLE IF NOT EXISTS traffic_raw (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                process_path    TEXT    NOT NULL,
                process_name    TEXT    NOT NULL,
                remote_address  TEXT    NOT NULL,
                remote_port     INTEGER NOT NULL,
                hostname        TEXT,
                country         TEXT    NOT NULL,
                bytes_in        INTEGER NOT NULL,
                bytes_out       INTEGER NOT NULL,
                bucket_start_ms INTEGER NOT NULL,
                bucket_seconds  INTEGER NOT NULL DEFAULT 1
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS traffic_buckets_10s (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                process_path    TEXT    NOT NULL,
                process_name    TEXT    NOT NULL,
                remote_address  TEXT    NOT NULL,
                remote_port     INTEGER NOT NULL,
                hostname        TEXT,
                country         TEXT    NOT NULL,
                bytes_in        INTEGER NOT NULL,
                bytes_out       INTEGER NOT NULL,
                bucket_start_ms INTEGER NOT NULL,
                bucket_seconds  INTEGER NOT NULL DEFAULT 10
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS traffic_buckets_1m (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                process_path    TEXT    NOT NULL,
                process_name    TEXT    NOT NULL,
                remote_address  TEXT    NOT NULL,
                remote_port     INTEGER NOT NULL,
                hostname        TEXT,
                country         TEXT    NOT NULL,
                bytes_in        INTEGER NOT NULL,
                bytes_out       INTEGER NOT NULL,
                bucket_start_ms INTEGER NOT NULL,
                bucket_seconds  INTEGER NOT NULL DEFAULT 60
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS traffic_buckets_10m (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                process_path    TEXT    NOT NULL,
                process_name    TEXT    NOT NULL,
                remote_address  TEXT    NOT NULL,
                remote_port     INTEGER NOT NULL,
                hostname        TEXT,
                country         TEXT    NOT NULL,
                bytes_in        INTEGER NOT NULL,
                bytes_out       INTEGER NOT NULL,
                bucket_start_ms INTEGER NOT NULL,
                bucket_seconds  INTEGER NOT NULL DEFAULT 600
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS traffic_buckets_1h (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                process_path    TEXT    NOT NULL,
                process_name    TEXT    NOT NULL,
                remote_address  TEXT    NOT NULL,
                remote_port     INTEGER NOT NULL,
                hostname        TEXT,
                country         TEXT    NOT NULL,
                bytes_in        INTEGER NOT NULL,
                bytes_out       INTEGER NOT NULL,
                bucket_start_ms INTEGER NOT NULL,
                bucket_seconds  INTEGER NOT NULL DEFAULT 3600
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS dns_cache (
                address    TEXT    PRIMARY KEY,
                hostname   TEXT    NOT NULL,
                updated_at INTEGER NOT NULL
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS settings_overrides (
                name       TEXT    PRIMARY KEY,
                value_json TEXT    NOT NULL,
                updated_at INTEGER NOT NULL
            );
            """);

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

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS alert_state (
                seq                INTEGER PRIMARY KEY,
                first_viewed_at_ns INTEGER NOT NULL
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS lan_device (
                mac                TEXT    PRIMARY KEY,
                ip                 TEXT    NOT NULL,
                vendor             TEXT    NULL,
                hostname           TEXT    NULL,
                first_seen_unix_ns INTEGER NOT NULL,
                last_seen_unix_ns  INTEGER NOT NULL
            );
            """);
    }

    /// <summary>
    /// Idempotent ALTER TABLE migration that adds the Phase 7.5 logical-
    /// identity + Authenticode columns to <c>process_registry</c>. Each
    /// column is only added when missing, so re-running on an upgraded
    /// database is a no-op. Pre-7.5 rows keep NULL identity columns and
    /// fall back to path-based dedup. See ADR 007.
    /// </summary>
    private static void MigrateProcessRegistryFor75(SqliteConnection connection) {
        var existingColumns = ReadColumnNames(connection, "process_registry");
        AddColumnIfMissing(connection, existingColumns, "process_registry", "company_name", "TEXT");
        AddColumnIfMissing(connection, existingColumns, "process_registry", "product_name", "TEXT");
        AddColumnIfMissing(connection, existingColumns, "process_registry", "install_root", "TEXT");
        AddColumnIfMissing(connection, existingColumns, "process_registry", "cert_subject_cn", "TEXT");
        AddColumnIfMissing(connection, existingColumns, "process_registry", "cert_issuer_cn", "TEXT");
        AddColumnIfMissing(connection, existingColumns, "process_registry", "signature_status", "TEXT");
    }

    /// <summary>
    /// Phase 9.5: adds the user-supplied <c>label</c> column to <c>lan_device</c>.
    /// Labels are cosmetic UI state (not chain-audited); persisted alongside
    /// the existing columns so they survive across daemon restarts and across
    /// scanner re-observations of the same MAC. Idempotent — re-runs on
    /// already-migrated DBs are no-ops via <see cref="AddColumnIfMissing"/>.
    /// </summary>
    private static void MigrateLanDeviceFor95(SqliteConnection connection) {
        var existingColumns = ReadColumnNames(connection, "lan_device");
        AddColumnIfMissing(connection, existingColumns, "lan_device", "label", "TEXT");
    }

    private static HashSet<string> ReadColumnNames(SqliteConnection connection, string table) {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) names.Add(reader.GetString(1));  // column 1 is the name
        return names;
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection, HashSet<string> existing,
        string table, string column, string type
    ) {
        if (existing.Contains(column)) return;
        Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type};");
    }

    private static void CreateIndexes(SqliteConnection connection) {
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_event_log_kind ON event_log(kind);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_firewall_rules_process_path ON firewall_rules(process_path);");

        // Partial index supporting Phase 7.5 logical-identity dedup queries.
        // Only indexes rows that actually have identity data (post-7.5 rows
        // and any pre-7.5 rows that were re-resolved). Keeps the index tight.
        Execute(connection,
            "CREATE INDEX IF NOT EXISTS idx_process_registry_logical_identity " +
            "ON process_registry(company_name, product_name, install_root) " +
            "WHERE company_name IS NOT NULL;");

        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_raw_process_time ON traffic_raw(process_path, bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_raw_time ON traffic_raw(bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_raw_country_time ON traffic_raw(country, bucket_start_ms);");
        // Phase 9.6: backs the optional remote_address filter on
        // GetProcessSummariesAsync (Scanner → Traffic cross-link). Idempotent
        // via `CREATE INDEX IF NOT EXISTS` — applied automatically on the next
        // daemon start for existing installs; no separate migration needed.
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_raw_address_time ON traffic_raw(remote_address, bucket_start_ms);");

        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_process_time ON traffic_buckets_10s(process_path, bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_time ON traffic_buckets_10s(bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_country_time ON traffic_buckets_10s(country, bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_address_time ON traffic_buckets_10s(remote_address, bucket_start_ms);");

        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_1m_process_time ON traffic_buckets_1m(process_path, bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_1m_time ON traffic_buckets_1m(bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_1m_country_time ON traffic_buckets_1m(country, bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_1m_address_time ON traffic_buckets_1m(remote_address, bucket_start_ms);");

        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_10m_process_time ON traffic_buckets_10m(process_path, bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_10m_time ON traffic_buckets_10m(bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_10m_country_time ON traffic_buckets_10m(country, bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_10m_address_time ON traffic_buckets_10m(remote_address, bucket_start_ms);");

        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_1h_process_time ON traffic_buckets_1h(process_path, bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_1h_time ON traffic_buckets_1h(bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_1h_country_time ON traffic_buckets_1h(country, bucket_start_ms);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_traffic_1h_address_time ON traffic_buckets_1h(remote_address, bucket_start_ms);");

        // Phase 9.1 (ADR 009): LAN device discovery storage.
        // idx_lan_device_ip supports 9.2's MAC-change detection (find existing
        // device by IP, compare its MAC to the just-observed one).
        // idx_lan_device_last_seen supports 9.3's ListLanDevices RPC "seen since"
        // filter — ORDER BY last_seen_unix_ns DESC + range scan via this index.
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_lan_device_ip ON lan_device(ip);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_lan_device_last_seen ON lan_device(last_seen_unix_ns);");
    }

    private static void Execute(SqliteConnection connection, string sql) {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
