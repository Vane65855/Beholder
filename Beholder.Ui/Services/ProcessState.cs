using System;
using Beholder.Ui.Helpers;

namespace Beholder.Ui.Services;

/// <summary>
/// Per-process traffic state maintained by <see cref="ProcessStateService"/>.
/// Tracks lifetime totals, per-tick deltas, and a 5-minute sliding window of rate history.
/// </summary>
internal sealed class ProcessState {
    public required string ProcessPath { get; init; }
    public required string DisplayName { get; init; }
    public long TotalBytesIn { get; set; }
    public long TotalBytesOut { get; set; }
    public long DeltaBytesIn { get; set; }
    public long DeltaBytesOut { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>300 samples = 5 minutes at 1 sample/sec.</summary>
    public CircularBuffer<long> RecentDeltaIn { get; } = new(300);

    /// <summary>300 samples = 5 minutes at 1 sample/sec.</summary>
    public CircularBuffer<long> RecentDeltaOut { get; } = new(300);
}
