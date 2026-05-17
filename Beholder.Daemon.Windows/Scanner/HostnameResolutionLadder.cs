using System.Collections.Concurrent;
using System.Net;
using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// Orchestrates a set of <see cref="IHostnameProbe"/> implementations across
/// a batch of LAN IPs in parallel. For each IP, the ladder tries each
/// injected probe in priority order until one returns non-null — first
/// non-null wins. Probes that fail or return null are skipped silently;
/// probes that throw are caught at the per-IP boundary so one bad device
/// doesn't kill the batch.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the parallelism shape from <see cref="ArpScanProbe.ProbeIpsAsync"/>
/// (Phase 9.2.1): <see cref="Parallel.ForEachAsync"/> with bounded
/// <see cref="MaxParallelHostnameResolves"/>, a linked
/// <see cref="CancellationTokenSource"/> enforcing
/// <see cref="DefaultPerLadderDeadline"/>, and the "deadline expires →
/// return partial results, outer cancel → rethrow" filtered-catch pattern
/// (Phase 9.2.1 §3 lesson on `OperationCanceledException` source filtering).
/// </para>
/// <para>
/// Parallelism is 32 (half of <c>ArpScanProbe</c>'s 64) because each IP
/// runs up to two probes sequentially per call, effectively doubling
/// concurrent network activity. Per-scan deadline is 60 s by default,
/// matching the ARP probe ceiling.
/// </para>
/// </remarks>
public sealed class HostnameResolutionLadder {
    /// <summary>
    /// Concurrent per-IP resolution slots. Each slot runs up to two probes
    /// sequentially (mDNS then NetBIOS fallback), so effective network
    /// concurrency peaks at ~64 simultaneous queries — same load profile
    /// as the ARP probe.
    /// </summary>
    private const int MaxParallelHostnameResolves = 32;

    /// <summary>
    /// Defensive ceiling on a single <see cref="ResolveAllAsync"/> wall-clock.
    /// Mirrors <c>ArpScanProbe.DefaultPerScanDeadline</c>. On expiry the
    /// ladder returns whatever partial results came in rather than throwing.
    /// </summary>
    private static readonly TimeSpan DefaultPerLadderDeadline = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<IHostnameProbe> _probes;
    private readonly TimeSpan _perLadderDeadline;
    private readonly ILogger<HostnameResolutionLadder> _logger;

    /// <summary>Production constructor: 60 s deadline.</summary>
    public HostnameResolutionLadder(IReadOnlyList<IHostnameProbe> probes, ILogger<HostnameResolutionLadder> logger)
        : this(probes, DefaultPerLadderDeadline, logger) { }

    /// <summary>
    /// Test-only constructor allowing a shorter deadline for deterministic
    /// deadline-expiry tests. Mirrors the test-injection pattern from
    /// <see cref="ArpScanProbe"/>.
    /// </summary>
    internal HostnameResolutionLadder(
        IReadOnlyList<IHostnameProbe> probes,
        TimeSpan perLadderDeadline,
        ILogger<HostnameResolutionLadder> logger
    ) {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(logger);
        _probes = probes;
        _perLadderDeadline = perLadderDeadline;
        _logger = logger;
    }

    /// <summary>
    /// Resolves hostnames for every IP in <paramref name="ips"/> in parallel
    /// via the injected probes. Returns a dictionary keyed on IP-string
    /// (matching <c>WindowsLanDeviceProbe</c>'s merge dictionary's key
    /// format). IPs that resolved to no hostname are absent from the
    /// dictionary (not present-with-null-value).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveAllAsync(
        IEnumerable<IPAddress> ips,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(ips);

        using var deadlineCts = new CancellationTokenSource(_perLadderDeadline);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadlineCts.Token);

        var results = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        try {
            await Parallel.ForEachAsync(ips, new ParallelOptions {
                MaxDegreeOfParallelism = MaxParallelHostnameResolves,
                CancellationToken = linkedCts.Token,
            }, async (ip, ct) => {
                ct.ThrowIfCancellationRequested();
                var name = await ResolveOneAsync(ip, ct).ConfigureAwait(false);
                if (name is not null) results[ip.ToString()] = name;
            }).ConfigureAwait(false);
        } catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested
                                                && !cancellationToken.IsCancellationRequested) {
            // Deadline expired before the caller cancelled — return partial results.
            // (If the OUTER cancellation token fired, propagate.)
        }
        return results;
    }

    /// <summary>
    /// Runs the injected probes against one IP in priority order, returning
    /// the first non-null hostname. Catches per-probe exceptions so one
    /// faulty probe doesn't take down the rest of the ladder for this IP.
    /// </summary>
    private async Task<string?> ResolveOneAsync(IPAddress ip, CancellationToken cancellationToken) {
        foreach (var probe in _probes) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                var name = await probe.ResolveAsync(ip, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(name)) return name;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                _logger.LogDebug(
                    ex, "Hostname probe {Protocol} threw for {Ip}; trying next probe",
                    probe.ProtocolName, ip);
            }
        }
        return null;
    }
}
