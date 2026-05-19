using System.Net;
using System.Net.Sockets;
using Beholder.Core.Discovery;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// mDNS DNS-Based Service Discovery (RFC 6763) probe per Phase 9.2.6 /
/// ADR 009. Unlike <see cref="MdnsHostnameProbe"/> (which sends one PTR
/// query per IP for the reverse-IP arpa name), this probe sends one PTR
/// query per <i>service-type</i> name (e.g. <c>_airplay._tcp.local</c>)
/// from a single ephemeral source port, then collects every unicast
/// response that arrives within <see cref="_browseDeadline"/>. One scan
/// → many responding devices → one (IP, hostname) pair per device.
/// </summary>
/// <remarks>
/// <para>
/// Empirically the dominant idiom on real LANs: most Bonjour-style
/// responders (Apple TVs, AirPlay speakers, Chromecasts, network
/// printers, NAS, IoT) advertise <i>services</i> and answer service-type
/// PTR queries while ignoring reverse-IP PTR queries. The RFC 6762 §5.4
/// QU bit on each query asks responders to unicast their reply to our
/// ephemeral source port, sidestepping the port-5353 conflict with the
/// Bonjour service that ships with iTunes / Adobe Acrobat on Windows.
/// </para>
/// <para>
/// Curated list of 12 service types covers the high-hit-rate categories:
/// Apple ecosystem (AirPlay, RAOP audio, HomeKit, companion-link),
/// Google (Chromecast / Cast), Linux/Avahi (workstation, SSH), file
/// sharing (SMB), printing (printer, IPP), and a handful of popular
/// consumer devices (Spotify Connect, Philips Hue). The DNS-SD
/// meta-query (<c>_services._dns-sd._udp.local</c>) for auto-discovering
/// service types is deferred to a possible 9.2.7 — most responders
/// don't support it well.
/// </para>
/// </remarks>
public sealed class MdnsServiceDiscoveryProbe {
    /// <summary>
    /// Service types we query each scan. Order doesn't matter — all queries
    /// fire in close succession from one socket and replies are correlated
    /// by transaction ID. Each entry is a DNS-SD service-type name of the
    /// form <c>_&lt;service&gt;._&lt;proto&gt;.local</c>.
    /// </summary>
    private static readonly string[] WellKnownServiceTypes = [
        "_workstation._tcp.local",     // Linux / Avahi machines, some macOS
        "_smb._tcp.local",              // Mac file sharing, NAS
        "_airplay._tcp.local",          // Apple TVs, AirPlay speakers, Sonos
        "_googlecast._tcp.local",       // Chromecast, Google Home, Android TV
        "_printer._tcp.local",          // Network printers (legacy LPD)
        "_ipp._tcp.local",              // Modern IPP-capable printers
        "_raop._tcp.local",             // AirPlay audio (Remote Audio Output Protocol)
        "_hap._tcp.local",              // HomeKit Accessory Protocol
        "_spotify-connect._tcp.local",  // Spotify Connect speakers
        "_hue._tcp.local",              // Philips Hue bridges
        "_ssh._tcp.local",              // SSH-advertising hosts (servers, dev boxes)
        "_companion-link._tcp.local",   // Apple Continuity / Handoff
    ];

    private const int MdnsMulticastPort = 5353;
    private static readonly IPAddress MdnsMulticastAddress = IPAddress.Parse("224.0.0.251");

    /// <summary>
    /// RFC 6762 recommends a ~1 s wait for the first replies and a few
    /// seconds for stragglers. 3 s is the sweet spot: long enough for
    /// slow IoT responders to flush their answer cache, short enough not
    /// to inflate the steady-state scan budget materially (a /24 cache
    /// hit pass completes in ~5 s; adding 3 s leaves us under 10 s).
    /// </summary>
    private static readonly TimeSpan DefaultBrowseDeadline = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _browseDeadline;
    private readonly ILogger<MdnsServiceDiscoveryProbe> _logger;

    /// <summary>Production constructor: 3 s browse window.</summary>
    public MdnsServiceDiscoveryProbe(ILogger<MdnsServiceDiscoveryProbe> logger)
        : this(DefaultBrowseDeadline, logger) { }

    /// <summary>
    /// Test-only constructor allowing a shorter deadline for deterministic
    /// tests. Mirrors the test-injection pattern from
    /// <see cref="ArpScanProbe"/> and <see cref="HostnameResolutionLadder"/>.
    /// </summary>
    internal MdnsServiceDiscoveryProbe(TimeSpan browseDeadline, ILogger<MdnsServiceDiscoveryProbe> logger) {
        ArgumentNullException.ThrowIfNull(logger);
        _browseDeadline = browseDeadline;
        _logger = logger;
    }

    /// <summary>
    /// Sends one PTR query per well-known service-type from a fresh
    /// ephemeral UDP socket, collects unicast replies for up to
    /// <see cref="_browseDeadline"/>, and returns a map from device IP
    /// (the source address of the reply) to the extracted hostname.
    /// </summary>
    /// <remarks>
    /// First non-null hostname per source IP wins — later packets for
    /// the same IP (e.g., a device advertising both <c>_airplay</c> and
    /// <c>_companion-link</c>) are ignored. Failures collapse to whatever
    /// partial results were collected: the caller treats an empty
    /// dictionary as "no SD-advertising devices visible" and falls
    /// through to the per-IP hostname ladder.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> BrowseAsync(CancellationToken cancellationToken) {
        var hostnames = new Dictionary<string, string>(StringComparer.Ordinal);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try {
            udp.MulticastLoopback = false;
        } catch (SocketException ex) {
            _logger.LogDebug(ex, "mDNS-SD: configuring UdpClient failed; skipping browse");
            return hostnames;
        }

        var expectedTransactionIds = await SendAllQueriesAsync(udp, cancellationToken).ConfigureAwait(false);
        if (expectedTransactionIds.Count == 0) {
            // Every send failed — no point waiting for replies.
            return hostnames;
        }

        await CollectResponsesAsync(udp, expectedTransactionIds, hostnames, cancellationToken).ConfigureAwait(false);
        return hostnames;
    }

    /// <summary>
    /// Sends one PTR query per well-known service type and returns the
    /// set of transaction IDs the parser should accept on inbound replies.
    /// A per-send <see cref="SocketException"/> is logged and skipped —
    /// other queries still go out.
    /// </summary>
    private async Task<HashSet<ushort>> SendAllQueriesAsync(UdpClient udp, CancellationToken cancellationToken) {
        var expectedTransactionIds = new HashSet<ushort>();
        var multicastEndpoint = new IPEndPoint(MdnsMulticastAddress, MdnsMulticastPort);

        foreach (var serviceType in WellKnownServiceTypes) {
            cancellationToken.ThrowIfCancellationRequested();
            var tid = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            byte[] packet;
            try {
                packet = MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery(serviceType, tid);
            } catch (ArgumentException ex) {
                // Programmer error — a service type in the hardcoded list is
                // malformed. Log and skip rather than throwing through DI.
                _logger.LogWarning(ex, "mDNS-SD: skipping malformed service type {ServiceType}", serviceType);
                continue;
            }

            try {
                await udp.SendAsync(packet, multicastEndpoint, cancellationToken).ConfigureAwait(false);
                expectedTransactionIds.Add(tid);
            } catch (SocketException ex) {
                _logger.LogDebug(ex, "mDNS-SD: send failed for {ServiceType}", serviceType);
            }
        }
        return expectedTransactionIds;
    }

    /// <summary>
    /// Reads inbound responses until <see cref="_browseDeadline"/> elapses
    /// or <paramref name="cancellationToken"/> fires. Each well-formed
    /// reply with a recognised transaction ID contributes one
    /// (source-IP → hostname) pair; first non-empty hostname per IP wins.
    /// </summary>
    private async Task CollectResponsesAsync(
        UdpClient udp,
        IReadOnlySet<ushort> expectedTransactionIds,
        Dictionary<string, string> hostnames,
        CancellationToken cancellationToken
    ) {
        using var deadlineCts = new CancellationTokenSource(_browseDeadline);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadlineCts.Token);

        while (!linkedCts.Token.IsCancellationRequested) {
            UdpReceiveResult result;
            try {
                result = await udp.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested
                                                    && !cancellationToken.IsCancellationRequested) {
                return;  // browse window closed — partial results are fine
            } catch (SocketException ex) {
                _logger.LogDebug(ex, "mDNS-SD: receive failed; ending browse early");
                return;
            }

            if (MdnsServiceDiscoveryParser.TryExtractHostname(result.Buffer, expectedTransactionIds, out var hostname)
                && !string.IsNullOrEmpty(hostname)) {
                hostnames.TryAdd(result.RemoteEndPoint.Address.ToString(), hostname);
            }
        }
    }
}
