# Architecture

This document describes the technical architecture of Beholder NMT. It is the single source of truth for how the system is structured, how data flows, and how components communicate. Read this before writing any code.

## System Overview

```
┌─────────────────────────────────┐
│         Beholder.Ui             │  Avalonia desktop app
│  (normal user privileges)       │  Connects via named pipe / unix socket
└──────────────┬──────────────────┘
               │ gRPC (local IPC)
┌──────────────▼──────────────────┐
│        Beholder.Daemon          │  Background service
│   (elevated privileges)         │  Owns all data, enforces all rules
│                                 │
│  ┌───────────┐ ┌──────────────┐ │
│  │ Platform   │ │ GeoIp        │ │
│  │ Provider   │ │ Resolver     │ │
│  │ (Win/Lin)  │ │ (DB-IP)      │ │
│  └───────────┘ └──────────────┘ │
│  ┌───────────┐ ┌──────────────┐ │
│  │ Chain      │ │ Uplink       │ │
│  │ Store      │ │ Client       │ │
│  │ (SQLite)   │ │ (disabled)   │ │
│  └───────────┘ └──────────────┘ │
└──────────────┬──────────────────┘
               │ outbound gRPC + TLS (opt-in)
┌──────────────▼──────────────────┐
│     Future Aggregator           │  NOT part of this codebase
│     (proprietary)               │
└─────────────────────────────────┘
```

## Project Dependency Graph

```
Beholder.Core ◄────────────────────────────────────────────────────────┐
  ▲  ▲  ▲  ▲                                                          │
  │  │  │  │                                                          │
  │  │  │  └──── Beholder.Daemon.GeoIp                                │
  │  │  │                                                             │
  │  │  ├─────── Beholder.Daemon.Windows  (conditional, Windows only) │
  │  │  │                                                             │
  │  │  └─────── Beholder.Daemon.Linux    (conditional, Linux only)   │
  │  │                                                                │
  │  └────────── Beholder.Daemon ──────────► Beholder.Protocol        │
  │                    ▲                        ▲   ▲                  │
  │                    │                        │   │                  │
  │                    └── Beholder.Daemon.Uplink   │                  │
  │                                                 │                  │
  └────────────── Beholder.Ui ──────────────────────┘                  │
                                                                       │
  Beholder.Tests ──► Beholder.Core, Beholder.Daemon.Uplink            │
       └──────────► Beholder.Tests.UplinkStub ──► Beholder.Protocol ──┘
```

Rules:
- Arrows point from dependent to dependency
- `Beholder.Core` has ZERO external dependencies (no NuGet packages that aren't part of the base framework)
- Platform projects (`Daemon.Windows`, `Daemon.Linux`) depend only on Core and their platform-specific NuGet packages
- The UI depends on Core and Protocol but NEVER on any Daemon project
- **Daemon platform splits are mandatory** because the per-OS delta (ETW vs netlink for flow capture, WFP vs nftables for firewall, Authenticode vs Linux equivalents for spoof detection) is large enough to warrant separate projects. New Linux daemon work goes in `Beholder.Daemon.Linux`; Windows in `Beholder.Daemon.Windows`.
- **UI is intentionally one project.** Windows-specific UI code (currently `Beholder.Ui/Services/WindowsNotificationService.cs`, the OS-toast surface) lives inline in `Beholder.Ui` wrapped in `#if PLATFORM_WINDOWS` source guards, with platform-specific NuGet packages declared in `Beholder.Ui.csproj` under MSBuild `Condition="'$(OS)' == 'Windows_NT'"` `ItemGroup`s. The UI's platform delta is small enough (~60 LOC today, expected ≤200 LOC even after a future Linux notification impl) that a separate project would cost more than it saves. See [ADR 008](decisions/008-ui-single-project-policy.md) for the trigger threshold that would justify revisiting.

## Data Flow

### Network Telemetry (Hot Path)

```
OS kernel event (ETW / netlink)
  → Platform provider (Daemon.Windows / Daemon.Linux)
    → GeoIpFlowSourceDecorator (attaches country code)
      → IFlowSource.OnFlowEvent callback
        → Channel<FlowEvent> (bounded, 10,000 capacity, DropOldest)
          → TrafficEngine (replaces Accumulator)
            → Two output cadences from the same event stream:
              1. Every 1 second:  Aggregate destinations by process →
                 build CounterSnapshot per process → fire OnSnapshotBatch →
                 BroadcastService → IPC subscribers (UI clients)
              2. Every 10 seconds: Build TrafficBucket per destination with
                 bucket bytes > 0 → join hostname via IDnsCache.Resolve() →
                 ITrafficStore.WriteBucketsAsync() + IDnsCacheStore.UpsertBatchAsync()
                 → evict idle destinations (5 min) and process totals (1 hr)
```

The Channel\<T\> decouples the OS callback (which must return fast) from the engine's processing. The channel is bounded — if the engine falls behind, the producer drops the oldest unprocessed events and logs a warning. Data loss in the counter pipeline is acceptable; the OS-level counters are cumulative, so the next sample self-corrects.

The TrafficEngine holds two kinds of in-memory state, both bounded:
- **DestinationAggregate:** Per-(process, address, port) tick/bucket deltas. NO cumulative totals. Evicted after 5 minutes idle. ~100–500 entries steady state.
- **ProcessLifetimeTotals:** Session-scoped per-process cumulative bytes. Evicted after 1 hour idle. ~50–200 entries. NOT reconstructed from SQLite on restart.

Historical queries (timelines, destination breakdowns, country analysis) are served from SQLite via four gRPC RPCs, never from in-memory state.

### Firewall Rule Application

```
User clicks ALLOW/BLOCK in UI
  → gRPC call: ApplyFirewallRule(process_path, direction, action)
    → Daemon validates the request
      → IFirewallController.AddRule / RemoveRule (platform-specific)
        → Chain store: append RULE event with hash
          → IPC: broadcast RuleChanged event to UI
            → UI updates the pill state
```

Firewall rules are persisted in SQLite (separate `firewall_rules` table, not in the chain) and re-applied on daemon startup. The chain records the fact that a rule was created/changed/removed, but the active rule set is a regular mutable table.

### GeoIP Resolution

```
New remote IP observed in FlowEvent
  → GeoIpResolver.Resolve(IPAddress)
    → Check LRU cache (Dictionary<IPAddress, CountryCode>, capped at 10,000)
      → Cache hit: return immediately
      → Cache miss:
        → IsPrivateOrReserved? → return CountryCode.Local
        → MMDB lookup via MaxMind.Db Reader
          → Found: cache + return alpha-2 code
          → Not found: cache + return CountryCode.Unknown
```

Resolution happens in the daemon, once per unique IP. The resolved country code is attached to the FlowEvent before it enters the Channel<T>. The UI never sees raw IPs without geo annotation.

### Recording Policy

The daemon applies a self-traffic filter at the ingestion boundary — the callback that receives events from `IFlowSource`, before they enter the bounded `Channel<FlowEvent>`. Any `FlowEvent` whose executable filename matches a known Beholder binary (`Beholder.Daemon`, `Beholder.Ui`, with or without `.exe`) is dropped. Filtered events never reach the `TrafficEngine`, `SqliteTrafficStore`, `BroadcastService`, or the UI.

The filter is controlled by a single config flag in `appsettings.json`:

```json
"Recording": {
  "FilterSelfTraffic": true
}
```

Default is `true`. Rationale: without the filter, daemon↔UI gRPC chatter adds roughly 50 MB/month to `traffic_buckets_10s` at default retention, making Beholder the top recorded process in its own database. The filter is a storage-efficiency measure, not obfuscation; setting the flag to `false` records everything including self-traffic, which is useful for debugging and data-hoarding users.

The filter is bound via `IOptionsMonitor<RecordingOptions>` so a live reload takes effect on the next flow event without a daemon restart. Toggling the flag does not retroactively prune existing rows — filtered events are never persisted, so "turning it off" takes effect immediately for incoming events, and "turning it on" leaves any previously recorded self-traffic in SQLite until the normal retention window expires.

The v1 filter is deliberately one switch. Future phases may add granular controls (per-path exclusion lists, localhost-only, port ranges) behind the same `Recording` config section.

### LAN Discovery (cold-path)

The Phase 9 LAN scanner runs as a periodic background pass — not a hot-path stream. Every `ScannerOptions.ScanIntervalSeconds` (default 300; floor 30) the cross-platform `LanScannerService` (`Beholder.Daemon/Scanner/`) asks `ILanDeviceProbe.ScanAsync` for one batched scan of the local subnet, then processes the results:

```
PeriodicTimer (every ScanIntervalSeconds, default 300)
  → LanScannerService.RunOnceAsync
    → WindowsLanDeviceProbe.ScanAsync (Linux: probe is null, scanner inactive)
      → NetworkInterface.GetAllNetworkInterfaces() picks the primary IPv4
        NIC (Up + non-empty GatewayAddresses + IPv4 mask present)
      → Fast pass: IphlpapiInterop.TryEnumerateIpv4ArpCache via GetIpNetTable2
        → yields (IP, MAC) from Windows' existing ARP/neighbor cache
          (Reachable / Stale / Permanent states only)
        → filter to entries inside the primary NIC's subnet → cachedEntries
      → Slow pass: ArpScanProbe.ProbeIpsAsync over (subnet hosts MINUS cachedIps)
        → Parallel.ForEachAsync(MaxDegreeOfParallelism=64) calls SendARP per IP
        → 60 s per-scan deadline returns partial results on expiry rather than
          blocking the scheduler
          → returns (IP, MAC) for each cache-miss responder → probedResults
      → merge cachedEntries + probedResults (cache wins on IP collisions)
      → IF ScannerOptions.EnableHostnameResolution (default true):
          MdnsServiceDiscoveryProbe.BrowseAsync (one-shot, ~3 s):
            → fresh UdpClient bound to an ephemeral source port (avoids
              competing with the Bonjour service that may already own
              UDP/5353 on the host)
            → multicast 12 well-known service-type PTR queries to
              224.0.0.251:5353 with the QU bit (RFC 6762 §5.4) asking
              responders to unicast back. Service types: _airplay,
              _googlecast, _smb, _workstation, _printer, _ipp, _raop,
              _hap, _spotify-connect, _hue, _ssh, _companion-link —
              all ._tcp.local
            → 3 s receive loop; per reply, MdnsServiceDiscoveryParser
              walks PTR + SRV + A records across answer + authority +
              additional sections. Hostname priority per device:
              SRV-target → A-record owner-name → PTR-instance leftmost
              label (printable-ASCII so "Living Room TV" decodes fine).
              Trailing .local stripped.
            → returns Dictionary<source-ip, hostname>; first non-empty
              hostname per source IP wins
          → patch each observation.Hostname from the SD result
          HostnameResolutionLadder.ResolveAllAsync over IPs STILL without
          a hostname (those the SD browse didn't cover):
            → Parallel.ForEachAsync(MaxDegreeOfParallelism=32) per IP:
                → MdnsHostnameProbe (RFC 6762): UDP multicast PTR query to
                  224.0.0.251:5353 (TTL=1, link-local) with the QU bit set
                  so responders unicast the reply to our ephemeral source
                  port. 1 s per-probe timeout.
                → fallback NetbiosHostnameProbe (RFC 1002): UDP NBSTAT
                  unicast to <ip>:137; parser extracts the workstation
                  name (suffix 0x00, unique-type). 1 s per-probe timeout.
            → 60 s per-ladder deadline mirrors ArpScanProbe; first non-null
              wins per IP.
            → returns Dictionary<ip, hostname>
          → patch each remaining observation.Hostname from the ladder result
    → LanScannerService.ProcessObservationAsync per observation:
      → vendor = IOuiVendorLookup.GetVendor(mac)        // Phase 9.1
      → existingByMac = ILanDeviceStore.GetByMacAsync   // Phase 9.1
      → if new MAC and known IP with different MAC:
          IEventStore.AppendAsync(EventKind.LanDeviceMacChanged, payload)
        else if new MAC:
          IEventStore.AppendAsync(EventKind.LanDeviceFirstSeen, payload)
      → ILanDeviceStore.UpsertAsync(LanDevice) preserves first_seen, updates rest
```

Cold-path discipline applies. The two-pass shape (added in Phase 9.2.1; see commit message + `phases.md` §3) is the practical fix for `SendARP`'s ~1 s per-unresponsive-IP cost: the cache walk catches the dominant case (devices Windows has recently seen) in microseconds with zero packets sent, and the parallel `SendARP` backstop covers the residue (recently-disconnected / freshly-joined-silent devices). Wall-clock on a typical /24: ~5 s steady-state (cache dominates), ~30 s cold-cache. Active probing per ADR 009 is preserved — `SendARP` still runs for cache misses; the cache walk just makes us not pay its cost for IPs we already know about. Failures at any layer log and continue: chain-write failure still upserts the store row; per-observation processing failure still processes the rest of the batch; probe-level failure logs and retries on the next tick.

The hostname-resolution pass (added in Phase 9.2.5; ADR 009's third design layer) is link-local-only: mDNS multicast TTL=1 (RFC 6762 requirement); NetBIOS unicast goes to subnet-local IPs only. Neither leaves the LAN. Implemented as pure managed C# via `System.Net.Sockets.UdpClient` — no new P/Invoke surface beyond the iphlpapi.dll work from 9.2/9.2.1. The packet builders and parsers (in `Beholder.Core/Discovery/`) mirror `Beholder.Core/Tls/TlsClientHelloParser` per ADR 006: `public static bool TryExtractX(ReadOnlySpan<byte>, out string?)` with exhaustive bounds checks and `false`-on-malformed-no-exception. The kill-switch `ScannerOptions.EnableHostnameResolution = true` matches ADR 005's `DnsOptions.EnableReverseDnsFallback` pattern: default-on, opt-out for users who want strict "no extra traffic" mode.

Phase 9.2.6 added DNS-Based Service Discovery (RFC 6763) as the **primary** hostname-resolution path, with the per-IP mDNS-PTR + NetBIOS ladder demoted to fallback. Diagnostic root cause: most Bonjour-style responders (Apple TVs, AirPlay speakers, Chromecasts, network printers, NAS, IoT bridges) advertise *services* via PTR records keyed on `_<service>._<proto>.local` and ignore reverse-IP PTR queries. The 9.2.6 SD-browse pattern — one multicast PTR per service-type from one socket, batched once per scan — matches what real-world tools (Fing, GlassWire Things tab, `dns-sd -B`, `avahi-browse`) use and lifts hit rate from "0 / N typical" to "most LAN devices visible." Same trust posture: pure managed UDP, no new P/Invoke, defensive bounds-checked parser, the same `EnableHostnameResolution` kill-switch gates the whole pass. DNS name compression / SRV + A correlation logic lives in the shared `DnsNameDecoder` helper (extracted in 9.2.6) so both `MdnsPacketParser` and `MdnsServiceDiscoveryParser` reuse the same loop-guarded pointer-following code.

Mac-as-identity per [ADR 009](decisions/009-scanner-as-lan-device-discovery.md): IP is mutable (DHCP renewal), MAC is the durable layer-2 identifier. A known IP showing up with a different MAC is the only condition that fires `LanDeviceMacChanged` — usually a benign DHCP reassignment, occasionally a potential ARP-spoof signal.

LAN-discovery events are **chain-audit-only** — they go to `event_log` (auditable, chain-hashed, never deleted) but are **NOT** alerts in the [ADR 002](decisions/002-three-alert-types.md) sense (no Alerts-tab row, no toast notification, no read/unread state). The Scanner-tab activity strip (Phase 9.4) will surface them in a Firewall-tab-style recent-changes view.

### Alert Generation

Only three alert types exist:

| Kind          | Trigger                                      | Frequency         |
|---------------|----------------------------------------------|--------------------|
| `NewProcess`  | A binary accesses the network for the first time. Identity = (signed publisher subject, ProductName, install-root folder) when available; falls back to path for unsigned or no-VersionInfo binaries. See ADR 007. | Once per logical identity, ever |
| `HashChanged` | A tracked binary's SHA-256 differs from the stored value, OR a known logical app appears with a different signing publisher (spoof detection — see ADR 007). | Per change |
| `ChainError`  | Hash chain verification detects a mismatch or gap | Should be never |

Alerts are written to the chain-hashed event log. The UI receives them via the IPC event stream. Alerts are never deleted — they transition from "unread" to "read" when the user views them.

## Storage Schema

All persistent state lives in a single SQLite database file.

### event_log (chain-hashed, append-only)

```sql
CREATE TABLE event_log (
    seq         INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_unix_ns  INTEGER NOT NULL,
    kind        TEXT    NOT NULL,   -- 'Counter', 'NewProcess', 'HashChanged',
                                   -- 'ChainError', 'FirewallRuleCreated',
                                   -- 'FirewallRuleChanged', 'FirewallRuleRemoved',
                                   -- 'LanDeviceFirstSeen', 'LanDeviceMacChanged'
                                   -- (LAN device kinds are chain-audit-only,
                                   -- NOT Alert-tab alerts — see ADR 009)
    payload     BLOB    NOT NULL,  -- canonical serialized event
    prev_hash   BLOB    NOT NULL,  -- SHA-256 of previous row's row_hash (32 bytes)
    row_hash    BLOB    NOT NULL   -- SHA-256(seq || ts || kind || payload || prev_hash)
);
```

### checkpoint (signed integrity markers)

```sql
CREATE TABLE checkpoint (
    seq        INTEGER PRIMARY KEY,
    row_hash   BLOB    NOT NULL,
    ts_unix_ns INTEGER NOT NULL,
    signature  BLOB    NOT NULL,   -- Ed25519 over (seq || row_hash || ts)
    key_id     TEXT    NOT NULL
);
```

### firewall_rules (mutable, not chain-hashed directly)

```sql
CREATE TABLE firewall_rules (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    process_path TEXT    NOT NULL,
    direction    TEXT    NOT NULL,  -- 'Inbound' or 'Outbound'
    action       TEXT    NOT NULL,  -- 'Allow' or 'Block'
    source       TEXT    NOT NULL,  -- 'Manual', 'Default', 'Remote'
    created_at   INTEGER NOT NULL,
    updated_at   INTEGER NOT NULL,
    UNIQUE(process_path, direction)
);
```

### process_registry (tracks known binaries)

```sql
CREATE TABLE process_registry (
    path         TEXT    PRIMARY KEY,
    display_name TEXT    NOT NULL,
    sha256       BLOB,
    first_seen   INTEGER NOT NULL,
    last_seen    INTEGER NOT NULL,
    last_hash_at INTEGER
);
```

### lan_device (Phase 9 LAN scanner discovered devices)

```sql
CREATE TABLE lan_device (
    mac                TEXT    PRIMARY KEY,    -- lowercase hex with colons (aa:bb:cc:dd:ee:ff)
    ip                 TEXT    NOT NULL,
    vendor             TEXT    NULL,           -- NULL if MAC's OUI prefix not in IEEE table
    hostname           TEXT    NULL,           -- NULL if mDNS/NetBIOS/PTR ladder all failed
    first_seen_unix_ns INTEGER NOT NULL,
    last_seen_unix_ns  INTEGER NOT NULL
);
-- Indexes: (ip), (last_seen_unix_ns)
```

Identity is keyed on `mac` per [ADR 009](decisions/009-scanner-as-lan-device-discovery.md). IP is mutable (DHCP renewals); the scanner uses `idx_lan_device_ip` to find the device currently associated with a given IP, compares its MAC to the just-observed MAC, and writes a `LanDeviceMacChanged` chain event when they differ (potential ARP-spoof signal in the simplest case, more commonly just DHCP reassignment). `idx_lan_device_last_seen` supports the `ListLanDevices` RPC's `seen_since` filter.

### traffic_buckets_10s (first tier of rollup cascade)

```sql
CREATE TABLE traffic_buckets_10s (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    process_path    TEXT    NOT NULL,
    process_name    TEXT    NOT NULL,
    remote_address  TEXT    NOT NULL,
    remote_port     INTEGER NOT NULL,
    hostname        TEXT,               -- NULL when DNS cache had no entry
    country         TEXT    NOT NULL,   -- alpha-2, "??", or "--"
    bytes_in        INTEGER NOT NULL,
    bytes_out       INTEGER NOT NULL,
    bucket_start_ms INTEGER NOT NULL,   -- Unix milliseconds, aligned to 10s boundary
    bucket_seconds  INTEGER NOT NULL DEFAULT 10
);
-- Indexes: (process_path, bucket_start_ms), (bucket_start_ms), (country, bucket_start_ms)
```

One of five tiers in the rollup cascade. See "Storage Rollup Architecture" below for the cascade's shape, the rollup invariant, and tier-selection rules.

### Storage Rollup Architecture

Traffic data is stored across five tiers of progressively coarser time resolution. The engine writes 1-second buckets directly into `traffic_raw`; the `RollupService` background hosted service cascades rows upward through each adjacent pair on that tier's own cadence.

```
  traffic_raw           1 s buckets    ┐
     │                                 │
     ▼ rollup every 10 s               │
  traffic_buckets_10s   10 s buckets   │  Timeline reads STITCH across
     │                                 │  all tiers: recent time slices
     ▼ rollup every 1 min              │  served by the finest-retention
  traffic_buckets_1m    1 min buckets  │  tier that covers them.
     │                                 │
     ▼ rollup every 10 min             │  Single-tier aggregate reads
  traffic_buckets_10m   10 min buckets │  pick the coarsest tier whose
     │                                 │  bucketSeconds ≤ resolution.
     ▼ rollup every 1 hour             │
  traffic_buckets_1h    1 hour buckets ┘  (terminal; retention = ∞ by default)
```

All five tiers share an identical column schema — tier selection is just a table-name swap. Indexes on `(process_path, bucket_start_ms)`, `(bucket_start_ms)`, and `(country, bucket_start_ms)` exist on every tier.

**Rollup invariant.** For any time range `[t0, t1]` and any pair of tiers `A`, `B` where both retain the range, `SUM(bytes_in + bytes_out)` from `A` equals the same sum from `B`. The cascade preserves this by design: each rollup step is a single `INSERT ... SELECT ... GROUP BY process_path, process_name, remote_address, remote_port, target_bucket_start`, summing `bytes_in` / `bytes_out` across source rows. Enforced by `Beholder.Tests/RollupServiceTests.cs → RollupInvariant_Holds_AcrossAllTiers`.

**Read-side: two different selection rules, depending on the query type.**

*Single-tier queries* (`GetProcessDestinationsAsync`, `GetCountryBreakdownAsync`, `GetProcessSummariesAsync`) — queries that return an aggregate summary over a range, not a timeline. These delegate to `TierSelector.Select(tiers, from, resolution, now)`:

1. Find the coarsest tier whose `BucketSeconds ≤ resolution.TotalSeconds` (fewer rows scanned).
2. If no tier matches, fall back to the finest tier (first in the list). The caller receives data at the tier's native bucket size.

Retention is **not** a filter here. A tier's retention cap limits how far back that tier has data, but `WHERE bucket_start_ms >= from` naturally returns only the rows that actually exist, so querying a shorter-retention tier for a longer range just yields whatever data the tier has (typically the most recent portion of the range at full fidelity). This is strictly better than falling back to a coarser tier that happens to have infinite retention — the user wants the finest granularity available for whatever data exists, not the coarsest guaranteed-complete tier.

Queries without a resolution parameter (`GetProcessDestinationsAsync`, `GetCountryBreakdownAsync`, `GetProcessSummariesAsync`) use a pseudo-resolution of `(to - from) / 300` with a 1-second floor, so tier choice stays consistent with timeline queries over the same range.

*Timeline queries* (`GetAggregateTimelineAsync`, `GetProcessTimelineAsync`) — these use a **stitched multi-tier** query that partitions the request range into non-overlapping slices, one per tier, and serves each slice from its finest-retention tier. Walking finest → coarsest:

- `traffic_raw` serves `[now − 10 min, now)`
- `traffic_buckets_10s` serves `[now − 7 d, now − 10 min)`
- `traffic_buckets_1m` serves `[now − 14 d, now − 7 d)`
- `traffic_buckets_10m` serves `[now − 1 y, now − 14 d)`
- `traffic_buckets_1h` serves everything older (retention `null`)

(Boundaries are Balanced preset; Compact preset has shorter retentions but the partitioning logic is identical.) Each slice issues one SQL query against its tier's table; results merge by output-bucket timestamp in C#. Recent events stay sharp; older data degrades smoothly within the same chart. Helper `TierSelector.SelectTierForAge(tiers, age)` returns the finest tier whose retention covers a given age — used when the slicing logic asks "what's the tier for this slice's age?".

**Bucket-width stability for timeline queries.** The stitched query computes the output-bucket width purely from actual data extent inside the request range:

```
effectiveResolutionMs = smallest entry of NiceResolutionsMs ≥ (extent / 400)
```

with `NiceResolutionsMs = [1s, 5s, 10s, 30s, 1min, 5min, 10min, 30min, 1h, 6h, 1day]`. The caller's `resolution_ms` parameter is accepted for backward compatibility but intentionally ignored here — honoring it would make the same underlying data produce different-width buckets at 7d vs 30d vs All Time, violating "same data → same chart."

**`nowMs` is snapped to the start of the current minute** before any slice boundary is computed. Queries issued inside the same wall-clock minute share slice boundaries exactly and return byte-identical arrays on unchanged data — so rapid re-queries (e.g., user switching between range presets) don't introduce drift that `NiceMax` in the chart would amplify to 2× Y-axis jumps.

See `Beholder.Daemon/Storage/SqliteTrafficStore.cs → StitchMultiTierTimelineAsync` for the implementation and `Beholder.Tests/SqliteTrafficStoreTests.cs → GetAggregateTimelineAsync_StitchesAcrossTiers / SameDataDifferentRanges / TimeDriftWithinMinute` for the guarantees.

**Watermark.** The rollup service does not maintain a watermark table. Each cascade step queries `SELECT MAX(bucket_start_ms) FROM target_tier` to find where the target left off, then processes source rows with `bucket_start_ms >= watermark AND bucket_start_ms < aligned_now`, where `aligned_now = floor(now / target_bucket_ms) * target_bucket_ms`. This design is self-correcting across daemon restarts — the service always resumes from the target's own history — and adds only a microsecond `MAX` lookup per tick (the target tier is indexed on `bucket_start_ms`). Partial target buckets are never rolled; only fully-closed target windows cross the aligned-now boundary.

**First-tick catch-up.** After startup, the first rollup tick runs every cascade pair regardless of each tier's `RollupInterval`. This absorbs rollup ticks that would have fired while the daemon was stopped. Subsequent ticks respect each source tier's interval (10 s / 1 min / 10 min / 1 hour for raw / `_10s` / `_1m` / `_10m` respectively; the terminal tier has zero interval).

**Retention presets.** The shipped `RollupOptions` class provides two hand-tuned presets, selected via `Preset` bound from the `"Rollup"` section of `appsettings.json`:

| Tier | Balanced (default) | Compact |
|---|---|---|
| `traffic_raw` | 10 min | 10 min |
| `traffic_buckets_10s` | 7 days | 3 days |
| `traffic_buckets_1m` | 14 days | 7 days |
| `traffic_buckets_10m` | 365 days | 90 days |
| `traffic_buckets_1h` | ∞ (never prune) | ∞ (never prune) |
| **Year-1 footprint** | ~1.4 GB | ~580 MB |

**Balanced** is for users who want full historical fidelity without thinking about storage. **Compact** is for users who'd rather pay less storage at the cost of shorter zoom-in headroom on older data. Both presets share identical bucket sizes; only retention differs.

Per-tier retention is **not** individually user-configurable in Phase 4.6b. Individual overrides could create combinations that break the tier-selection contract (e.g., a user setting `_10s` to 60 days would shadow `_1m` for mid-range queries and waste query cost). The two presets are hand-checked to leave tier selection's routing intact. A future settings UI will expose only the preset picker, not per-tier knobs.

**Terminal tier retention is nullable.** `RollupTier.Retention` is `TimeSpan?`; `null` means "never prune", and the rollup service skips pruning that tier entirely. Both presets use `null` on `_1h`, so the hourly tier grows unbounded by default (~90 MB/year at typical usage). Users who want a hard ceiling on history will use the planned `RetentionOptions.MaxDataAge` (see below).

**Future `RetentionOptions` hook.** A planned future class — not implemented in Phase 4.6b — will expose a single user-facing "max data age" cap, defaulting to `null` (infinite). When set, it applies as a `min()` cap on every tier's preset-derived retention:

```
effective_retention[tier] = min(tier.Retention, RetentionOptions.MaxDataAge)
```

The two controls compose naturally: `Balanced + Infinite` (default) = keep everything forever at decreasing resolution; `Compact + 30 days` = aggressive storage floor with no data older than a month in any tier; `Balanced + 1 year` = detailed history with a hard ceiling. Do **not** add per-tier retention customization ahead of this work — the single `MaxDataAge` knob is the designed entry point.

**No data migration on upgrade.** On first startup after Phase 4.6b lands, the new coarser tiers (`_1m` / `_10m` / `_1h`) are empty. They begin populating forward from that point. Existing `traffic_buckets_10s` data remains queryable via the `_10s` tier's retention window but is not backfilled into coarser tiers — the rollup cascade propagates naturally going forward.

**Self-traffic filter interaction.** The Phase 4.7 self-traffic filter runs at ingestion before the `Channel<FlowEvent>`, so filtered events never enter any tier. Beholder's own processes are absent from every tier regardless of preset.

**Cross-references.** See `Beholder.Daemon/RollupOptions.cs` for the preset definitions, `Beholder.Daemon/Storage/TierSelector.cs` for `Select` and `SelectTierForAge`, `Beholder.Daemon/Storage/SqliteTrafficStore.cs → StitchMultiTierTimelineAsync` for the stitched timeline read path, `Beholder.Daemon/Pipeline/RollupService.cs` for the cascade service, and `Beholder.Tests/RollupServiceTests.cs` / `TierSelectionTests.cs` / `RollupOptionsPresetTests.cs` / `SqliteTrafficStoreTests.cs` for the invariant's enforcement and the stitched-query guarantees.

### dns_cache (persistent hostname-to-IP mappings)

```sql
CREATE TABLE dns_cache (
    address    TEXT    PRIMARY KEY,   -- IP address as string
    hostname   TEXT    NOT NULL,
    updated_at INTEGER NOT NULL       -- Unix milliseconds
);
```

Survives daemon restarts. Populated during 10-second bucket flush from the in-memory `IDnsCache`. Used to backfill hostnames on traffic records.

### DNS observation limitations

`IDnsCache` exists because reverse DNS on CDN IPs returns generic edge names (`server-52-84-150-39.fra2.r.cloudfront.net`) rather than the hostname the user actually typed. On Windows we observe DNS traffic passively via the `Microsoft-Windows-DNS-Client` ETW provider (`EtwDnsCache`), which gives us the user-intended name the moment any Windows app resolves it. **The daemon issues no outbound DNS queries except PTR (reverse-DNS) lookups for direct-IP destinations that have no hostname from the Windows DNS cache or the ETW capture path** — those are the residual class no passive method can ever cover (BitTorrent peers, P2P bootstrap addresses, hardcoded endpoints). The fallback is implemented as `ReverseDnsFallbackCache` (a decorator over `EtwDnsCache`) and is gated by `DnsOptions.EnableReverseDnsFallback` (default `true`); see `docs/decisions/005-reverse-dns-fallback.md`.

That strategy has three known gaps:

1. **Explicit bypass: DNS-over-HTTPS / DoT / DoQ.** Apps that run their own resolver inside the process — Brave or Firefox with DoH enabled, some VPN clients, apps with bundled stub resolvers — never call the Windows DNS Client API. The ETW provider emits no event. Workaround: disable DoH (Brave: `brave://settings/security` → *Use secure DNS* → Disabled; Firefox: `about:config` → `network.trr.mode = 5`).
2. **Implicit bypass: in-process DNS caches.** Even with DoH off, browsers like Firefox keep a per-process DNS cache (`network.dnsCacheEntries = 400` default, `network.dnsCacheExpiration = 60` s default positive TTL). Once Firefox has resolved a hostname within its own lifetime — including for long-lived HTTPS/HTTP-2/QUIC sessions that reuse connections — it connects directly to the cached IP without asking Windows again. The ETW provider never sees the resolution, so we never capture it. This is the same effective blind spot as DoH, via a different mechanism. No workaround short of restarting the browser (which drops its cache). In Firefox's case a manual clear is available at `about:networking#dnscache`. The same applies to Chromium-based browsers' own internal caches.
3. **Short-window races.** `TrafficEngine.FlushTickAndRawAsync` writes one raw bucket per destination per second. If the first bucket fires before the DNS event arrives (rare — ETW events normally precede the first packet by microseconds) the bucket is written with `hostname = NULL`. `SqliteTrafficStore.GetDestinationsAsync` uses `MAX(hostname)` grouped by remote address, so any later bucket with a non-null hostname promotes the row to the resolved name — the gap self-heals as traffic continues.

Event coverage: `EtwDnsCache.OnEtwEvent` accepts any event from the provider that carries a query name + answer pair. That covers `3008` (`DnsQueryCompleted`), `3018` (`DnsCacheLookup` — the common case on a warm machine), `3020` (`DnsQueryCompletedEx` on Windows 11), and any future/variant events exposing the same logical fields. Events that only carry a query name or metadata (`3006` `DnsQueryStarted`, `3010` `SentToServer`, `3011` `ReceivedFromServer`, `1015` `DnsServerTimeout`, etc.) are counted in a separate lifecycle metric so they don't inflate the probe-miss diagnostic. Negative-cache entries (answer = `0.0.0.0` or `::`) are filtered out in `ExtractAddresses` — those mark resolutions the OS has chosen to remember as failures and would otherwise pollute the IP-to-hostname map.

**Preload at startup.** To close the cold-start portion of the cache-bypass gap, `EtwDnsCache.StartAsync` reads whatever Windows already has cached via `DnsGetCacheDataTableEx` (preferred on Win11; falls back to the legacy `DnsGetCacheDataTable` export if Ex is absent). Entries are fetched by re-querying each cached name with `DNS_QUERY_NO_WIRE_QUERY` — a flag that restricts `DnsQuery_W` to the local cache + HOSTS file, guaranteeing zero outbound traffic. See `docs/decisions/004-dns-cache-preload-undocumented-api.md`.

**TLS-level hostname visibility via SNI extraction.** The fourth and final layer of the resolution ladder, added in ADR 006. `Beholder.Daemon.Windows/PktmonSniSource.cs` subscribes to the `Microsoft-Windows-PktMon` ETW provider, parses captured TCP/443 packets for TLS ClientHello records via `Beholder.Core/Tls/TlsClientHelloParser.cs`, and feeds resolved (hostname, dest IP) pairs into the existing `IDnsCacheIngest` seam. Closes the DoH bypass and the in-process-cache bypass simultaneously: every fresh TLS handshake emits a plaintext SNI on the wire regardless of how DNS was resolved. Gated by `SniOptions.EnableSniCapture` (default `true`). TLS 1.3 ECH (Encrypted ClientHello) defeats this by design — those flows fall through to reverse DNS or raw IP — but ECH is opt-in for browsers today and not in widespread production use.

## IPC Protocol (Daemon ↔ UI)

gRPC over named pipe (`\\.\pipe\beholder` on Windows) or Unix domain socket (`/run/beholder.sock` on Linux). The pipe/socket is DACL'd (Windows) or permission-restricted (Linux) to the local user or a `beholder-users` group.

The UI is a gRPC client. The daemon is a gRPC server. The primary RPC is a server-streaming call:

```protobuf
service BeholderLocal {
    // Live streaming — DaemonEvent has 5 oneof variants:
    //   CounterBatch / FirewallRuleChange / Alert (Phases 2-7)
    //   LanDeviceFirstSeenEvent / LanDeviceMacChangedEvent (Phase 9.3)
    rpc Subscribe (SubscribeRequest) returns (stream DaemonEvent);
    // Current state
    rpc GetSnapshot (GetSnapshotRequest) returns (GetSnapshotResponse);
    // Firewall management
    rpc ApplyFirewallRule (ApplyFirewallRuleRequest) returns (ApplyFirewallRuleResponse);
    rpc RemoveFirewallRule (RemoveFirewallRuleRequest) returns (RemoveFirewallRuleResponse);
    rpc ListFirewallRules (ListFirewallRulesRequest) returns (ListFirewallRulesResponse);
    rpc SetFirewallEnabled (SetFirewallEnabledRequest) returns (SetFirewallEnabledResponse);
    // Alert management
    rpc MarkAlertRead (MarkAlertReadRequest) returns (MarkAlertReadResponse);
    // Chain integrity
    rpc VerifyChain (VerifyChainRequest) returns (VerifyChainResponse);
    // Historical traffic queries (Phase 4.6a / 6.5 / 8)
    rpc GetProcessTimeline (GetProcessTimelineRequest) returns (GetProcessTimelineResponse);
    rpc GetProcessDestinations (GetProcessDestinationsRequest) returns (GetProcessDestinationsResponse);
    rpc GetAggregateTimeline (GetAggregateTimelineRequest) returns (GetAggregateTimelineResponse);
    rpc GetCountryBreakdown (GetCountryBreakdownRequest) returns (GetCountryBreakdownResponse);
    rpc GetProtocolBreakdown (GetProtocolBreakdownRequest) returns (GetProtocolBreakdownResponse);
    rpc GetProcessSummaries (GetProcessSummariesRequest) returns (GetProcessSummariesResponse);
    rpc GetFirewallActivity (GetFirewallActivityRequest) returns (GetFirewallActivityResponse);
    // LAN scanner (Phase 9.3)
    rpc ListLanDevices (ListLanDevicesRequest) returns (ListLanDevicesResponse);
    rpc TriggerScan (TriggerScanRequest) returns (TriggerScanResponse);
}
```

`Subscribe` is the main channel: the UI calls it once on connect and receives a stream of events (counter batches, alerts, rule changes, LAN device first-seen / MAC-changed) for the lifetime of the connection.

`GetSnapshot` returns the current state (all active processes, their cumulative counters, firewall rules, recent alerts) so the UI can populate its views immediately on connect without waiting for the next counter tick.

The historical `Get*` RPCs query SQLite directly for traffic data. They accept time ranges and (for timelines) a resolution parameter that controls bucket aggregation granularity.

`ListLanDevices` returns a paged read of the `lan_device` table (Phase 9.3). Supports a `seen_since` filter against `idx_lan_device_last_seen` and a server-clamped `limit` (default 200, cap 1000). `TriggerScan` runs one immediate scan, returning a structured response with the observation count — recoverable failures (scanner inactive, probe threw) surface as `success=false` with a human-readable message rather than `RpcException`, mirroring the `ApplyFirewallRule` soft-failure precedent. Both RPCs are gated by the same DI registration that wires up the scanner; on Linux (no probe) `ListLanDevices` returns an empty list and `TriggerScan` reports the scanner-inactive condition.

LAN scanner chain events (`LanDeviceFirstSeen` / `LanDeviceMacChanged`) are pushed to subscribed UIs via `BroadcastService` as part of the same fan-out mechanism that ships counter batches, alerts, and rule changes — Phase 9.3 closed the previously-implicit gap where the LAN scanner wrote to the chain but never broadcast (every other event kind already did both). The pattern is now invariant: every mutable event goes to both chain (durable audit) and broadcast (live UI).

Phase 9.4 closes the user-facing loop: the Scanner tab UI (`Beholder.Ui/Views/Tabs/ScannerTabView.axaml` + `ScannerTabViewModel`) consumes the 9.3 IPC surface end-to-end. A master-detail layout (320-px-MinWidth device list on the left, full-context detail pane on the right) backed by `ListLanDevices` for the initial snapshot + `LanDeviceFirstSeenEvent` / `LanDeviceMacChangedEvent` for live updates. A "Scan now" button in the header drives `TriggerScan`; the structured `success / message / devices_observed` response renders as a transient warn/danger banner. Mirrors the `AlertsTabViewModel` precedent: single-tab state ownership (no cross-tab state service yet — abstraction deferred to a second-consumer phase), cold-start race handled via the `Task? _activationTask` field, all five required UI states (loading / empty / populated / error / extreme) implemented and tested. End-to-end loop: daemon discovers → store persists → chain audits → broadcast pushes → UI renders.

## Uplink Protocol (Daemon → Aggregator)

Bidirectional streaming gRPC over TCP + TLS, initiated by the daemon:

```protobuf
service BeholderUplink {
    rpc Connect (stream DaemonMessage) returns (stream ServerMessage);
}
```

The daemon authenticates with a signed JWT (Ed25519) carrying a sensor ID and capability set. The aggregator validates the token against a list of trusted issuer public keys.

Disabled by default. Enabled via `[uplink]` section in `beholder.toml`. The daemon never opens an inbound port.

## Platform Abstraction

### Core Interfaces

```csharp
// In Beholder.Core

public interface IFlowSource {
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    event Action<FlowEvent> OnFlowEvent;
}

public interface IFirewallController {
    Task<IReadOnlyList<FirewallRule>> ListRulesAsync(CancellationToken ct);
    Task AddRuleAsync(FirewallRule rule, CancellationToken ct);
    Task RemoveRuleAsync(string processPath, Direction direction, CancellationToken ct);
}

public interface IGeoIpResolver {
    CountryCode Resolve(IPAddress address);
}

public interface IEventStore {
    Task AppendAsync(EventKind kind, ReadOnlyMemory<byte> payload, CancellationToken ct);
    Task<ChainVerificationResult> VerifyAsync(CancellationToken ct);
}
```

### Platform Registration

At daemon startup, `Program.cs` detects the OS and registers the appropriate implementations:

```csharp
if (OperatingSystem.IsWindows()) {
    services.AddSingleton<IFlowSource, EtwFlowSource>();
    services.AddSingleton<IFirewallController, WfpFirewallController>();
} else if (OperatingSystem.IsLinux()) {
    services.AddSingleton<IFlowSource, NetlinkFlowSource>();
    services.AddSingleton<IFirewallController, NftablesFirewallController>();
}
```

The rest of the daemon code depends only on the interfaces. It never sees the platform types.

## Security Model

- The daemon runs elevated (Administrator / root). The UI runs as normal user.
- The named pipe / unix socket is the trust boundary. The daemon validates all commands from the UI.
- The uplink uses mutual TLS + JWT token authentication. The daemon presents a token; the aggregator validates it.
- The signing key for chain checkpoints is stored in a permissions-locked file owned by the daemon's service account.
- Firewall rule changes are always logged to the chain, regardless of source (manual, default, or remote).
