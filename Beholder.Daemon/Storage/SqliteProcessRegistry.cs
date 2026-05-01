using Beholder.Core;
using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IProcessRegistry"/>. Tracks every binary
/// the daemon has observed on the network, along with its most recent SHA-256 hash.
/// The alert pipeline consults this store to decide whether a binary is appearing on
/// the network for the first time (<c>NewProcess</c> alert) or whether its hash has
/// drifted since the last observation (<c>HashChanged</c> alert).
/// </summary>
internal sealed class SqliteProcessRegistry : IProcessRegistry {
    private readonly ConnectionFactory _connectionFactory;

    public SqliteProcessRegistry(ConnectionFactory connectionFactory) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<ProcessInfo?> GetByPathAsync(string path, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectAllColumnsSql + " WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return MapRow(reader);
    }

    /// <inheritdoc />
    public async Task<ProcessInfo?> FindByLogicalIdentityAsync(
        string companyName, string productName, string installRoot,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        // Case-insensitive comparison on install_root because Windows file
        // paths are case-insensitive. company_name and product_name come
        // from PE VersionInfo strings; pin those exact for now (publishers
        // don't typically vary case across versions). LIMIT 1 because
        // (company, product, install_root) is treated as unique even though
        // the schema doesn't enforce it (multiple paths per logical app
        // are normal — Discord 9225 vs 9235 — but they share identity).
        command.CommandText =
            SelectAllColumnsSql +
            " WHERE company_name = $company AND product_name = $product " +
            "  AND install_root = $root COLLATE NOCASE " +
            "LIMIT 1;";
        command.Parameters.AddWithValue("$company", companyName);
        command.Parameters.AddWithValue("$product", productName);
        command.Parameters.AddWithValue("$root", installRoot);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return MapRow(reader);
    }

    /// <summary>
    /// Shared column list for the three SELECT queries. Centralised so a
    /// schema column addition only edits one constant — avoids the bug where
    /// adding a column breaks one of the readers but not the others.
    /// </summary>
    private const string SelectAllColumnsSql = """
        SELECT path, display_name, sha256, first_seen, last_seen, last_hash_at,
               company_name, product_name, install_root,
               cert_subject_cn, cert_issuer_cn, signature_status
        FROM process_registry
        """;

    public async Task RegisterAsync(ProcessInfo info, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(info);

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        // first_seen is deliberately omitted from DO UPDATE SET. The first time a
        // binary was observed is a historical fact — it never changes. Matches the
        // SqliteFirewallRuleStore.UpsertAsync pattern for created_at.
        // Phase 7.5: identity columns (company/product/install_root + cert info)
        // get upserted alongside the path so a re-registration with newly-resolved
        // identity (e.g. first cert read) populates the row.
        command.CommandText = """
            INSERT INTO process_registry
                (path, display_name, sha256, first_seen, last_seen, last_hash_at,
                 company_name, product_name, install_root,
                 cert_subject_cn, cert_issuer_cn, signature_status)
            VALUES
                ($path, $displayName, $sha256, $firstSeen, $lastSeen, $lastHashAt,
                 $companyName, $productName, $installRoot,
                 $certSubjectCn, $certIssuerCn, $signatureStatus)
            ON CONFLICT(path) DO UPDATE SET
                display_name     = excluded.display_name,
                sha256           = excluded.sha256,
                last_seen        = excluded.last_seen,
                last_hash_at     = excluded.last_hash_at,
                company_name     = excluded.company_name,
                product_name     = excluded.product_name,
                install_root     = excluded.install_root,
                cert_subject_cn  = excluded.cert_subject_cn,
                cert_issuer_cn   = excluded.cert_issuer_cn,
                signature_status = excluded.signature_status;
            """;
        command.Parameters.AddWithValue("$path", info.Path);
        command.Parameters.AddWithValue("$displayName", info.DisplayName);
        command.Parameters.AddWithValue("$sha256", (object?)info.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$firstSeen", info.FirstSeen.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$lastSeen", info.LastSeen.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$lastHashAt",
            info.LastHashedAt.HasValue ? info.LastHashedAt.Value.ToUnixTimeMilliseconds() : (object)DBNull.Value
        );
        command.Parameters.AddWithValue("$companyName", (object?)info.CompanyName ?? DBNull.Value);
        command.Parameters.AddWithValue("$productName", (object?)info.ProductName ?? DBNull.Value);
        command.Parameters.AddWithValue("$installRoot", (object?)info.InstallRoot ?? DBNull.Value);
        command.Parameters.AddWithValue("$certSubjectCn", (object?)info.CertSubjectCn ?? DBNull.Value);
        command.Parameters.AddWithValue("$certIssuerCn", (object?)info.CertIssuerCn ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$signatureStatus",
            info.SignatureStatus.HasValue ? info.SignatureStatus.Value.ToString() : (object)DBNull.Value
        );

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProcessInfo>> ListAllAsync(CancellationToken cancellationToken) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectAllColumnsSql + " ORDER BY last_seen DESC;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var processes = new List<ProcessInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) processes.Add(MapRow(reader));
        return processes;
    }

    private static ProcessInfo MapRow(SqliteDataReader reader) {
        return new ProcessInfo(
            path: reader.GetString(0),
            displayName: reader.GetString(1),
            sha256: reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2),
            firstSeen: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            lastSeen: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            lastHashedAt: reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            companyName: reader.IsDBNull(6) ? null : reader.GetString(6),
            productName: reader.IsDBNull(7) ? null : reader.GetString(7),
            installRoot: reader.IsDBNull(8) ? null : reader.GetString(8),
            certSubjectCn: reader.IsDBNull(9) ? null : reader.GetString(9),
            certIssuerCn: reader.IsDBNull(10) ? null : reader.GetString(10),
            signatureStatus: reader.IsDBNull(11)
                ? null
                : Enum.Parse<SignatureValidationStatus>(reader.GetString(11))
        );
    }
}
