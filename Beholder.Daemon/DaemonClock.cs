using Beholder.Core;

namespace Beholder.Daemon;

/// <summary>
/// Default <see cref="IDaemonClock"/> — records <see cref="TimeProvider.GetUtcNow"/>
/// at construction. Registered as a singleton in <c>Program.cs</c> early in
/// the DI graph so its construction time approximates the daemon's logical
/// start (after the host builder runs but before the long-lived services
/// begin their work).
/// </summary>
internal sealed class DaemonClock : IDaemonClock {
    public DateTimeOffset StartedAt { get; }

    public DaemonClock(TimeProvider timeProvider) {
        ArgumentNullException.ThrowIfNull(timeProvider);
        StartedAt = timeProvider.GetUtcNow();
    }
}
