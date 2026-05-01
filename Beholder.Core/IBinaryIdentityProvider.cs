namespace Beholder.Core;

/// <summary>
/// Reads platform-specific identity metadata from a binary on disk: PE
/// VersionInfo strings (CompanyName, ProductName) and Authenticode signature
/// info (SubjectCn, IssuerCn, validation status). Used by the alert pipeline
/// to deduplicate auto-updating apps (Squirrel-style installers like Discord)
/// and detect publisher-spoof scenarios. See ADR 007.
/// </summary>
/// <remarks>
/// Windows implementation lives in <c>Beholder.Daemon.Windows</c>; Linux and
/// macOS daemons register no implementation and the alert pipeline falls back
/// to path-based deduplication (current pre-Phase 7.5 behavior preserved).
/// </remarks>
public interface IBinaryIdentityProvider {
    /// <summary>
    /// Reads the binary's identity. Returns null if the binary cannot be
    /// read (file missing, locked, malformed PE). Identity components are
    /// individually nullable when the corresponding metadata is absent —
    /// unsigned binaries return non-null result with null
    /// <see cref="BinaryIdentity.Signature"/>; binaries without VersionInfo
    /// return non-null result with null CompanyName/ProductName.
    /// </summary>
    Task<BinaryIdentity?> ReadIdentityAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Identity metadata extracted from a binary on disk. All fields are
/// nullable so the alert pipeline can fall back gracefully when any layer
/// of metadata is unavailable (unsigned binaries, missing VersionInfo,
/// non-PE files).
/// </summary>
public sealed record BinaryIdentity(
    string? CompanyName,
    string? ProductName,
    AuthenticodeInfo? Signature
);

/// <summary>
/// Authenticode signature metadata. Present only for code-signed binaries
/// whose signature was successfully extracted. The <see cref="Status"/>
/// field carries the chain-validation outcome — only <see cref="SignatureValidationStatus.Valid"/>
/// should be trusted for spoof-detection comparisons.
/// </summary>
public sealed record AuthenticodeInfo(
    string SubjectCn,
    string IssuerCn,
    SignatureValidationStatus Status
);

/// <summary>
/// Result of <c>WinVerifyTrust</c> chain validation against the system
/// certificate store. Maps the Win32 trust HRESULT space to a tractable
/// enumeration. Per ADR 007 only <see cref="Valid"/> grants trust for
/// publisher-comparison purposes.
/// </summary>
public enum SignatureValidationStatus {
    /// <summary>Reserved default. Indicates an unrecognized or uninitialized value.</summary>
    Unknown = 0,
    /// <summary>Chain validates; signature is current and trusted.</summary>
    Valid = 1,
    /// <summary>Signature or certificate is past its expiration date.</summary>
    Expired = 2,
    /// <summary>Certificate has been revoked by its issuer.</summary>
    Revoked = 3,
    /// <summary>Chain does not terminate at a trusted root in the system store.</summary>
    UntrustedRoot = 4,
    /// <summary>Signature failed validation for another reason (malformed, broken chain, etc.).</summary>
    Invalid = 5,
}
