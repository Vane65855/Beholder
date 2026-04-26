#if PLATFORM_WINDOWS
using System.Net;
using Beholder.Daemon.Windows;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

/// <summary>
/// Covers the decorator semantics of <see cref="ReverseDnsFallbackCache"/>:
/// inner-hit pass-through, kill-switch passthrough, private-IP exclusion,
/// in-flight coalescing, write-back into the inner cache on success,
/// negative-cache cooldown, and shutdown behaviour. The real
/// <c>System.Net.Dns</c> path is not exercised here — that's
/// <see cref="SystemReverseDnsResolver"/>'s territory and is verified by
/// the daemon smoke test in ADR 005.
/// </summary>
public sealed class ReverseDnsFallbackCacheTests {
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress PublicIpv4 = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress PrivateIpv4 = IPAddress.Parse("10.0.0.1");

    private static ReverseDnsFallbackCache CreateCache(
        FakeDnsCache inner,
        FakeReverseDnsResolver resolver,
        FakeTimeProvider timeProvider,
        bool enabled = true
    ) {
        var options = new DnsOptions { EnableReverseDnsFallback = enabled };
        return new ReverseDnsFallbackCache(
            inner: inner,
            ingest: inner,
            resolver: resolver,
            options: new FakeOptionsMonitor<DnsOptions>(options),
            timeProvider: timeProvider,
            logger: NullLogger<ReverseDnsFallbackCache>.Instance);
    }

    /// <summary>
    /// Spins on the inner cache until it returns <paramref name="expected"/>
    /// (or 5 s elapses, in which case the test fails). Used after seeding the
    /// resolver with a positive answer to wait for the worker to write back.
    /// </summary>
    private static async Task WaitForResolutionAsync(FakeDnsCache inner, IPAddress address, string expected) {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline) {
            if (inner.Resolve(address) == expected) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.Fail($"Inner cache did not receive '{expected}' for {address} within 5 s");
    }

    /// <summary>
    /// Polls the decorator's internal completion counter until it reaches
    /// <paramref name="target"/>. The counter increments inside the worker's
    /// finally block, after _pending is cleared and _negative is written
    /// (see <c>ProcessOneAsync</c> ordering comment) — once the counter
    /// reaches the target, all per-lookup state has settled and tests can
    /// assert against it deterministically without sleeping.
    /// </summary>
    private static async Task WaitForLookupsAttemptedAsync(ReverseDnsFallbackCache cache, int target) {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline) {
            if (cache.LookupsAttempted >= target) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.Fail($"LookupsAttempted did not reach {target} within 5 s (current={cache.LookupsAttempted})");
    }

    [Fact]
    public void Resolve_InnerHit_ReturnsInnerValue() {
        var inner = new FakeDnsCache();
        inner.Add(PublicIpv4.ToString(), "known.example.com");
        var resolver = new FakeReverseDnsResolver();
        using var cache = CreateCache(inner, resolver, new FakeTimeProvider(FixedTimestamp));

        var result = cache.Resolve(PublicIpv4);

        Assert.Equal("known.example.com", result);
    }

    [Fact]
    public async Task Resolve_DisabledOption_PassesThroughWithoutEnqueue() {
        var inner = new FakeDnsCache();
        var resolver = new FakeReverseDnsResolver();
        using var cache = CreateCache(inner, resolver, new FakeTimeProvider(FixedTimestamp), enabled: false);
        await cache.StartAsync(CancellationToken.None);

        // No EnqueueAnswer — if the worker called ResolveAsync it would block
        // indefinitely. The fact that StopAsync returns cleanly proves no
        // call ever entered the worker.
        Assert.Null(cache.Resolve(PublicIpv4));
        Assert.Null(inner.Resolve(PublicIpv4));

        await cache.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Resolve_PrivateAddressNeverEnqueued() {
        var inner = new FakeDnsCache();
        var resolver = new FakeReverseDnsResolver();
        using var cache = CreateCache(inner, resolver, new FakeTimeProvider(FixedTimestamp));
        await cache.StartAsync(CancellationToken.None);

        Assert.Null(cache.Resolve(PrivateIpv4));
        Assert.Null(inner.Resolve(PrivateIpv4));

        await cache.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Resolve_PublicMiss_EnqueuesAndIngestsOnSuccess() {
        var inner = new FakeDnsCache();
        var resolver = new FakeReverseDnsResolver();
        resolver.EnqueueAnswer(PublicIpv4, "rdns.example.com");
        using var cache = CreateCache(inner, resolver, new FakeTimeProvider(FixedTimestamp));
        await cache.StartAsync(CancellationToken.None);

        // First call returns null (lookup is async).
        Assert.Null(cache.Resolve(PublicIpv4));

        // Worker eventually writes the resolved name into the inner cache.
        await WaitForResolutionAsync(inner, PublicIpv4, "rdns.example.com");

        // Second call hits the inner cache directly (decorator's first
        // branch), confirming the resolved name is now visible.
        Assert.Equal("rdns.example.com", cache.Resolve(PublicIpv4));

        await cache.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Resolve_PendingIp_DoesNotReEnqueue() {
        var inner = new FakeDnsCache();
        var resolver = new FakeReverseDnsResolver();
        using var cache = CreateCache(inner, resolver, new FakeTimeProvider(FixedTimestamp));
        await cache.StartAsync(CancellationToken.None);

        // First call enqueues. The resolver's queue is empty, so the worker
        // enters ResolveAsync and parks on the answer-waiter.
        Assert.Null(cache.Resolve(PublicIpv4));
        await resolver.WaitForCallAsync(PublicIpv4);

        // Second call with the worker already in flight — must short-circuit
        // on the _pending check, not enqueue again. We can't directly observe
        // the queue, so instead: enqueue exactly one answer, wait for the
        // resolution, and stop. If a second enqueue had snuck through we'd
        // need a second answer to avoid hanging StopAsync.
        Assert.Null(cache.Resolve(PublicIpv4));
        resolver.EnqueueAnswer(PublicIpv4, "single.example.com");

        await WaitForResolutionAsync(inner, PublicIpv4, "single.example.com");

        await cache.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Resolve_AfterFailure_NegativeCooldownPreventsReEnqueue() {
        var inner = new FakeDnsCache();
        var resolver = new FakeReverseDnsResolver();
        var time = new FakeTimeProvider(FixedTimestamp);
        using var cache = CreateCache(inner, resolver, time);
        await cache.StartAsync(CancellationToken.None);

        // Worker resolves to null → records negative entry at time T0.
        resolver.EnqueueAnswer(PublicIpv4, null);
        Assert.Null(cache.Resolve(PublicIpv4));

        // Wait deterministically: once LookupsAttempted reaches 1, the
        // worker has already cleared _pending and written _negative.
        await WaitForLookupsAttemptedAsync(cache, 1);

        // A follow-up Resolve must NOT enqueue (cooldown is in effect). If
        // it did, the resolver would block waiting for an answer and
        // StopAsync would have to wait out the grace period to time out.
        Assert.Null(cache.Resolve(PublicIpv4));
        Assert.Null(inner.Resolve(PublicIpv4));
        Assert.Equal(1, cache.LookupsAttempted); // no second lookup happened

        // StopAsync completes promptly because no second worker call is
        // outstanding.
        var stopTask = cache.StopAsync(CancellationToken.None);
        var winner = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(stopTask, winner);
    }

    [Fact]
    public async Task Resolve_NegativeCacheExpired_ReEnqueues() {
        var inner = new FakeDnsCache();
        var resolver = new FakeReverseDnsResolver();
        var time = new FakeTimeProvider(FixedTimestamp);
        using var cache = CreateCache(inner, resolver, time);
        await cache.StartAsync(CancellationToken.None);

        // First attempt: fail → negative entry written at T0.
        resolver.EnqueueAnswer(PublicIpv4, null);
        Assert.Null(cache.Resolve(PublicIpv4));
        await WaitForLookupsAttemptedAsync(cache, 1);

        // Advance past the 30-min cooldown.
        time.Advance(TimeSpan.FromMinutes(31));

        // Second attempt: cooldown elapsed → re-enqueue. Resolver returns
        // a positive answer this time.
        resolver.EnqueueAnswer(PublicIpv4, "later.example.com");
        Assert.Null(cache.Resolve(PublicIpv4));

        await WaitForResolutionAsync(inner, PublicIpv4, "later.example.com");
        Assert.Equal(2, cache.LookupsAttempted);

        await cache.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsWorkerEvenIfQueryNeverCompletes() {
        var inner = new FakeDnsCache();
        var resolver = new FakeReverseDnsResolver();
        using var cache = CreateCache(inner, resolver, new FakeTimeProvider(FixedTimestamp));
        await cache.StartAsync(CancellationToken.None);

        // Enqueue a lookup but never EnqueueAnswer — the resolver parks
        // forever waiting for an answer.
        Assert.Null(cache.Resolve(PublicIpv4));
        await resolver.WaitForCallAsync(PublicIpv4);

        // StopAsync cancels the worker; the resolver's await on the
        // cancellation-aware waiter throws OCE; the loop exits cleanly.
        // Must complete well within the 5 s grace period.
        var stopTask = cache.StopAsync(CancellationToken.None);
        var winner = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(7)));
        Assert.Same(stopTask, winner);
    }

    [Fact]
    public void Resolve_NullAddress_Throws() {
        var inner = new FakeDnsCache();
        var resolver = new FakeReverseDnsResolver();
        using var cache = CreateCache(inner, resolver, new FakeTimeProvider(FixedTimestamp));

        Assert.Throws<ArgumentNullException>(() => cache.Resolve(null!));
    }
}
#endif
