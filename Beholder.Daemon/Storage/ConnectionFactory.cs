using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Builds opened <see cref="SqliteConnection"/> instances pointed at the daemon's
/// database file. Centralizes the connection string so individual store classes do
/// not have to know how the database is located.
/// </summary>
internal sealed class ConnectionFactory {
    private readonly string _databasePath;

    public ConnectionFactory(string databasePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public SqliteConnection CreateConnection() {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }
}
