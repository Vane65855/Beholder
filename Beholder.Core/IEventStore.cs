namespace Beholder.Core;

/// <summary>
/// The chain-hashed append-only event log. The single point of truth for any state
/// change that needs tamper evidence — counters, alerts, firewall rule mutations.
/// Implementations are responsible for computing each row's hash from the canonical
/// payload bytes plus the previous row's hash, and for never deleting or updating
/// existing rows.
/// </summary>
public interface IEventStore {
    /// <summary>
    /// Appends an event to the log. The implementation computes the chain hash from
    /// <paramref name="payload"/> and the most recent row's hash, then writes the new
    /// row atomically. Callers must supply the canonical byte representation of the
    /// event — the store does not serialize. Returns the sequence number assigned to
    /// the appended row, so callers can build a downstream payload (e.g. broadcast a
    /// live alert) carrying the chain seq without a second database round-trip.
    /// </summary>
    Task<long> AppendAsync(EventKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>
    /// Walks the entire chain from the first row to the last, recomputing each row's
    /// hash and comparing it against the stored value. Side-effect-free and idempotent
    /// — running it twice produces identical results.
    /// </summary>
    Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent <paramref name="limit"/> events whose kind appears in
    /// <paramref name="kinds"/>, ordered newest-first by sequence number. Used by
    /// the Firewall tab's activity strip and any future audit views that need a
    /// kind-filtered slice of the chain. Negative or zero limits return an empty
    /// result; an empty kinds list does the same. Callers receive raw payload bytes —
    /// decoding is the caller's responsibility.
    /// </summary>
    Task<IReadOnlyList<EventLogEntry>> ListByKindsAsync(
        IReadOnlyCollection<EventKind> kinds, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// One row from <see cref="IEventStore.ListByKindsAsync"/>. Mirrors the schema
/// fields the activity strip needs: chain sequence, kind, timestamp, raw payload.
/// The chain hash columns are intentionally omitted — chain integrity is queried
/// via <see cref="IEventStore.VerifyAsync"/> separately.
/// </summary>
public sealed record EventLogEntry(
    long Seq,
    EventKind Kind,
    DateTimeOffset Timestamp,
    byte[] Payload);
