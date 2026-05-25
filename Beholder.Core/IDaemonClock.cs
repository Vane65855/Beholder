namespace Beholder.Core;

/// <summary>
/// Captures the wall-clock time at which the daemon process started, so the
/// Settings tab's MOTD-style status strip can render an uptime label
/// ("4h 12m") that's stable across query refreshes. Implementations record
/// the start time once at construction and never update it.
/// </summary>
/// <remarks>
/// Deliberately a tiny abstraction. The alternative — having every consumer
/// derive uptime from <see cref="System.Diagnostics.Process.StartTime"/> —
/// is brittle on Linux (returns process-creation time, not necessarily the
/// daemon's logical start) and hits real OS APIs from inside test fakes.
/// One singleton, one property, faked deterministically in tests.
/// </remarks>
public interface IDaemonClock {
    /// <summary>
    /// UTC time at which the daemon was started. Set once at construction.
    /// </summary>
    DateTimeOffset StartedAt { get; }
}
