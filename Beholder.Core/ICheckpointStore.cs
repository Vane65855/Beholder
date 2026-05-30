namespace Beholder.Core;

/// <summary>
/// Persistence for signed chain checkpoints (Phase 11). Mirrors
/// <see cref="IFirewallRuleStore"/>'s shape — one row per appended checkpoint,
/// keyed on <see cref="Checkpoint.Seq"/>. Append-only; checkpoints are never
/// updated or deleted, so older rows remain valid anchors for chain export
/// verification long after the chain has moved past them.
/// </summary>
public interface ICheckpointStore {
    /// <summary>
    /// Returns the highest-seq checkpoint, or <c>null</c> when the
    /// <c>checkpoint</c> table is empty (fresh install, or no signing has run
    /// yet).
    /// </summary>
    Task<Checkpoint?> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inserts <paramref name="checkpoint"/>. Throws if a checkpoint already
    /// exists at <see cref="Checkpoint.Seq"/> — the signer's tick logic must
    /// guarantee monotonic seq before calling.
    /// </summary>
    Task AppendAsync(Checkpoint checkpoint, CancellationToken cancellationToken);
}
