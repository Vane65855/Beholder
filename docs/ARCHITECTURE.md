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

Phase 4.6a: this is the only tier. All historical queries hit this table regardless of time range. Phase 4.6b/c will add finer and coarser tiers with cascading rollup. The rollup invariant: `SUM(bytes)` over a time range returns identical totals regardless of which tier is consulted, because coarser tiers are built by summing finer-tier rows.

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
