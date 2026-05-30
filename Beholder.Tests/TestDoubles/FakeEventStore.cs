using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IEventStore"/> for detector tests that need a
/// controllable Verify result without spinning up a real SQLite chain.
/// Tests set <see cref="VerifyResult"/> or <see cref="VerifyException"/>
/// to drive the success / failure / throw paths through
/// <see cref="ChainIntegrityMonitor"/>. Append + ListByKinds round-trip
/// in memory so callers needing a non-trivial chain can build one.
/// </summary>
internal sealed class FakeEventStore : IEventStore {
    private readonly List<EventLogEntry> _entries = new();
    private long _nextSeq = 1;

    public ChainVerificationResult VerifyResult { get; set; } = ChainVerificationResult.Success(0);
    public Exception? VerifyException { get; set; }

    public IReadOnlyList<EventLogEntry> Appended => _entries;

    public Task<long> AppendAsync(
        EventKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken
    ) {
        var seq = _nextSeq++;
        _entries.Add(new EventLogEntry(seq, kind, DateTimeOffset.UtcNow, payload.ToArray()));
        return Task.FromResult(seq);
    }

    public Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken) {
        if (VerifyException is not null) throw VerifyException;
        return Task.FromResult(VerifyResult);
    }

    public Task<IReadOnlyList<EventLogEntry>> ListByKindsAsync(
        IReadOnlyCollection<EventKind> kinds, int limit, CancellationToken cancellationToken
    ) {
        IReadOnlyList<EventLogEntry> filtered = _entries
            .Where(e => kinds.Contains(e.Kind))
            .OrderByDescending(e => e.Seq)
            .Take(limit)
            .ToList();
        return Task.FromResult(filtered);
    }

    public Task<ChainHead?> TryGetChainHeadAsync(CancellationToken cancellationToken) {
        if (_entries.Count == 0) return Task.FromResult<ChainHead?>(null);
        var latest = _entries[^1];
        // No real hash chain in the fake — synthesize a deterministic 32-byte
        // sentinel from the seq so tests that don't care about cryptographic
        // verification still get a valid-shape ChainHead.
        var rowHash = new byte[ChainHasher.HashSize];
        BitConverter.GetBytes(latest.Seq).CopyTo(rowHash, 0);
        return Task.FromResult<ChainHead?>(new ChainHead(latest.Seq, rowHash, latest.Timestamp));
    }
}
