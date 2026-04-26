using System.Net;

namespace Beholder.Core;

/// <summary>
/// Persistent-layer write that retroactively fills the <c>hostname</c>
/// column on historical traffic rows once the reverse-DNS fallback (ADR
/// 005) finally learns a name for a direct-IP destination. Without this
/// seam, one-off flows that ended before the PTR query completed would
/// forever show as raw IPs in the UI even though the daemon eventually
/// learned their name — the in-memory cache write only takes effect for
/// flush ticks that record a *new* bucket for that IP, and a finished
/// connection produces no further bucket.
/// </summary>
public interface IDnsHostnameBackfill {
    /// <summary>
    /// Updates every traffic row across all rollup tiers where
    /// <c>remote_address</c> matches <paramref name="address"/> and
    /// <c>hostname IS NULL</c>, setting the hostname to
    /// <paramref name="hostname"/>. Returns the total number of rows
    /// updated across all tiers — useful for diagnostic logging and tests.
    /// </summary>
    /// <remarks>
    /// Idempotent: a re-run finds nothing to update because the
    /// previously-null rows are now populated. Rows that already had a
    /// hostname (set by the live ETW path before this PTR query
    /// completed) are not overwritten — those names came from observed
    /// DNS and are strictly more authoritative than reverse DNS.
    /// </remarks>
    Task<int> BackfillHostnameAsync(IPAddress address, string hostname, CancellationToken cancellationToken);
}
