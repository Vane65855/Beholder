using System.Net;
using System.Net.Sockets;
using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Production <see cref="IReverseDnsResolver"/> backed by
/// <see cref="Dns.GetHostEntryAsync(IPAddress, CancellationToken)"/>. Caps each
/// query at <see cref="QueryTimeout"/> so a single unresponsive PTR
/// authoritative server can't stall the worker channel that calls into us.
/// </summary>
/// <remarks>
/// Returns <c>null</c> on every expected failure mode (no PTR record, network
/// timeout, transient resolver failure, the resolver bouncing back the IP
/// string). The decorator interprets <c>null</c> as "negative — apply the
/// cooldown"; throwing here would only force the decorator to wrap us in a
/// try/catch on the hot path. Unexpected exception types (anything outside
/// <see cref="SocketException"/> / <see cref="OperationCanceledException"/>)
/// are logged at <c>Warning</c> and swallowed for the same reason — the
/// fallback must never be the thing that crashes the daemon.
/// </remarks>
public sealed class SystemReverseDnsResolver : IReverseDnsResolver {
    /// <summary>
    /// Per-query timeout. Sized for a slow-but-not-hung path:
    /// authoritative-PTR roundtrips on residential links commonly land in
    /// 200-800 ms; 3 s leaves headroom for a single retry inside the
    /// resolver before we give up.
    /// </summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger<SystemReverseDnsResolver> _logger;

    public SystemReverseDnsResolver(ILogger<SystemReverseDnsResolver> logger) {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async ValueTask<string?> ResolveAsync(IPAddress address, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(address);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(QueryTimeout);

        IPHostEntry entry;
        try {
            // Passing the IP as a string is the cancellation-aware overload —
            // Dns dispatches to the PTR path automatically when the input
            // parses as an IP address. The IPAddress-typed overload exists
            // but doesn't accept a CancellationToken on this BCL.
            entry = await Dns.GetHostEntryAsync(address.ToString(), timeoutCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Outer cancellation (host shutting down): propagate so the worker
            // exits its loop instead of looping forever on cancelled queries.
            throw;
        } catch (OperationCanceledException) {
            // Timeout (linked CTS fired). Expected on slow PTR auth servers.
            return null;
        } catch (SocketException) {
            // NXDOMAIN, no PTR record, network unreachable, host not found —
            // every "DNS said no" path lands here.
            return null;
        } catch (Exception ex) {
            _logger.LogWarning(ex,
                "Reverse-DNS lookup threw unexpected exception for {Address}; treating as negative",
                address);
            return null;
        }

        var hostname = entry.HostName;
        if (string.IsNullOrWhiteSpace(hostname)) return null;

        // Some Windows configurations bounce the IP string back as the
        // "hostname" when no PTR exists. That's not a resolution; treat it
        // as a miss so the caller doesn't ingest the IP-as-its-own-name.
        if (string.Equals(hostname, address.ToString(), StringComparison.Ordinal)) return null;

        return hostname;
    }
}
