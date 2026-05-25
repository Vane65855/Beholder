using Beholder.Core;

namespace Beholder.Tests;

/// <summary>
/// Minimal in-memory <see cref="IChainStatusCache"/> for tests. Mirrors the
/// production <c>ChainStatusCache</c> shape but exposes the captured
/// <see cref="UpdateCalls"/> list so tests can assert that the writer wired
/// the cache correctly without needing a real periodic loop to run.
/// </summary>
internal sealed class FakeChainStatusCache : IChainStatusCache {
    public ChainStatus? Current { get; private set; }

    public List<(ChainVerificationResult Result, DateTimeOffset VerifiedAt)> UpdateCalls { get; } = new();

    public void Update(ChainVerificationResult result, DateTimeOffset verifiedAt) {
        ArgumentNullException.ThrowIfNull(result);
        Current = new ChainStatus(verifiedAt, result);
        UpdateCalls.Add((result, verifiedAt));
    }
}
