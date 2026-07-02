using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Grpc.Core;

namespace Beholder.Ui.Services;

/// <summary>
/// Shared singleton that tracks per-process traffic state from the daemon's live stream.
/// Both <c>StatusStripViewModel</c> and <c>TrafficTabViewModel</c> consume this service,
/// eliminating duplicated per-process tracking logic.
/// </summary>
/// <remarks>
/// <para>
/// On daemon connect, <see cref="SeedAsync"/> pre-populates per-process state from the
/// daemon's <c>GetSnapshot</c> + per-process <c>GetProcessTimeline</c> RPCs. This ensures
/// the UI renders correct 5-minute history immediately rather than starting from zero.
/// </para>
/// <para>
/// The <see cref="ProcessStatesUpdated"/> event fires on the <see cref="DaemonStreamSubscriber"/>'s
/// background thread. Consumers must marshal to the UI thread via <c>Dispatcher.UIThread.Post</c>.
/// </para>
/// </remarks>
internal sealed class ProcessStateService : IDisposable {
    private readonly IDaemonClient _daemonClient;
    private readonly DaemonStreamSubscriber _subscriber;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ProcessState> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// Tick timestamp of the most recent counter batch (or the seed's "now"),
    /// in Unix nanoseconds. 0 = unknown (no batch yet, or a daemon that
    /// doesn't stamp ticks). Baseline for <see cref="OnCounterBatch"/>'s
    /// gap-fill: the live chart assumes 1 buffer sample = 1 wall-clock
    /// second, so seconds with no received batch (dropped by the broadcast
    /// channel, OS sleep, old daemon skipping idle ticks) must be
    /// backfilled with zero samples or the chart's time axis silently
    /// compresses (ADR 017).
    /// </summary>
    private long _lastTickUnixNs;

    /// <summary>
    /// Fired after each <see cref="CounterBatch"/> is processed or after
    /// <see cref="SeedAsync"/> populates historical state.
    /// The dictionary is a defensive snapshot — safe to read on any thread.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, ProcessState>>? ProcessStatesUpdated;

    public ProcessStateService(
        DaemonStreamSubscriber subscriber,
        IDaemonClient daemonClient,
        TimeProvider timeProvider
    ) {
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _subscriber = subscriber;
        _daemonClient = daemonClient;
        _timeProvider = timeProvider;
        _subscriber.CounterBatchReceived += OnCounterBatch;
    }

    public void Dispose() {
        _subscriber.CounterBatchReceived -= OnCounterBatch;
    }

    /// <summary>
    /// Seeds per-process state from daemon historical data. Called once per
    /// connection, after the daemon is connected but before the live event
    /// stream starts. This eliminates the "0 B on reconnect" problem.
    /// Best-effort: if any query fails, the live stream fills in within seconds.
    /// </summary>
    internal async Task SeedAsync(CancellationToken cancellationToken) {
        try {
            var snapshot = await _daemonClient.GetSnapshotAsync(cancellationToken);
            if (snapshot.Snapshots.Count == 0) return;

            _states.Clear();
            var now = _timeProvider.GetUtcNow();
            var from = now.AddMinutes(-5);

            foreach (var snap in snapshot.Snapshots) {
                var state = new ProcessState {
                    ProcessPath = snap.ProcessPath,
                    DisplayName = snap.ProcessName,
                };
                state.TotalBytesIn = snap.TotalBytesIn;
                state.TotalBytesOut = snap.TotalBytesOut;
                state.DeltaBytesIn = snap.DeltaBytesIn;
                state.DeltaBytesOut = snap.DeltaBytesOut;
                state.ActiveConnectionCount = snap.ActiveConnectionCount;
                state.LastSeen = now;
                _states[snap.ProcessPath] = state;

                // Backfill the 5-minute circular buffer from per-process
                // historical timeline (1-second buckets from traffic_raw,
                // gap-free — the stitcher zero-fills idle seconds inside the
                // data extent per ADR 017).
                try {
                    var request = new GetProcessTimelineRequest {
                        ProcessPath = snap.ProcessPath,
                        FromUnixNs = from.ToUnixTimeMilliseconds() * 1_000_000,
                        ToUnixNs = now.ToUnixTimeMilliseconds() * 1_000_000,
                        ResolutionMs = 1_000,
                    };
                    var timeline = await _daemonClient.GetProcessTimelineAsync(request, cancellationToken);
                    foreach (var point in timeline.Points) {
                        state.RecentDeltaIn.Add(point.BytesIn);
                        state.RecentDeltaOut.Add(point.BytesOut);
                    }
                    AppendTrailingZerosUpTo(state, timeline.Points, now);
                } catch (OperationCanceledException) {
                    throw;  // propagate to the outer try so SeedAsync honors its CT
                } catch (RpcException) {
                    // Per-process backfill is best-effort — live stream fills in.
                }
            }

            // Seeded buffers are aligned to `now`; make the first live batch's
            // gap-fill measure from the same instant instead of double-filling.
            _lastTickUnixNs = now.ToUnixTimeMilliseconds() * 1_000_000;

            ProcessStatesUpdated?.Invoke(_states);
        } catch (OperationCanceledException) {
            // Surface cancellation to the subscriber loop's OnConnected callback
            // so shutdown/reconnect signals aren't muted by the best-effort catch.
            throw;
        } catch (RpcException) {
            // Snapshot seeding is best-effort — live stream will fill in.
        }
    }

    internal void OnCounterBatch(CounterBatch batch) {
        // Detect daemon restart: if any snapshot's total is less than stored, clear all state
        foreach (var snapshot in batch.Snapshots) {
            if (_states.TryGetValue(snapshot.ProcessPath, out var existing)
                && snapshot.TotalBytesIn < existing.TotalBytesIn) {
                _states.Clear();
                break;
            }
        }

        BackfillMissedSeconds(batch.TickTimestampUnixNs);

        // Track which processes appeared in this batch so we can push zero deltas
        // for processes that didn't report in this tick
        var seenPaths = new HashSet<string>(batch.Snapshots.Count, StringComparer.Ordinal);

        foreach (var snapshot in batch.Snapshots) {
            seenPaths.Add(snapshot.ProcessPath);

            if (!_states.TryGetValue(snapshot.ProcessPath, out var state)) {
                state = new ProcessState {
                    ProcessPath = snapshot.ProcessPath,
                    DisplayName = snapshot.ProcessName,
                };
                _states[snapshot.ProcessPath] = state;
            }

            state.TotalBytesIn = snapshot.TotalBytesIn;
            state.TotalBytesOut = snapshot.TotalBytesOut;
            state.DeltaBytesIn = snapshot.DeltaBytesIn;
            state.DeltaBytesOut = snapshot.DeltaBytesOut;
            state.ActiveConnectionCount = snapshot.ActiveConnectionCount;
            state.LastSeen = _timeProvider.GetUtcNow();
            state.RecentDeltaIn.Add(snapshot.DeltaBytesIn);
            state.RecentDeltaOut.Add(snapshot.DeltaBytesOut);
        }

        // Push zero deltas for processes not in this batch (they're idle this tick).
        // ActiveConnectionCount also drops to zero — a process that didn't report
        // this tick has no live snapshot of its connections to us.
        foreach (var kvp in _states) {
            if (!seenPaths.Contains(kvp.Key)) {
                kvp.Value.DeltaBytesIn = 0;
                kvp.Value.DeltaBytesOut = 0;
                kvp.Value.ActiveConnectionCount = 0;
                kvp.Value.RecentDeltaIn.Add(0);
                kvp.Value.RecentDeltaOut.Add(0);
            }
        }

        // Fire with a snapshot reference — the dictionary is only mutated on this
        // same callback thread (DaemonStreamSubscriber's consume loop), so consumers
        // reading on the UI thread after Post() see a consistent state.
        ProcessStatesUpdated?.Invoke(_states);
    }

    /// <summary>
    /// Pads a seeded buffer with zero samples for the seconds between the
    /// timeline's last bucket and <paramref name="now"/>, so the buffer's
    /// right edge means "now" rather than "whenever this process last moved
    /// bytes" — without this, a process idle for the last N seconds renders
    /// its stale activity at the chart's right edge (ADR 017). An empty
    /// timeline needs no padding: an empty buffer already renders flat.
    /// </summary>
    private static void AppendTrailingZerosUpTo(
        ProcessState state,
        IReadOnlyList<TrafficTimePoint> points,
        DateTimeOffset now
    ) {
        if (points.Count == 0) return;
        var lastBucketUnixNs = points[^1].TimestampUnixNs;
        var nowUnixNs = now.ToUnixTimeMilliseconds() * 1_000_000;
        var elapsedSeconds = (long)((nowUnixNs - lastBucketUnixNs) / 1_000_000_000.0);
        var missingSamples = (int)Math.Clamp(
            elapsedSeconds - 1, 0, ProcessState.RecentWindowSampleCount);
        for (var i = 0; i < missingSamples; i++) {
            state.RecentDeltaIn.Add(0);
            state.RecentDeltaOut.Add(0);
        }
    }

    /// <summary>
    /// Appends one zero sample per wall-clock second that elapsed between the
    /// previous tick and <paramref name="tickUnixNs"/> without a batch
    /// arriving, to every tracked state's rate buffers. Keeps the buffers
    /// 1 sample = 1 second regardless of dropped batches, OS sleep/resume, or
    /// daemons that skip idle ticks. A zero or unknown timestamp on either
    /// side disables the fill (old daemons, first batch); a backwards clock
    /// step just resets the baseline. Capped at the buffer window — a longer
    /// gap zeroes the whole visible history anyway.
    /// </summary>
    private void BackfillMissedSeconds(long tickUnixNs) {
        if (tickUnixNs <= 0) return;
        var previousTickUnixNs = _lastTickUnixNs;
        _lastTickUnixNs = tickUnixNs;
        if (previousTickUnixNs <= 0) return;

        var elapsedSeconds = (long)Math.Round((tickUnixNs - previousTickUnixNs) / 1_000_000_000.0);
        var missedSeconds = (int)Math.Clamp(
            elapsedSeconds - 1, 0, ProcessState.RecentWindowSampleCount);
        if (missedSeconds == 0) return;

        foreach (var state in _states.Values) {
            for (var i = 0; i < missedSeconds; i++) {
                state.RecentDeltaIn.Add(0);
                state.RecentDeltaOut.Add(0);
            }
        }
    }

    /// <summary>
    /// Returns the current number of tracked processes. Exposed for testing.
    /// </summary>
    internal int TrackedProcessCount => _states.Count;
}
