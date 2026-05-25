using Beholder.Core;

namespace Beholder.Tests;

/// <summary>
/// In-memory <see cref="IStorageStatsProvider"/> for tests. Returns a canned
/// <see cref="StorageStats"/> snapshot (default: zero tables, zero bytes,
/// no chain status) and lets the test rewrite the canned response via
/// <see cref="Response"/>. Throwing tests set <see cref="Exception"/> to
/// drive the RPC error path.
/// </summary>
internal sealed class FakeStorageStatsProvider : IStorageStatsProvider {
    public StorageStats Response { get; set; } = new StorageStats(
        DatabasePath: string.Empty,
        DatabaseBytesTotal: 0,
        Tables: Array.Empty<TableStats>(),
        ChainStatus: null,
        ChainFirstEventAt: null,
        DaemonStartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LanDeviceCount: 0);

    public Exception? Exception { get; set; }
    public int CallCount { get; private set; }

    public Task<StorageStats> GetAsync(CancellationToken cancellationToken) {
        CallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (Exception is not null) throw Exception;
        return Task.FromResult(Response);
    }
}
