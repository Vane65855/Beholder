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
    private readonly Dictionary<string, ProcessState> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// Fired after each <see cref="CounterBatch"/> is processed or after
    /// <see cref="SeedAsync"/> populates historical state.
    /// The dictionary is a defensive snapshot — safe to read on any thread.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, ProcessState>>? ProcessStatesUpdated;

    public ProcessStateService(DaemonStreamSubscriber subscriber, IDaemonClient daemonClient) {
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(daemonClient);
        _subscriber = subscriber;
        _daemonClient = daemonClient;
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
            var now = DateTimeOffset.UtcNow;
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
                state.LastSeen = now;
                _states[snap.ProcessPath] = state;

                // Backfill the 5-minute circular buffer from per-process
                // historical timeline (1-second resolution from traffic_raw).
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
                } catch (OperationCanceledException) {
                    throw;  // propagate to the outer try so SeedAsync honors its CT
                } catch (RpcException) {
                    // Per-process backfill is best-effort — live stream fills in.
                }
            }

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
            state.LastSeen = DateTimeOffset.UtcNow;
            state.RecentDeltaIn.Add(snapshot.DeltaBytesIn);
            state.RecentDeltaOut.Add(snapshot.DeltaBytesOut);
        }

        // Push zero deltas for processes not in this batch (they're idle this tick)
        foreach (var kvp in _states) {
            if (!seenPaths.Contains(kvp.Key)) {
                kvp.Value.DeltaBytesIn = 0;
                kvp.Value.DeltaBytesOut = 0;
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
    /// Returns the current number of tracked processes. Exposed for testing.
    /// </summary>
    internal int TrackedProcessCount => _states.Count;
}
