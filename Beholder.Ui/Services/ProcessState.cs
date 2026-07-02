using System;
using Beholder.Ui.Helpers;

namespace Beholder.Ui.Services;

/// <summary>
/// Per-process traffic state maintained by <see cref="ProcessStateService"/>.
/// Tracks lifetime totals, per-tick deltas, and a 5-minute sliding window of rate history.
/// </summary>
internal sealed class ProcessState {
    /// <summary>
    /// Capacity of the live rate-history window: 300 samples = 5 minutes at
    /// 1 sample/sec. The live chart always renders exactly this many samples
    /// (zero-padded at the front for young processes) so its time axis stays
    /// pinned at −5:00 → now instead of stretching as a buffer fills.
    /// </summary>
    public const int RecentWindowSampleCount = 300;

    public required string ProcessPath { get; init; }
    public required string DisplayName { get; init; }
    public long TotalBytesIn { get; set; }
    public long TotalBytesOut { get; set; }
    public long DeltaBytesIn { get; set; }
    public long DeltaBytesOut { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// Number of currently-open TCP connections for this process at the
    /// most recent counter snapshot. Sourced from
    /// <see cref="Beholder.Protocol.Local.CounterSnapshot.ActiveConnectionCount"/>,
    /// already on the wire — no extra RPC needed. The Firewall tab's HOSTS
    /// column reads this for active rows.
    /// </summary>
    public int ActiveConnectionCount { get; set; }

    /// <summary>See <see cref="RecentWindowSampleCount"/>.</summary>
    public CircularBuffer<long> RecentDeltaIn { get; } = new(RecentWindowSampleCount);

    /// <summary>See <see cref="RecentWindowSampleCount"/>.</summary>
    public CircularBuffer<long> RecentDeltaOut { get; } = new(RecentWindowSampleCount);
}
