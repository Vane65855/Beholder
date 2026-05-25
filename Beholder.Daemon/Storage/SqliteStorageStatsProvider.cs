using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Reads per-table row counts and the database file size from the SQLite
/// database the daemon writes to. Backs the <c>GetStorageStats</c> gRPC
/// method, which in turn backs the Settings tab's Data Storage section.
/// </summary>
/// <remarks>
/// Per-table row counts come from <c>COUNT(*)</c> against every user
/// table in <c>sqlite_master</c>; on the daemon's tables this hits the
/// implicit rowid index and is cheap even at millions of rows. Per-table
/// byte size is deliberately not exposed — that requires enabling SQLite's
/// <c>dbstat</c> virtual table, which adds platform complexity for marginal
/// value when "row count per table + total DB bytes" already answers the
/// "is this thing eating my disk?" question.
/// </remarks>
internal sealed class SqliteStorageStatsProvider : IStorageStatsProvider {
    private readonly ConnectionFactory _connectionFactory;
    private readonly IChainStatusCache _chainStatusCache;
    private readonly string _databasePath;

    public SqliteStorageStatsProvider(
        ConnectionFactory connectionFactory,
        IChainStatusCache chainStatusCache,
        string databasePath
    ) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(chainStatusCache);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionFactory = connectionFactory;
        _chainStatusCache = chainStatusCache;
        _databasePath = databasePath;
    }

    public async Task<StorageStats> GetAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _connectionFactory.CreateConnection();

        var tables = new List<TableStats>();
        var names = await ListUserTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var name in names) {
            var count = await CountRowsAsync(connection, name, cancellationToken).ConfigureAwait(false);
            tables.Add(new TableStats(name, count));
        }

        // FileInfo.Length is the size of the .db file itself; WAL and SHM
        // sidecar files are not counted. SQLite periodically checkpoints
        // WAL contents back into the main file, so the number is a close
        // approximation of "how much disk this database is using" for a
        // daemon that has been running for any non-trivial time. Good
        // enough for a Settings UI; a more precise reading would sum the
        // -wal and -shm sidecars too but adds complexity for marginal gain.
        var totalBytes = new FileInfo(_databasePath).Length;

        return new StorageStats(
            DatabasePath: _databasePath,
            DatabaseBytesTotal: totalBytes,
            Tables: tables,
            ChainStatus: _chainStatusCache.Current);
    }

    private static async Task<List<string>> ListUserTablesAsync(
        SqliteConnection connection, CancellationToken cancellationToken
    ) {
        // sqlite_master rows for SQLite's own internal tables (sqlite_sequence,
        // sqlite_stat1 if ANALYZE has been run, etc.) start with the reserved
        // `sqlite_` prefix per the SQLite docs. The Settings UI doesn't care
        // about those — filter them out at the query layer rather than
        // post-filtering in C# so we don't allocate strings we'll discard.
        const string sql = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task<long> CountRowsAsync(
        SqliteConnection connection, string tableName, CancellationToken cancellationToken
    ) {
        // Quoting the table name with double quotes is the SQLite-approved way
        // to disambiguate identifiers (vs single quotes which are string
        // literals). Names come exclusively from sqlite_master, so SQL
        // injection isn't a concern here — but quoting anyway preserves the
        // pattern other callers should copy if they ever take user-supplied
        // table names.
        var sql = $"SELECT COUNT(*) FROM \"{tableName}\";";
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }
}
