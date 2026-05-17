using System.Net;
using System.Net.Sockets;
using Beholder.Core;
using Beholder.Core.Discovery;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// NetBIOS hostname probe per Phase 9.2.5 / ADR 009. Sends one RFC 1002
/// NBSTAT (Node Status Request) query for the wildcard NetBIOS name
/// <c>"*"</c> as a unicast UDP datagram to <c>&lt;target-ip&gt;:137</c>,
/// then parses the response to extract the host's workstation name (the
/// first unique entry with suffix byte <c>0x00</c>).
/// </summary>
/// <remarks>
/// Each <see cref="ResolveAsync"/> call uses a fresh <see cref="UdpClient"/>
/// bound to an ephemeral port. NBSTAT replies natively unicast to the source
/// port (no QU-bit equivalent needed). Failures (no response, NetBIOS-over-
/// TCP/IP disabled on target, socket exception, malformed reply) collapse
/// to <see langword="null"/> per the <see cref="IHostnameProbe"/> contract.
/// </remarks>
public sealed class NetbiosHostnameProbe : IHostnameProbe {
    private const int NetbiosNameServicePort = 137;
    private const int PerProbeTimeoutMs = 1000;

    private readonly ILogger<NetbiosHostnameProbe> _logger;

    public NetbiosHostnameProbe(ILogger<NetbiosHostnameProbe> logger) {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public string ProtocolName => "NetBIOS";

    public async Task<string?> ResolveAsync(IPAddress ip, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork) return null;

        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var queryPacket = NetbiosPacketBuilder.BuildNbstatQuery(transactionId);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try {
            udp.Client.ReceiveTimeout = PerProbeTimeoutMs;

            await udp.SendAsync(queryPacket, new IPEndPoint(ip, NetbiosNameServicePort), cancellationToken)
                .ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(PerProbeTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try {
                var result = await udp.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);
                if (NetbiosPacketParser.TryExtractHostname(result.Buffer, transactionId, out var hostname)) {
                    return hostname;
                }
                return null;
            } catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                    && !cancellationToken.IsCancellationRequested) {
                return null;  // per-probe timeout — many devices don't speak NetBIOS at all
            }
        } catch (SocketException ex) {
            _logger.LogDebug(ex, "NetBIOS probe socket failure for {Ip}", ip);
            return null;
        }
    }
}
