using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed persistence for the Phase 9 LAN scanner's <c>lan_device</c>
/// table. Identity is keyed on MAC per ADR 009; <see cref="UpsertAsync"/> uses
/// <c>INSERT … ON CONFLICT(mac) DO UPDATE</c> so the original
/// <see cref="LanDevice.FirstSeen"/> is preserved across re-observations while
/// the mutable attributes (IP, vendor, hostname, last-seen) refresh.
/// </summary>
internal sealed class SqliteLanDeviceStore : ILanDeviceStore {
    private const int ColMac = 0;
    private const int ColIp = 1;
    private const int ColVendor = 2;
    private const int ColHostname = 3;
    private const int ColFirstSeen = 4;
    private const int ColLastSeen = 5;

    private const string SelectColumns =
        "mac, ip, vendor, hostname, first_seen_unix_ns, last_seen_unix_ns";

    private readonly ConnectionFactory _connectionFactory;

    public SqliteLanDeviceStore(ConnectionFactory connectionFactory) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<LanDevice?> GetByMacAsync(string mac, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM lan_device
            WHERE mac = $mac;
            """;
        command.Parameters.AddWithValue("$mac", mac);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return MapRow(reader);
    }

    public async Task<LanDevice?> GetByIpAsync(string ip, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM lan_device
            WHERE ip = $ip
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ip", ip);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return MapRow(reader);
    }

    public async Task<IReadOnlyList<LanDevice>> ListAsync(
        LanDeviceQuery query, CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(query);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();

        var whereClause = query.SeenSince.HasValue
            ? "WHERE last_seen_unix_ns >= $seenSince"
            : string.Empty;
        var limitClause = query.Limit > 0 ? "LIMIT $limit" : string.Empty;

        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM lan_device
            {whereClause}
            ORDER BY last_seen_unix_ns DESC
            {limitClause};
            """;
        if (query.SeenSince.HasValue) {
            command.Parameters.AddWithValue("$seenSince", ToUnixNanoseconds(query.SeenSince.Value));
        }
        if (query.Limit > 0) command.Parameters.AddWithValue("$limit", query.Limit);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var devices = new List<LanDevice>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) devices.Add(MapRow(reader));
        return devices;
    }

    public async Task UpsertAsync(LanDevice device, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(device);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        // first_seen_unix_ns is deliberately omitted from DO UPDATE SET so the
        // original observation timestamp is preserved across re-observations.
        // Mirrors the SqliteFirewallRuleStore upsert's created_at handling.
        command.CommandText = """
            INSERT INTO lan_device
                (mac, ip, vendor, hostname, first_seen_unix_ns, last_seen_unix_ns)
            VALUES
                ($mac, $ip, $vendor, $hostname, $firstSeen, $lastSeen)
            ON CONFLICT(mac) DO UPDATE SET
                ip                = excluded.ip,
                vendor            = excluded.vendor,
                hostname          = excluded.hostname,
                last_seen_unix_ns = excluded.last_seen_unix_ns;
            """;
        command.Parameters.AddWithValue("$mac", device.Mac);
        command.Parameters.AddWithValue("$ip", device.Ip);
        command.Parameters.AddWithValue("$vendor", (object?)device.Vendor ?? DBNull.Value);
        command.Parameters.AddWithValue("$hostname", (object?)device.Hostname ?? DBNull.Value);
        command.Parameters.AddWithValue("$firstSeen", ToUnixNanoseconds(device.FirstSeen));
        command.Parameters.AddWithValue("$lastSeen", ToUnixNanoseconds(device.LastSeen));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static LanDevice MapRow(SqliteDataReader reader) {
        return new LanDevice(
            Mac: reader.GetString(ColMac),
            Ip: reader.GetString(ColIp),
            Vendor: reader.IsDBNull(ColVendor) ? null : reader.GetString(ColVendor),
            Hostname: reader.IsDBNull(ColHostname) ? null : reader.GetString(ColHostname),
            FirstSeen: FromUnixNanoseconds(reader.GetInt64(ColFirstSeen)),
            LastSeen: FromUnixNanoseconds(reader.GetInt64(ColLastSeen))
        );
    }

    private static long ToUnixNanoseconds(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds() * 1_000_000L;

    private static DateTimeOffset FromUnixNanoseconds(long unixNs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixNs / 1_000_000L);
}
