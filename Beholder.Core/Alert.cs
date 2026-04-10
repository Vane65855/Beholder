namespace Beholder.Core;

/// <summary>
/// A persisted alert from the chain-hashed event log. Alerts are append-only and
/// transition from "unread" to "read" by setting <see cref="FirstViewedAt"/>.
/// </summary>
public sealed record Alert {
    /// <summary>Sequence number assigned by the event_log table.</summary>
    public long Seq { get; }

    /// <summary>Category of the alert.</summary>
    public AlertKind Kind { get; }

    /// <summary>
    /// Path of the binary that triggered the alert. Empty string for
    /// <see cref="AlertKind.ChainError"/>, which has no associated process.
    /// </summary>
    public string ProcessPath { get; }

    /// <summary>Human-readable one-line summary of the alert.</summary>
    public string Summary { get; }

    /// <summary>Wall-clock timestamp at which the alert was generated.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Timestamp at which the user first viewed the alert, or null while unread.</summary>
    public DateTimeOffset? FirstViewedAt { get; }

    /// <summary>True once the user has viewed this alert at least once.</summary>
    public bool IsRead => FirstViewedAt is not null;

    /// <summary>Constructs a validated alert.</summary>
    public Alert(
        long seq,
        AlertKind kind,
        string processPath,
        string summary,
        DateTimeOffset timestamp,
        DateTimeOffset? firstViewedAt
    ) {
        ArgumentNullException.ThrowIfNull(processPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        Seq = seq;
        Kind = kind;
        ProcessPath = processPath;
        Summary = summary;
        Timestamp = timestamp;
        FirstViewedAt = firstViewedAt;
    }
}
