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
    private const string LanDeviceTableName = "lan_device";
    private const string EventLogTableName = "event_log";

    private readonly ConnectionFactory _connectionFactory;
    private readonly IChainStatusCache _chainStatusCache;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IDaemonClock _daemonClock;
    private readonly string _databasePath;

    public SqliteStorageStatsProvider(
        ConnectionFactory connectionFactory,
        IChainStatusCache chainStatusCache,
        ICheckpointStore checkpointStore,
        IDaemonClock daemonClock,
        string databasePath
    ) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(chainStatusCache);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(daemonClock);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionFactory = connectionFactory;
        _chainStatusCache = chainStatusCache;
        _checkpointStore = checkpointStore;
        _daemonClock = daemonClock;
        _databasePath = databasePath;
    }

    public async Task<StorageStats> GetAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _connectionFactory.CreateConnection();

        var tables = new List<TableStats>();
        long lanDeviceCount = 0;
        var names = await ListUserTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var name in names) {
            var count = await CountRowsAsync(connection, name, cancellationToken).ConfigureAwait(false);
            tables.Add(new TableStats(name, count));
            if (name == LanDeviceTableName) lanDeviceCount = count;
        }

        // FileInfo.Length is the size of the .db file itself; WAL and SHM
        // sidecar files are not counted. SQLite periodically checkpoints
        // WAL contents back into the main file, so the number is a close
        // approximation of "how much disk this database is using" for a
        // daemon that has been running for any non-trivial time. Good
        // enough for a Settings UI; a more precise reading would sum the
        // -wal and -shm sidecars too but adds complexity for marginal gain.
        var totalBytes = new FileInfo(_databasePath).Length;

        var chainFirstEventAt = await QueryChainFirstEventAtAsync(
            connection, cancellationToken).ConfigureAwait(false);

        // Latest signed checkpoint (Phase 11), read fresh each call so the
        // Settings tab's "Last checkpoint" line reflects the current signer
        // state independently of when the chain was last verified.
        var latestCheckpoint = await _checkpointStore
            .GetLatestAsync(cancellationToken).ConfigureAwait(false);

        return new StorageStats(
            DatabasePath: _databasePath,
            DatabaseBytesTotal: totalBytes,
            Tables: tables,
            ChainStatus: _chainStatusCache.Current,
            ChainFirstEventAt: chainFirstEventAt,
            DaemonStartedAt: _daemonClock.StartedAt,
            LanDeviceCount: lanDeviceCount,
            LatestCheckpointSeq: latestCheckpoint?.Seq,
            LatestCheckpointAt: latestCheckpoint?.Timestamp,
            LatestCheckpointKeyId: latestCheckpoint?.KeyId);
    }

    /// <summary>
    /// Returns the timestamp of the earliest row in <c>event_log</c>, or
    /// null when the chain is empty (fresh install, no events yet) or when
    /// the table is somehow absent (defensive — shouldn't happen against a
    /// daemon-initialized database).
    /// </summary>
    private static async Task<DateTimeOffset?> QueryChainFirstEventAtAsync(
        SqliteConnection connection, CancellationToken cancellationToken
    ) {
        // The event_log table is the chain-audited log; ts_unix_ns is the
        // column name per DatabaseInitializer.CreateTables. MIN() over an
        // indexed integer column on a typical-sized chain (<100k rows) is
        // negligibly cheap.
        const string sql = $"SELECT MIN(ts_unix_ns) FROM \"{EventLogTableName}\";";
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull) return null;
        var unixNs = Convert.ToInt64(result);
        return DateTimeOffset.FromUnixTimeMilliseconds(unixNs / 1_000_000L);
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
