using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public class ChainStatusCacheTests {
    private static readonly DateTimeOffset T0 = new(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Current_InitialState_IsNull() {
        var cache = new ChainStatusCache();
        Assert.Null(cache.Current);
    }

    [Fact]
    public void Update_StoresLatestResult() {
        var cache = new ChainStatusCache();
        var result = ChainVerificationResult.Success(rowsVerified: 100);

        cache.Update(result, T0);

        Assert.NotNull(cache.Current);
        Assert.Equal(T0, cache.Current!.LastVerifiedAt);
        Assert.Same(result, cache.Current.Result);
    }

    [Fact]
    public void Update_OverwritesPriorValue() {
        var cache = new ChainStatusCache();
        cache.Update(ChainVerificationResult.Success(rowsVerified: 1), T0);

        var failedResult = ChainVerificationResult.Failure(
            rowsVerified: 5, failedAtSeq: 3, errorMessage: "hash mismatch");
        var t1 = T0.AddMinutes(5);
        cache.Update(failedResult, t1);

        Assert.NotNull(cache.Current);
        Assert.Equal(t1, cache.Current!.LastVerifiedAt);
        Assert.False(cache.Current.Result.IsValid);
        Assert.Equal(3, cache.Current.Result.FailedAtSeq);
    }

    [Fact]
    public void Update_NullResult_Throws() {
        var cache = new ChainStatusCache();
        Assert.Throws<ArgumentNullException>(
            () => cache.Update(null!, T0));
    }

    [Fact]
    public async Task Update_ConcurrentWriters_AllWritesSettle() {
        // Volatile-reference-write semantics: every writer's update is
        // atomic; reads after all writers complete observe the most recent
        // value. We don't enforce a specific winner because that would
        // depend on the OS scheduler — we just assert no torn writes and
        // that Current is non-null after the storm.
        var cache = new ChainStatusCache();
        var writers = Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => {
                for (var j = 0; j < 1000; j++) {
                    cache.Update(
                        ChainVerificationResult.Success(rowsVerified: i * 1000 + j),
                        T0.AddSeconds(i));
                }
            }))
            .ToArray();
        await Task.WhenAll(writers);

        Assert.NotNull(cache.Current);
        Assert.True(cache.Current!.Result.IsValid);
        Assert.InRange(cache.Current.Result.RowsVerified, 0, 16 * 1000);
    }
}
