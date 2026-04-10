namespace Beholder.Core;

/// <summary>
/// Traffic direction relative to the local host.
/// </summary>
public enum Direction {
    /// <summary>Traffic flowing from a remote endpoint into the local host.</summary>
    Inbound,

    /// <summary>Traffic flowing from the local host out to a remote endpoint.</summary>
    Outbound,
}
