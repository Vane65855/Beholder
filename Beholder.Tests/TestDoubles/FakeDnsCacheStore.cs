using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

internal sealed class FakeDnsCacheStore : IDnsCacheStore {
    public List<(string Address, string Hostname)> UpsertedEntries { get; } = new();

    public Task UpsertBatchAsync(
        IReadOnlyList<(string Address, string Hostname)> entries,
        CancellationToken cancellationToken) {
        UpsertedEntries.AddRange(entries);
        return Task.CompletedTask;
    }

    public Task<string?> ResolveAsync(string address, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<long> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        => Task.FromResult(0L);
}
