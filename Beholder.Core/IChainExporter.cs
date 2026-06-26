namespace Beholder.Core;

/// <summary>
/// Builds a signed, self-verifying export of the chain-hashed event log
/// (Phase 11.3). The output is a UTF-8 JSON envelope: a metadata header plus
/// every event in the requested range (with its <c>prev_hash</c> /
/// <c>row_hash</c>), signed end-to-end with the daemon's Ed25519 checkpoint
/// key. A receiver can verify both that the file is authentic (the signature
/// over the canonical body) and that it is internally consistent (recomputing
/// each row's SHA-256 against the embedded hash chain).
/// </summary>
public interface IChainExporter {
    /// <summary>
    /// Serialises <paramref name="rows"/> into a signed JSON export envelope.
    /// <paramref name="fromSeq"/> and <paramref name="toSeq"/> are recorded in
    /// the envelope metadata (<paramref name="toSeq"/> = 0 means "to head");
    /// they describe the requested range, not a second filter — the caller has
    /// already read the matching rows. <paramref name="daemonVersion"/> is
    /// stamped in the metadata for provenance. Returns the canonical UTF-8 JSON
    /// bytes. Side-effect-free.
    /// </summary>
    byte[] Export(
        IReadOnlyList<EventLogRow> rows,
        long fromSeq,
        long toSeq,
        DateTimeOffset exportedAt,
        string daemonVersion);
}
