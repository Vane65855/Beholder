# 005: Reverse-DNS fallback for direct-IP destinations (gated, default on)

## Context

ADR 004 established the rule: "the daemon must never issue its own outbound DNS queries to close the hostname gap." That rule was correct for the broad case — Windows DNS Client ETW + the resolver-cache preload cover virtually every flow that any process on the machine resolved through the system resolver. Both paths are passive observations of work that's already happening.

There remains a residual class no passive path can ever cover: connections that bypass DNS entirely. BitTorrent peers from a tracker IP list, P2P services with hardcoded bootstrap addresses, embedded IoT clients with baked-in endpoints, anything that opens a socket to a literal IP. For those flows the IP arrives on the wire without ever having traversed `getaddrinfo` / `DnsQuery_W` / a system resolver, so neither the ETW capture nor the cache preload has anything to record. The flow ends up in `TrafficEngine.cs:198` with `_dnsCache.Resolve(ip)` returning `null`, the bucket persists with `Hostname = null`, and the UI at `TrafficColsViewModel.cs:134` falls back to `d.Hostname ?? d.RemoteAddress` — the user sees a raw IP forever.

For the user this gap is the difference between "I see I'm uploading to `peer-49.example-tracker.net`" and "I see I'm uploading to `45.79.123.4`". The privacy and security calculus tilts toward "show me the name" once the connection is already on the wire — we're not initiating outbound traffic to learn anything new about the network, we're only asking the resolver "what's this IP I'm already talking to called?".

The right scope for the relaxation is exactly that residual class: PTR queries, only for IPs that have no observed name from the passive paths, only after the cache has missed. That's a strictly narrower exception to ADR 004's "never" — the broad rule still holds for everything covered by ETW + preload, which is the overwhelming majority of traffic.

## Decision

Add a reverse-DNS fallback as a decorator over `IDnsCache`, gated by a new `DnsOptions.EnableReverseDnsFallback` setting that defaults to `true`.

### Wiring

`Beholder.Daemon.Windows/ReverseDnsFallbackCache.cs` implements `IDnsCache` + `IHostedService`. It wraps the existing `EtwDnsCache` and is registered as the `IDnsCache` in `Program.cs`. Consumers (`TrafficEngine` is the only one today) call `_dnsCache.Resolve(ip)` exactly as before; the decorator is invisible.

The fast path stays synchronous and non-blocking. On a miss, the decorator enqueues the IP onto a bounded `Channel<IPAddress>` and returns `null` immediately. A background `IHostedService` worker drains the channel, calls `IReverseDnsResolver.ResolveAsync(ip)` (`SystemReverseDnsResolver` in production, backed by `Dns.GetHostEntryAsync` with a 3-second per-query timeout), and writes the resolved hostname back into the inner cache via the new `IDnsCacheIngest.IngestResolved` interface that `EtwDnsCache` already implements.

So the first flush tick after a new direct-IP destination appears shows the raw IP; the second tick (typically <1 s later) shows the resolved hostname. For the user this is the same "first connection looks raw, then resolves" behavior they already experience for cold-start ETW capture.

### Short-circuits

`Resolve(ip)` returns `null` without enqueuing in any of these cases:

- `EnableReverseDnsFallback == false` — pure passthrough, ADR 004's strict rule is honoured exactly.
- `ip.IsPrivateOrReserved()` (existing `Beholder.Core.IPAddressExtensions`) — RFC 1918 / link-local / loopback / ULA / CGNAT have no meaningful PTR records.
- IP currently in `_pending` — lookup in flight, don't double-enqueue.
- IP in `_negative` cache within the 30-minute cooldown — recent failure, don't retry yet.

### Failure handling

`SystemReverseDnsResolver` returns `null` (not throws) on every expected DNS failure: `SocketException` (NXDOMAIN, no PTR, host not found), per-query timeout, or the resolver bouncing the IP back as its own "hostname". The decorator interprets `null` as a negative — records `_negative[ip] = now` with a 30-minute cooldown, increments `_failed`, exits the worker iteration. No exception path crosses the worker boundary on the expected failure modes; `try`/`catch` in the worker loop only exists as defence against unexpected resolver bugs.

### Bounded resources

- Worker queue: `Channel<IPAddress>` capacity 500, `BoundedChannelFullMode.DropWrite`. A torrent peer-list burst (hundreds of unique IPs in a few seconds) will fill the queue; new IPs past the cap are dropped without setting the `_pending` flag, and the next flush tick reconsiders them.
- Single sequential worker. PTR queries are I/O-bound; one in flight at a time is a safe ceiling that won't overwhelm the system resolver. Future parallelism is straightforward (`Parallel.ForEachAsync` with `MaxDegreeOfParallelism: 4`) if measurements justify it.
- Negative cache: in-memory `ConcurrentDictionary`, 30-minute cooldown. Not persisted — restart re-attempts. PTR queries are cheap enough that this isn't a problem.

## Consequences

- **Positive: closes the last hostname gap.** Direct-IP traffic — torrent, P2P, hardcoded endpoints — gets human-readable names within one flush tick of first appearing. The COLS view's `HOSTS` column stops showing raw IPs for the residual class.
- **Positive: opt-out, not opt-in.** Default-on means users see the benefit without configuration. Users who care about the strict ADR 004 contract flip `Dns__EnableReverseDnsFallback=false` in the environment or `appsettings.json`.
- **Positive: zero impact on the observed-DNS path.** The decorator is transparent for inner-cache hits — no new code runs when the ETW path or preload has already covered an IP.
- **Positive: single-point gating.** The decorator is the only place in the daemon that can issue outbound DNS. If the policy ever needs to change again, there's exactly one file to edit.
- **Negative: small outbound DNS load.** PTR queries for unique direct-IP destinations. In normal browsing this is essentially zero (DNS already covered everything). For a torrent client running a few hundred peers, expect a few hundred PTR queries spread over the session — well within any reasonable rate budget.
- **Negative: reverse-DNS hostnames are sometimes misleading.** CDN edges resolve to generic infrastructure names like `server-52-84-150-39.fra2.r.cloudfront.net`, hosting providers return placeholder `static.X.Y.Z.W.clients.your-server.de` patterns. These are still strictly more informative than raw IPs but lose the original-intent advantage that observed-DNS gives.
- **Negative: 30-minute cooldown means a destination that briefly fails PTR stays unresolved for that window.** Consider raising the cooldown for genuine NXDOMAIN (cloud machines that intentionally have no PTR record) and lowering it for transient timeout if real-world data shows the uniform value is wrong.

## Scope

Wired on Windows only today (Program.cs Windows-only block). `Beholder.Daemon.Linux` has no `IDnsCache` registration — when it does, the decorator and `IReverseDnsResolver` are platform-agnostic and apply unchanged.

## Kill-switch

`Dns__EnableReverseDnsFallback=false` (env var) or in `appsettings.json` under section `"Dns"`:

```json
{
  "Dns": {
    "EnableReverseDnsFallback": false
  }
}
```

Set at daemon startup; the decorator snapshots `CurrentValue` once and does not honour hot-reload (matches `EnablePreload`'s behaviour from ADR 004's resolution).

### Backfill of historical SQLite rows

Closing the original "first (null) bucket" gap from this ADR's earlier out-of-scope list: the worker now writes the resolved hostname not just into the in-memory cache but also retroactively into every persisted bucket for that IP. `IDnsHostnameBackfill.BackfillHostnameAsync` (in `Beholder.Core`) is implemented by `SqliteTrafficStore` and runs an `UPDATE {tier} SET hostname = ? WHERE remote_address = ? AND hostname IS NULL` across all five rollup tiers in a single transaction.

Without this, one-off direct-IP flows (a 30-byte handshake that never gets a follow-up packet) had no further flush tick to carry the resolved name into SQLite, so the UI's `MAX(hostname) GROUP BY remote_address` kept seeing all-NULL rows for that IP and displayed the raw IP forever. With it, the next UI refresh after the worker resolves an IP shows the resolved hostname even for completed connections.

`hostname IS NULL` in the `WHERE` clause means the backfill never overwrites observed-DNS names — those are strictly more authoritative than reverse DNS. Backfill failure (DB locked, IO error) is caught at the worker boundary and degraded to a `Warning` log; the in-memory ingest already succeeded so live traffic still resolves, only persisted history misses out for that one IP.

## Out of scope

- Distinguishing reverse-DNS-derived names from observed-DNS names in the cache or UI. Both end up in `EtwDnsCache._cache` indistinguishably. A "provenance" field is a future enhancement if users want a visual marker (e.g., a subtle icon next to PTR-resolved rows).
- Per-query timeout / parallelism / cooldown configurability. Hardcoded sane defaults today (3 s timeout, 1 worker, 30 min cooldown). Make configurable later if real usage demands it.
- Distinguishing NXDOMAIN from timeout for differentiated cooldowns. Single uniform cooldown for v1.
- Persisting the negative cache across restarts. In-memory only.
- Adding an index on `remote_address` to speed up the backfill UPDATE. The reverse-DNS worker is rate-limited (single in-flight, 500-capacity bounded channel); the per-resolution cost of five tier UPDATEs without the index is microseconds-to-milliseconds and adding the index slows down every `WriteRawBucketsAsync` insert. Revisit only if measurements show the backfill is a hotspot on terabyte-scale databases.
