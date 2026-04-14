using System;
using System.Collections.Generic;
using Beholder.Protocol.Local;

namespace Beholder.Ui.Services;

/// <summary>
/// Shared singleton that tracks per-process traffic state from the daemon's live stream.
/// Both <c>StatusStripViewModel</c> and <c>TrafficTabViewModel</c> consume this service,
/// eliminating duplicated per-process tracking logic.
/// </summary>
/// <remarks>
/// The <see cref="ProcessStatesUpdated"/> event fires on the <see cref="DaemonStreamSubscriber"/>'s
/// background thread. Consumers must marshal to the UI thread via <c>Dispatcher.UIThread.Post</c>.
/// </remarks>
internal sealed class ProcessStateService {
    private readonly Dictionary<string, ProcessState> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// Fired after each <see cref="CounterBatch"/> is processed.
    /// The dictionary is a defensive snapshot — safe to read on any thread.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, ProcessState>>? ProcessStatesUpdated;

    public ProcessStateService(DaemonStreamSubscriber subscriber) {
        ArgumentNullException.ThrowIfNull(subscriber);
        subscriber.CounterBatchReceived += OnCounterBatch;
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
