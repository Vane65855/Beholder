using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IChainVerifier"/> for tests that drive the
/// <c>VerifyChain</c> RPC and <c>ChainIntegrityMonitor</c> without a real
/// chain + checkpoint store. Set <see cref="Result"/> or <see cref="Exception"/>
/// to control the outcome; <see cref="LastForceFull"/> records what the caller
/// requested so tests can assert the startup-full / periodic-anchor split.
/// </summary>
internal sealed class FakeChainVerifier : IChainVerifier {
    public ChainVerificationResult Result { get; set; } = ChainVerificationResult.Success(0);
    public Exception? Exception { get; set; }
    public int CallCount { get; private set; }
    public bool? LastForceFull { get; private set; }

    public Task<ChainVerificationResult> VerifyAsync(bool forceFull, CancellationToken cancellationToken) {
        CallCount++;
        LastForceFull = forceFull;
        if (Exception is not null) throw Exception;
        return Task.FromResult(Result);
    }
}
