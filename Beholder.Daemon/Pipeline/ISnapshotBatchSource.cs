using Beholder.Core;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Produces per-tick batches of <see cref="CounterSnapshot"/> aggregated from the
/// flow pipeline. Exposed so services that need to fan out snapshot events to
/// external consumers (gRPC streaming, uplink upload) can subscribe without
/// depending on the concrete pipeline type.
/// </summary>
internal interface ISnapshotBatchSource {
    /// <summary>
    /// Fires once per accumulator tick with the batch of snapshots for processes
    /// that had activity during the tick. Handlers run on the accumulator loop
    /// thread and must not block.
    /// </summary>
    event Action<IReadOnlyList<CounterSnapshot>>? OnSnapshotBatch;
}
