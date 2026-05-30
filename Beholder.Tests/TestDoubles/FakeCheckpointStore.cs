using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="ICheckpointStore"/> for signer-service tests. Tracks
/// appended checkpoints in insertion order; <see cref="GetLatestAsync"/>
/// returns the highest-seq entry. Tests inspect <see cref="Appended"/> to
/// assert on the signed payload + the seq/hash that was attested.
/// </summary>
internal sealed class FakeCheckpointStore : ICheckpointStore {
    private readonly List<Checkpoint> _checkpoints = new();

    /// <summary>Optional hook: when non-null, the next AppendAsync throws this.</summary>
    public Exception? AppendException { get; set; }

    /// <summary>All checkpoints appended via AppendAsync, in insertion order.</summary>
    public IReadOnlyList<Checkpoint> Appended => _checkpoints;

    /// <summary>Pre-populates the store with a checkpoint (test setup convenience).</summary>
    public void Seed(Checkpoint checkpoint) => _checkpoints.Add(checkpoint);

    public Task<Checkpoint?> GetLatestAsync(CancellationToken cancellationToken) {
        if (_checkpoints.Count == 0) return Task.FromResult<Checkpoint?>(null);
        var latest = _checkpoints.OrderByDescending(c => c.Seq).First();
        return Task.FromResult<Checkpoint?>(latest);
    }

    public Task AppendAsync(Checkpoint checkpoint, CancellationToken cancellationToken) {
        if (AppendException is not null) throw AppendException;
        _checkpoints.Add(checkpoint);
        return Task.CompletedTask;
    }
}
