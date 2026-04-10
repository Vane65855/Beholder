using Beholder.Daemon.Storage;
using Microsoft.Data.Sqlite;

namespace Beholder.Tests;

public sealed class DatabaseInitializerTests : IDisposable {
    private readonly string _tempDir;
    private readonly string _databasePath;

    public DatabaseInitializerTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
    }

    public void Dispose() {
        // SqliteConnection pools file handles; clear them so the temp directory can be deleted on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Initialize_CreatesAllTables_OnNewDatabase() {
        var initializer = new DatabaseInitializer(_databasePath);

        initializer.Initialize();

        var tables = ListTableNames();
        Assert.Contains("event_log", tables);
        Assert.Contains("checkpoint", tables);
        Assert.Contains("firewall_rules", tables);
        Assert.Contains("process_registry", tables);
    }

    [Fact]
    public void Initialize_IsIdempotent_RunningTwiceDoesNotThrow() {
        var initializer = new DatabaseInitializer(_databasePath);

        initializer.Initialize();
        initializer.Initialize();

        var tables = ListTableNames();
        Assert.Contains("event_log", tables);
        Assert.Contains("checkpoint", tables);
        Assert.Contains("firewall_rules", tables);
        Assert.Contains("process_registry", tables);
    }

    [Fact]
    public void Initialize_CreatesDirectory_WhenItDoesNotExist() {
        var nestedDir = Path.Combine(_tempDir, "subdir");
        var nestedDatabase = Path.Combine(nestedDir, "beholder.db");

        new DatabaseInitializer(nestedDatabase).Initialize();

        Assert.True(Directory.Exists(nestedDir));
        Assert.True(File.Exists(nestedDatabase));
    }

    [Fact]
    public void Initialize_SetsWalMode() {
        new DatabaseInitializer(_databasePath).Initialize();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)command.ExecuteScalar();

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public void Initialize_CreatesIndexes() {
        new DatabaseInitializer(_databasePath).Initialize();

        var indexes = ListIndexNames();
        Assert.Contains("idx_event_log_kind", indexes);
        Assert.Contains("idx_firewall_rules_process_path", indexes);
    }

    [Fact]
    public void Initialize_EventLogTable_HasCorrectColumns() {
        new DatabaseInitializer(_databasePath).Initialize();

        var columns = ReadColumnTypes("event_log");

        Assert.Equal("INTEGER", columns["seq"]);
        Assert.Equal("INTEGER", columns["ts_unix_ns"]);
        Assert.Equal("TEXT", columns["kind"]);
        Assert.Equal("BLOB", columns["payload"]);
        Assert.Equal("BLOB", columns["prev_hash"]);
        Assert.Equal("BLOB", columns["row_hash"]);
    }

    [Fact]
    public void Initialize_FirewallRulesTable_HasUniqueConstraint() {
        new DatabaseInitializer(_databasePath).Initialize();

        InsertFirewallRule(processPath: "/usr/bin/curl", direction: "out", action: "allow");

        var ex = Assert.Throws<SqliteException>(() =>
            InsertFirewallRule(processPath: "/usr/bin/curl", direction: "out", action: "block"));

        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private SqliteConnection OpenConnection() {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    private List<string> ListTableNames() {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private List<string> ListIndexNames() {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index';";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private Dictionary<string, string> ReadColumnTypes(string tableName) {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        var columns = new Dictionary<string, string>();
        while (reader.Read()) {
            var name = reader.GetString(1);
            var type = reader.GetString(2);
            columns[name] = type;
        }
        return columns;
    }

    private void InsertFirewallRule(string processPath, string direction, string action) {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO firewall_rules (process_path, direction, action, source, created_at, updated_at)
            VALUES ($path, $dir, $action, 'manual', 0, 0);
            """;
        command.Parameters.AddWithValue("$path", processPath);
        command.Parameters.AddWithValue("$dir", direction);
        command.Parameters.AddWithValue("$action", action);
        command.ExecuteNonQuery();
    }
}
