namespace Beholder.Core;

/// <summary>
/// A tracked binary in the daemon's process registry. Identified by its filesystem
/// path; carries the most recent SHA-256 hash if one has been computed.
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

    /// <summary>Constructs a validated process registry entry.</summary>
    public ProcessInfo(
        string path,
        string displayName,
        byte[]? sha256,
        DateTimeOffset firstSeen,
        DateTimeOffset lastSeen,
        DateTimeOffset? lastHashedAt
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        Path = path;
        DisplayName = displayName;
        Sha256 = sha256?.ToArray();
        FirstSeen = firstSeen;
        LastSeen = lastSeen;
        LastHashedAt = lastHashedAt;
    }
}
