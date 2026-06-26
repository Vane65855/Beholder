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
    /// Walks the chain forward from <paramref name="fromSeq"/> (inclusive),
    /// requiring the first row's <c>prev_hash</c> to equal
    /// <paramref name="expectedPrevHash"/> and recomputing every row's hash
    /// from there to the head. <c>VerifyFromAsync(0, ChainHasher.ZeroPrevHash)</c>
    /// is equivalent to <see cref="VerifyAsync"/>. Used by the checkpoint-anchored
    /// verifier to skip rows already attested by a signed checkpoint.
    /// <see cref="ChainVerificationResult.RowsVerified"/> counts only the rows
    /// actually walked (from <paramref name="fromSeq"/> onward).
    /// Side-effect-free and idempotent.
    /// </summary>
    Task<ChainVerificationResult> VerifyFromAsync(
        long fromSeq, byte[] expectedPrevHash, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the stored <c>row_hash</c> for the row at <paramref name="seq"/>,
    /// or null when no such row exists. Used by the checkpoint-anchored verifier
    /// to confirm the live chain's row at a checkpoint's seq still matches the
    /// signed hash before trusting the anchor. Side-effect-free.
    /// </summary>
    Task<byte[]?> TryGetRowHashAsync(long seq, CancellationToken cancellationToken);

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

    /// <summary>
    /// Returns the chain's current head row (latest seq + its row_hash + timestamp)
    /// or <c>null</c> when the chain is empty. Used by the Phase 11 checkpoint
    /// signer to know what to attest. Side-effect-free.
    /// </summary>
    Task<ChainHead?> TryGetChainHeadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns full chain rows (including the hash columns) from
    /// <paramref name="fromSeq"/> to <paramref name="toSeq"/> inclusive, in
    /// ascending seq order. <paramref name="toSeq"/> &lt;= 0 means "to the
    /// chain head". Used by the Phase 11.3 chain exporter to build a signed,
    /// independently-verifiable snapshot of the audit log. Side-effect-free.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="fromSeq"/> is negative, or when both bounds
    /// are positive and <paramref name="fromSeq"/> &gt; <paramref name="toSeq"/>.
    /// </exception>
    Task<IReadOnlyList<EventLogRow>> ReadRangeAsync(
        long fromSeq, long toSeq, CancellationToken cancellationToken);
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

/// <summary>
/// The chain's current head — the highest-seq row in <c>event_log</c>, with
/// its 32-byte <c>row_hash</c> and timestamp. Returned by
/// <see cref="IEventStore.TryGetChainHeadAsync"/>; consumed by the Phase 11
/// checkpoint signer.
/// </summary>
public sealed record ChainHead(long Seq, byte[] RowHash, DateTimeOffset Timestamp);

/// <summary>
/// A complete <c>event_log</c> row, including the chain-hash columns that
/// <see cref="EventLogEntry"/> omits. Returned by
/// <see cref="IEventStore.ReadRangeAsync"/>; consumed by the Phase 11.3 chain
/// exporter, which embeds <see cref="PrevHash"/> and <see cref="RowHash"/> in
/// the export so a receiver can recompute the SHA-256 chain independently of
/// the export's Ed25519 signature.
/// </summary>
public sealed record EventLogRow(
    long Seq,
    DateTimeOffset Timestamp,
    EventKind Kind,
    byte[] Payload,
    byte[] PrevHash,
    byte[] RowHash);
