using System.Collections.Concurrent;
using System.Net;
using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IReverseDnsResolver"/>. Each
/// <see cref="ResolveAsync"/> call dequeues one pre-configured answer per IP
/// (set via <see cref="EnqueueAnswer"/>); if no answer is queued the call
/// blocks until <see cref="EnqueueAnswer"/> or cancellation. The
/// <see cref="WaitForCallAsync"/> hook lets tests assert "the worker has
/// actually entered ResolveAsync" so they can drive timing-sensitive
/// scenarios (in-flight coalescing, stop-while-pending) without
/// <see cref="Task.Delay"/>.
/// </summary>
internal sealed class FakeReverseDnsResolver : IReverseDnsResolver {
    private readonly ConcurrentDictionary<IPAddress, ConcurrentQueue<string?>> _answers = new();
    private readonly ConcurrentDictionary<IPAddress, TaskCompletionSource> _pendingAnswers = new();
    private readonly ConcurrentDictionary<IPAddress, TaskCompletionSource> _calls = new();

    /// <summary>Queues one answer for <paramref name="address"/>. Each
    /// <see cref="ResolveAsync"/> consumes one answer; a second
    /// <see cref="ResolveAsync"/> for the same IP needs a second
    /// <see cref="EnqueueAnswer"/>. <c>null</c> represents a negative
    /// result (no PTR / timeout / failure).</summary>
    public void EnqueueAnswer(IPAddress address, string? answer) {
        var queue = _answers.GetOrAdd(address, _ => new ConcurrentQueue<string?>());
        queue.Enqueue(answer);
        // Release any ResolveAsync that's already waiting for an answer.
        if (_pendingAnswers.TryRemove(address, out var pending)) {
            pending.TrySetResult();
        }
    }

    /// <summary>Returns a task that completes when the worker enters
    /// <see cref="ResolveAsync"/> for <paramref name="address"/>. Reset
    /// after each call so the same IP can be awaited multiple times across
    /// sequential lookups.</summary>
    public Task WaitForCallAsync(IPAddress address) {
        var tcs = _calls.GetOrAdd(address, _ => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously));
        return tcs.Task;
    }

    public async ValueTask<string?> ResolveAsync(IPAddress address, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(address);

        // Signal the call entry. Reset for any future caller.
        if (_calls.TryRemove(address, out var callTcs)) {
            callTcs.TrySetResult();
        }

        var queue = _answers.GetOrAdd(address, _ => new ConcurrentQueue<string?>());
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            if (queue.TryDequeue(out var answer)) {
                return answer;
            }
            // No answer queued — wait until EnqueueAnswer (or cancellation).
            var waiter = _pendingAnswers.GetOrAdd(address, _ => new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously));
            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            await waiter.Task.ConfigureAwait(false);
            // Loop and try the queue again.
        }
    }
}
