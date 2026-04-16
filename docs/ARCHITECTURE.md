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

### Alert Generation

Only three alert types exist:

| Kind          | Trigger                                      | Frequency         |
|---------------|----------------------------------------------|--------------------|
| `NewProcess`  | A binary path accesses the network for the first time | Once per unique path, ever |
| `HashChanged` | A tracked binary's SHA-256 differs from the stored value | Once per update per binary |
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
                                   -- 'FirewallRuleChanged', 'FirewallRuleRemoved'
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

## IPC Protocol (Daemon ↔ UI)

gRPC over named pipe (`\\.\pipe\beholder` on Windows) or Unix domain socket (`/run/beholder.sock` on Linux). The pipe/socket is DACL'd (Windows) or permission-restricted (Linux) to the local user or a `beholder-users` group.

The UI is a gRPC client. The daemon is a gRPC server. The primary RPC is a server-streaming call:

```protobuf
service BeholderLocal {
    // Live streaming
    rpc Subscribe (SubscribeRequest) returns (stream DaemonEvent);
    // Current state
    rpc GetSnapshot (GetSnapshotRequest) returns (GetSnapshotResponse);
    // Firewall management
    rpc ApplyFirewallRule (ApplyFirewallRuleRequest) returns (ApplyFirewallRuleResponse);
    // Alert management
    rpc MarkAlertRead (MarkAlertReadRequest) returns (MarkAlertReadResponse);
    // Chain integrity
    rpc VerifyChain (VerifyChainRequest) returns (VerifyChainResponse);
    // Historical traffic queries (Phase 4.6a)
    rpc GetProcessTimeline (GetProcessTimelineRequest) returns (GetProcessTimelineResponse);
    rpc GetProcessDestinations (GetProcessDestinationsRequest) returns (GetProcessDestinationsResponse);
    rpc GetAggregateTimeline (GetAggregateTimelineRequest) returns (GetAggregateTimelineResponse);
    rpc GetCountryBreakdown (GetCountryBreakdownRequest) returns (GetCountryBreakdownResponse);
}
```

`Subscribe` is the main channel: the UI calls it once on connect and receives a stream of events (counter batches, alerts, rule changes) for the lifetime of the connection.

`GetSnapshot` returns the current state (all active processes, their cumulative counters, firewall rules, recent alerts) so the UI can populate its views immediately on connect without waiting for the next counter tick.

The four `Get*` RPCs query SQLite directly for historical traffic data. They accept time ranges and (for timelines) a resolution parameter that controls bucket aggregation granularity.

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
