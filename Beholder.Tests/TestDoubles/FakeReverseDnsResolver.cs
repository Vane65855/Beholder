using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IReverseDnsResolver"/>. Each
/// <see cref="ResolveAsync"/> call reads one answer from a per-IP unbounded
/// channel; if the channel is empty the call blocks until
/// <see cref="EnqueueAnswer"/> writes one (or cancellation fires). The
/// <see cref="WaitForCallAsync"/> hook lets tests assert "the worker has
/// actually entered ResolveAsync" so they can drive timing-sensitive
/// scenarios without <see cref="Task.Delay"/>.
/// </summary>
/// <remarks>
/// Both signal paths use semaphore- / channel-based primitives that are
/// safe regardless of which side runs first. An earlier implementation
/// used <see cref="ConcurrentQueue{T}"/> + per-IP
/// <see cref="TaskCompletionSource"/> waiters; that had a race where a
/// producer signal could land between the consumer's empty-check and
/// waiter creation, leaving the consumer parked on a TCS no one would
/// ever set. Tests passed individually (no concurrent producers) but
/// hung in the full-class run because more tests = more interleaving
/// = the race wins eventually.
/// </remarks>
internal sealed class FakeReverseDnsResolver : IReverseDnsResolver {
    private readonly ConcurrentDictionary<IPAddress, Channel<string?>> _answers = new();
    private readonly ConcurrentDictionary<IPAddress, SemaphoreSlim> _callSemaphores = new();

    /// <summary>Queues one answer for <paramref name="address"/>. Each
    /// <see cref="ResolveAsync"/> consumes one answer; a second call for
    /// the same IP needs a second <see cref="EnqueueAnswer"/>. <c>null</c>
    /// represents a negative result (no PTR / timeout / failure).</summary>
    public void EnqueueAnswer(IPAddress address, string? answer) {
        var channel = _answers.GetOrAdd(address, _ => Channel.CreateUnbounded<string?>());
        channel.Writer.TryWrite(answer);
    }

    /// <summary>Awaits the next <see cref="ResolveAsync"/> entry for
    /// <paramref name="address"/>. Race-safe regardless of order: if the
    /// worker has already entered (and incremented the semaphore) before
    /// the test calls this, the wait returns immediately; if the test
    /// calls first, the next worker entry signals it.</summary>
    public Task WaitForCallAsync(IPAddress address) {
        var sem = _callSemaphores.GetOrAdd(address, _ => new SemaphoreSlim(0, int.MaxValue));
        return sem.WaitAsync();
    }

    public async ValueTask<string?> ResolveAsync(IPAddress address, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(address);

        // Release the per-call semaphore before parking on the channel.
        // Any test currently in WaitForCallAsync sees the release; tests
        // that haven't called WaitForCallAsync yet get the count buffered
        // for whenever they do.
        var sem = _callSemaphores.GetOrAdd(address, _ => new SemaphoreSlim(0, int.MaxValue));
        sem.Release();

        var channel = _answers.GetOrAdd(address, _ => Channel.CreateUnbounded<string?>());
        return await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}
