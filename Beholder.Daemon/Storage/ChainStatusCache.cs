using Beholder.Core;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IChainStatusCache"/>.
/// A single <c>volatile</c> reference write per <see cref="Update"/> call
/// makes reads lock-free — sufficient for the expected write rate (one per
/// chain-verify interval plus the rare user-triggered verify).
/// </summary>
internal sealed class ChainStatusCache : IChainStatusCache {
    private volatile ChainStatus? _current;

    public ChainStatus? Current => _current;

    public void Update(ChainVerificationResult result, DateTimeOffset verifiedAt) {
        ArgumentNullException.ThrowIfNull(result);
        _current = new ChainStatus(verifiedAt, result);
    }
}
