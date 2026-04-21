#if PLATFORM_WINDOWS
using System.Net;
using Beholder.Daemon.Windows;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

/// <summary>
/// Covers the channel-based decoupling between the ETW callback and the cache
/// consumer: enqueue → drain → Resolve round-trip, overflow DropOldest counter,
/// and the graceful-drain path on StopAsync. These don't construct real
/// TraceEvent instances (TraceEvent is an internal flyweight that's impractical
/// to fake); instead they drive the post-decode seam <c>TryEnqueueForTest</c>,
/// which writes the same <c>DnsEventSnapshot</c> the production callback does.
/// </summary>
public class EtwDnsCacheChannelTests {
    private static EtwDnsCache CreateCache(int queueCapacity = 1024) =>
        new(NullLogger<EtwDnsCache>.Instance,
            new FakeOptionsMonitor<DnsOptions>(new DnsOptions { QueueCapacity = queueCapacity }));

    /// <summary>
    /// Polls <paramref name="predicate"/> until it returns true or
    /// <paramref name="timeoutMs"/> elapses. Used instead of a fixed
    /// <c>Task.Delay</c> so fast hardware doesn't waste real time.
    /// </summary>
    private static async Task<bool> WaitForAsync(
        Func<bool> predicate, int timeoutMs = 2000, int stepMs = 10) {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            if (predicate()) return true;
            await Task.Delay(stepMs);
        }
        return predicate();
    }

    [Fact(Skip = "Requires Administrator — real ETW session start. Manual smoke test only.")]
    public void StartAsync_RequiresElevation() { /* documented-only */ }

    [Fact]
    public void DroppedCount_StartsAtZero() {
        var cache = CreateCache();
        Assert.Equal(0, cache.DroppedCount);
    }

    [Fact]
    public void TryEnqueueForTest_BeforeStart_DoesNotThrow_ReturnsFalse() {
        // Queue is null until StartAsync. The test seam returns false instead
        // of throwing, which models the safe "not yet started" state — the
        // production callback path has the same early-return shape.
        var cache = CreateCache();

        var enqueued = cache.TryEnqueueForTest("example.com", "93.184.216.34");

        Assert.False(enqueued);
        Assert.Equal(0, cache.DroppedCount);  // null-queue case doesn't count as a drop
    }

    [Fact]
    public async Task Enqueue_Then_Resolve_RoundTripsThroughConsumer() {
        // The channel-path happy case: write a synthetic event, consumer
        // drains it, Resolve returns the hostname. Exercises OnEtwEvent →
        // queue → ConsumeAsync → Ingest → _cache[ip] = hostname end to end
        // (minus OnEtwEvent's TraceEvent decode, which the test seam skips).
        var cache = CreateCache();
        await StartQueueOnlyAsync(cache);
        try {
            Assert.True(cache.TryEnqueueForTest("example.com", "93.184.216.34"));

            var resolved = await WaitForAsync(
                () => cache.Resolve(IPAddress.Parse("93.184.216.34")) == "example.com");

            Assert.True(resolved);
        } finally {
            await StopQueueOnlyAsync(cache);
        }
    }

    [Fact]
    public async Task QueueCapacity_Overflow_IncrementsDroppedCounter() {
        // Force the DropOldest path: fill the queue faster than the consumer
        // can drain by writing synchronously in a tight loop. The tiny
        // capacity (4) makes the overflow deterministic even on fast
        // consumers — some writes must race ahead of the drain.
        var cache = CreateCache(queueCapacity: 4);
        await StartQueueOnlyAsync(cache);
        try {
            const int totalWrites = 1000;
            for (var i = 0; i < totalWrites; i++) {
                cache.TryEnqueueForTest($"host{i}.example.com", "1.2.3.4");
            }

            // Consumer will drain eventually, but DroppedCount should have
            // grown while the ring was full. Give it a moment to settle, then
            // assert at least one drop happened.
            await WaitForAsync(() => cache.DroppedCount > 0, timeoutMs: 500);

            Assert.True(cache.DroppedCount > 0,
                $"Expected non-zero drops after overflowing a 4-slot queue with 1000 writes; got {cache.DroppedCount}");
        } finally {
            await StopQueueOnlyAsync(cache);
        }
    }

    [Fact]
    public async Task StopAsync_DrainsQueuedEventsBeforeCompleting() {
        // Graceful shutdown contract: enqueue N events, signal Stop, all N
        // must land in the cache before StopAsync returns. Writer.Complete()
        // in StopAsync lets the consumer exhaust the ring before exiting
        // ReadAllAsync.
        var cache = CreateCache();
        await StartQueueOnlyAsync(cache);

        for (var i = 1; i <= 5; i++) {
            Assert.True(cache.TryEnqueueForTest($"host{i}.example.com", $"10.0.0.{i}"));
        }

        await StopQueueOnlyAsync(cache);

        for (var i = 1; i <= 5; i++) {
            Assert.Equal(
                $"host{i}.example.com",
                cache.Resolve(IPAddress.Parse($"10.0.0.{i}")));
        }
    }

    [Fact]
    public async Task Enqueue_IgnoresCnameSegments_ThroughFullPipeline() {
        // End-to-end shape check: a CNAME-interleaved payload like what
        // event 3018 can emit on some Windows builds must only produce a
        // cache entry for the A record, not the CNAME name.
        var cache = CreateCache();
        await StartQueueOnlyAsync(cache);
        try {
            Assert.True(cache.TryEnqueueForTest(
                "www.example.com",
                "type:  5 edge.example.com;type:  1 93.184.216.34"));

            var resolved = await WaitForAsync(
                () => cache.Resolve(IPAddress.Parse("93.184.216.34")) == "www.example.com");

            Assert.True(resolved);
            Assert.Null(cache.Resolve(IPAddress.Parse("0.0.0.1")));
        } finally {
            await StopQueueOnlyAsync(cache);
        }
    }

    /// <summary>
    /// Starts only the queue + consumer (no ETW session) via the
    /// <c>TestStartQueueOnly</c> seam. Real <c>StartAsync</c> needs
    /// Administrator + a live provider, neither available under xUnit.
    /// </summary>
    private static Task StartQueueOnlyAsync(EtwDnsCache cache) {
        cache.TestStartQueueOnly();
        return Task.CompletedTask;
    }

    private static Task StopQueueOnlyAsync(EtwDnsCache cache) =>
        cache.TestStopQueueOnlyAsync();
}
#endif
