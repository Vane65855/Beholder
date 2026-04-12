using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Builds opened <see cref="SqliteConnection"/> instances pointed at the daemon's
/// database file. Centralizes the connection string so individual store classes do
/// not have to know how the database is located.
/// </summary>
internal sealed class ConnectionFactory {
    private readonly string _connectionString;

    public ConnectionFactory(string databasePath, bool pooling = true) {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Pooling = pooling
        };
        _connectionString = builder.ConnectionString;
    }

    public SqliteConnection CreateConnection() {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
