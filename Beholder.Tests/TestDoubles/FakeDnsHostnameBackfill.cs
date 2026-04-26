using System.Collections.Concurrent;
using System.Net;
using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Records every <see cref="BackfillHostnameAsync"/> call so tests can assert
/// the reverse-DNS decorator invokes the persistent-layer backfill exactly
/// when expected. Each call returns <see cref="RowsToReturn"/> by default;
/// set <see cref="ThrowOnNextCall"/> to simulate a SQLite failure.
/// </summary>
internal sealed class FakeDnsHostnameBackfill : IDnsHostnameBackfill {
    private readonly ConcurrentQueue<(IPAddress Address, string Hostname)> _calls = new();

    /// <summary>Calls in invocation order.</summary>
    public IReadOnlyCollection<(IPAddress Address, string Hostname)> Calls => _calls;

    /// <summary>Default count returned from every call. Overrideable for
    /// tests that assert on the worker's logged "(backfilled N rows)" path.</summary>
    public int RowsToReturn { get; set; } = 0;

    /// <summary>If non-null, the next call throws this exception (then the
    /// flag is cleared so subsequent calls succeed). Lets a test exercise
    /// the worker's outer-boundary catch without making every call fail.</summary>
    public Exception? ThrowOnNextCall { get; set; }

    public Task<int> BackfillHostnameAsync(IPAddress address, string hostname, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        if (ThrowOnNextCall is { } toThrow) {
            ThrowOnNextCall = null;
            throw toThrow;
        }

        _calls.Enqueue((address, hostname));
        return Task.FromResult(RowsToReturn);
    }
}
