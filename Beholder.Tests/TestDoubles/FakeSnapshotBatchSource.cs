using Beholder.Core;
using Beholder.Daemon.Pipeline;

namespace Beholder.Tests.TestDoubles;

internal sealed class FakeSnapshotBatchSource : ISnapshotBatchSource {
    public event Action<IReadOnlyList<CounterSnapshot>>? OnSnapshotBatch;
    public void Fire(IReadOnlyList<CounterSnapshot> batch) => OnSnapshotBatch?.Invoke(batch);
}
