using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

internal sealed class FakeTrafficStore : ITrafficStore {
    public List<TrafficBucket> WrittenBuckets { get; } = new();

    public Task WriteBucketsAsync(IReadOnlyList<TrafficBucket> buckets, CancellationToken cancellationToken) {
        WrittenBuckets.AddRange(buckets);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TrafficTimePoint>> GetProcessTimelineAsync(
        string processPath, DateTimeOffset from, DateTimeOffset to,
        TimeSpan resolution, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TrafficTimePoint>>([]);

    public Task<IReadOnlyList<DestinationSummary>> GetProcessDestinationsAsync(
        string processPath, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DestinationSummary>>([]);

    public Task<IReadOnlyList<TrafficTimePoint>> GetAggregateTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, TimeSpan resolution,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TrafficTimePoint>>([]);

    public Task<IReadOnlyList<CountryTrafficSummary>> GetCountryBreakdownAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CountryTrafficSummary>>([]);

    public Task<long> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        => Task.FromResult(0L);
}
