using System.Text.Json;
using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IAlertStore"/>. Alert events
/// themselves live in <c>event_log</c> as chain-hashed rows — this store only
/// reads them and joins against the out-of-chain <c>alert_state</c> side table
/// for read-state. Writes go exclusively to <c>alert_state</c>, never to the
/// chain. The canonical payload is a UTF-8 JSON object with string fields
/// <c>processPath</c> and <c>summary</c>.
/// </summary>
internal sealed class SqliteAlertStore : IAlertStore {
    private readonly ConnectionFactory _connectionFactory;
    private readonly ILogger<SqliteAlertStore> _logger;

    public SqliteAlertStore(ConnectionFactory connectionFactory, ILogger<SqliteAlertStore> logger) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Alert>> GetAlertsAsync(int limit, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT el.seq, el.ts_unix_ns, el.kind, el.payload,
                   COALESCE(als.first_viewed_at_ns, 0) AS first_viewed_at_ns
            FROM event_log el
            LEFT JOIN alert_state als ON el.seq = als.seq
            WHERE el.kind IN ('NewProcess', 'HashChanged', 'ChainError')
            ORDER BY el.seq DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var alerts = new List<Alert>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            alerts.Add(MapRow(reader));
        }
        return alerts;
    }

    public async Task MarkAlertReadAsync(long seq, DateTimeOffset viewedAt, CancellationToken cancellationToken) {
        // Event-log seq is 0-based; the genesis-row alert is seq 0, so only
        // negatives are invalid.
        ArgumentOutOfRangeException.ThrowIfNegative(seq);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_state (seq, first_viewed_at_ns)
            VALUES ($seq, $viewedAtNs)
            ON CONFLICT(seq) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$viewedAtNs", viewedAt.ToUnixTimeMilliseconds() * 1_000_000L);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Alert MapRow(SqliteDataReader reader) {
        var seq = reader.GetInt64(0);
        var tsNs = reader.GetInt64(1);
        var kindString = reader.GetString(2);
        var payload = (byte[])reader.GetValue(3);
        var firstViewedNs = reader.GetInt64(4);

        var kind = MapAlertKind(kindString);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsNs / 1_000_000L);
        DateTimeOffset? firstViewedAt = firstViewedNs == 0
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(firstViewedNs / 1_000_000L);

        var (processPath, summary) = ParsePayload(payload, seq);
        return new Alert(seq, kind, processPath, summary, timestamp, firstViewedAt);
    }

    private (string ProcessPath, string Summary) ParsePayload(byte[] payload, long seq) {
        try {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var processPath = root.TryGetProperty("processPath", out var pp)
                ? pp.GetString() ?? ""
                : "";
            var summary = root.TryGetProperty("summary", out var sm)
                ? sm.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(summary)) {
                _logger.LogWarning("Alert seq {Seq} has empty summary in payload", seq);
                return (processPath, "(empty summary)");
            }
            return (processPath, summary);
        } catch (JsonException ex) {
            _logger.LogWarning(ex, "Failed to parse alert payload for seq {Seq}", seq);
            return ("", "(malformed payload)");
        }
    }

    private static AlertKind MapAlertKind(string kindString) => kindString switch {
        "NewProcess" => AlertKind.NewProcess,
        "HashChanged" => AlertKind.HashChanged,
        "ChainError" => AlertKind.ChainError,
        _ => AlertKind.Unknown,
    };
}
