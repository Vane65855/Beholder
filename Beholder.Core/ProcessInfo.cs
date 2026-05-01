namespace Beholder.Core;

/// <summary>
/// A tracked binary in the daemon's process registry. Identified by its filesystem
/// path; carries the most recent SHA-256 hash if one has been computed, plus
/// optional logical-identity metadata (CompanyName, ProductName, install root)
/// and Authenticode signature metadata for the spoof-detection layer added in
/// Phase 7.5. See ADR 007.
/// </summary>
public sealed record ProcessInfo {
    /// <summary>Full filesystem path of the binary. Acts as the registry primary key.</summary>
    public string Path { get; }

    /// <summary>Display name shown in the UI (typically the executable file name).</summary>
    public string DisplayName { get; }

    /// <summary>SHA-256 hash of the binary, or null if it has not yet been hashed.</summary>
    public byte[]? Sha256 { get; }

    /// <summary>Wall-clock timestamp at which the binary was first observed on the network.</summary>
    public DateTimeOffset FirstSeen { get; }

    /// <summary>Wall-clock timestamp at which the binary was most recently observed.</summary>
    public DateTimeOffset LastSeen { get; }

    /// <summary>Wall-clock timestamp of the most recent successful hash, or null if never hashed.</summary>
    public DateTimeOffset? LastHashedAt { get; }

    /// <summary>PE VersionInfo CompanyName (e.g., "Discord, Inc."), or null if absent.</summary>
    public string? CompanyName { get; }

    /// <summary>PE VersionInfo ProductName (e.g., "Discord"), or null if absent.</summary>
    public string? ProductName { get; }

    /// <summary>
    /// Install-root folder for the logical app — closest ancestor folder of
    /// <see cref="Path"/> whose name matches <see cref="ProductName"/>. Null
    /// when no ancestor matches OR when ProductName is unavailable. Forms the
    /// third component of the (CompanyName, ProductName, InstallRoot) logical
    /// identity tuple used to dedup auto-updating apps.
    /// </summary>
    public string? InstallRoot { get; }

    /// <summary>Authenticode certificate Subject CN (e.g., "CN=Discord Inc."), or null if unsigned.</summary>
    public string? CertSubjectCn { get; }

    /// <summary>Authenticode certificate Issuer CN (e.g., "CN=DigiCert Trusted G4 ..."), or null if unsigned.</summary>
    public string? CertIssuerCn { get; }

    /// <summary>WinVerifyTrust validation outcome at registration time, or null if unsigned/unverified.</summary>
    public SignatureValidationStatus? SignatureStatus { get; }

    /// <summary>Constructs a validated process registry entry.</summary>
    public ProcessInfo(
        string path,
        string displayName,
        byte[]? sha256,
        DateTimeOffset firstSeen,
        DateTimeOffset lastSeen,
        DateTimeOffset? lastHashedAt,
        string? companyName = null,
        string? productName = null,
        string? installRoot = null,
        string? certSubjectCn = null,
        string? certIssuerCn = null,
        SignatureValidationStatus? signatureStatus = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        Path = path;
        DisplayName = displayName;
        Sha256 = sha256?.ToArray();
        FirstSeen = firstSeen;
        LastSeen = lastSeen;
        LastHashedAt = lastHashedAt;
        CompanyName = companyName;
        ProductName = productName;
        InstallRoot = installRoot;
        CertSubjectCn = certSubjectCn;
        CertIssuerCn = certIssuerCn;
        SignatureStatus = signatureStatus;
    }
}
