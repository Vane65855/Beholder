using Beholder.Core;

namespace Beholder.Tests;

/// <summary>
/// Returns a fixed <see cref="StartedAt"/> for deterministic testing of
/// daemon-uptime-derived values (Settings tab MOTD strip, Phase 13.1.1).
/// </summary>
internal sealed class FakeDaemonClock : IDaemonClock {
    public DateTimeOffset StartedAt { get; }

    public FakeDaemonClock(DateTimeOffset startedAt) {
        StartedAt = startedAt;
    }
}
