using System.Net;

namespace Beholder.Core;

/// <summary>
/// Resolves a single LAN IP to its hostname via one specific discovery
/// protocol (mDNS, NetBIOS, etc.). Implementations issue a single per-IP
/// query and wait up to a documented timeout for a response. The
/// <see cref="HostnameResolutionLadder"/> (in <c>Beholder.Daemon.Windows</c>)
/// orchestrates multiple probes per IP, trying each in priority order until
/// one returns non-null.
/// </summary>
public interface IHostnameProbe {
    /// <summary>
    /// Short human-readable protocol name used in log messages (e.g.
    /// <c>"mDNS"</c>, <c>"NetBIOS"</c>). Surfaces the protocol that produced
    /// a hostname when diagnostics need to attribute results.
    /// </summary>
    string ProtocolName { get; }

    /// <summary>
    /// Issues one hostname query for <paramref name="ip"/> and waits for the
    /// response. Returns the device's hostname on success, or
    /// <see langword="null"/> if the device didn't respond, returned a
    /// malformed reply, or doesn't speak this protocol. Throws only for
    /// non-recoverable setup failures (no socket bind permission, etc.);
    /// per-IP non-response collapses to null. Honors
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    Task<string?> ResolveAsync(IPAddress ip, CancellationToken cancellationToken);
}
