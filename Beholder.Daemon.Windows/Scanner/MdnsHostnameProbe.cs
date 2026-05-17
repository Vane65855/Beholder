using System.Net;
using System.Net.Sockets;
using Beholder.Core;
using Beholder.Core.Discovery;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// mDNS hostname probe per Phase 9.2.5 / ADR 009. Sends one RFC 6762 PTR
/// query for the reverse-IP arpa name of each target IP, multicasts it to
/// <c>224.0.0.251:5353</c> from an ephemeral source port with the QU bit set
/// (RFC 6762 §5.4), and waits up to 1 second for a unicast reply on the
/// source port. mDNS multicast is link-local (TTL=1 by spec) — never leaves
/// the LAN.
/// </summary>
/// <remarks>
/// Each <see cref="ResolveAsync"/> call uses a fresh <see cref="UdpClient"/>
/// bound to an ephemeral port. <c>MulticastLoopback = false</c> so we don't
/// receive our own query back from the local stack. Failures (no response,
/// socket exception, malformed reply) all collapse to <see langword="null"/>
/// per the <see cref="IHostnameProbe"/> contract; cancellation propagates.
/// </remarks>
public sealed class MdnsHostnameProbe : IHostnameProbe {
    private const int MdnsMulticastPort = 5353;
    private const int PerProbeTimeoutMs = 1000;
    private static readonly IPAddress MdnsMulticastAddress = IPAddress.Parse("224.0.0.251");

    private readonly ILogger<MdnsHostnameProbe> _logger;

    public MdnsHostnameProbe(ILogger<MdnsHostnameProbe> logger) {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public string ProtocolName => "mDNS";

    public async Task<string?> ResolveAsync(IPAddress ip, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork) return null;

        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var queryPacket = MdnsPacketBuilder.BuildPtrQuery(ip, transactionId);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try {
            udp.MulticastLoopback = false;
            udp.Client.ReceiveTimeout = PerProbeTimeoutMs;

            await udp.SendAsync(queryPacket, new IPEndPoint(MdnsMulticastAddress, MdnsMulticastPort), cancellationToken)
                .ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(PerProbeTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try {
                var result = await udp.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);
                if (MdnsPacketParser.TryExtractHostname(result.Buffer, transactionId, out var hostname)) {
                    return hostname;
                }
                return null;
            } catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                    && !cancellationToken.IsCancellationRequested) {
                return null;  // per-probe timeout — not a real failure
            }
        } catch (SocketException ex) {
            _logger.LogDebug(ex, "mDNS probe socket failure for {Ip}", ip);
            return null;
        }
    }
}
