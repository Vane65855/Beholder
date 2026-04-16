# Beholder NMT — Project Status & Phase Plan

**Last updated:** 2026-04-16
**Current checkpoint:** Phase 5.4.3 (Historical query fidelity and stability)
**Test count:** 472

---

## 1. Status Summary

As of 2026-04-16, the daemon captures per-process network telemetry via ETW on Windows, enriches flows with DB-IP country codes, and persists per-destination traffic to SQLite through a five-tier rollup cascade (`traffic_raw` → `_10s` → `_1m` → `_10m` → `_1h`). Historical timeline RPCs use a stitched multi-tier query that serves each time slice from the finest-retention tier that covers it — recent data at 1-second fidelity, older data smoothly coarser. The UI shell ships a Traffic tab with a time-range dropdown (5 Minutes live + 1 Hour / 24 Hours / 7 Days / 30 Days / All Time / Custom historical) and a chart that guarantees "same data → same shape" regardless of which range preset is selected. Five `Get*` RPCs serve aggregated traffic data from SQLite. DNS hostname mappings are persisted to a `dns_cache` table, surviving daemon restarts. The gRPC IPC surface now has ten RPCs total. 472 tests pass deterministically. Next up: Phase 6 (remaining tab content, starting with the Firewall tab) or Phase 5.5 (settings page exposing the retention preset picker).

---

## 2. Phases Completed

### Phase 0 — Foundation (Core models and interfaces) ✅

**Purpose:** Define the domain model, value types, interfaces, and hash chain primitives that every downstream component depends on.

**Key components:**
- `Beholder.Core/` — 22 files: 5 enums (`Direction`, `FirewallAction`, `AlertKind`, `EventKind`, `RuleSource`), `CountryCode` value type, `IPAddressExtensions`, 5 record types (`FlowEvent`, `FirewallRule`, `Alert`, `ProcessInfo`, `CounterSnapshot`, `ChainVerificationResult`), 8 interfaces (`IFlowSource`, `IFirewallController`, `IGeoIpResolver`, `IEventStore`, `IAlertStore`, `IProcessRegistry`, `IDnsCache`, `IFirewallRuleStore`), `ChainHasher` static class

**Tests added:** ~75 (enum coverage, CountryCode equality/formatting, private range detection for all RFC1918/4193/5737/loopback/link-local/CGNAT ranges, record equality, defensive construction, ChainHasher known vectors/round-trip/tamper/edge cases)

**Design decisions:**
- `CountryCode` is a `readonly record struct` wrapping a two-char string, with `Local` and `Unknown` static sentinels. `default(CountryCode)` returns `Unknown` to avoid null reference traps.
- `ChainHasher.ComputeRowHash` uses `ArrayPool<byte>` for payloads exceeding a stack-allocation threshold, avoiding GC pressure on the hot path.
- `IEventStore` was split from an original combined interface into `IEventStore` (append + verify) and `IAlertStore` (read alerts + mark read) to satisfy ISP.
- `IFirewallRuleStore` was extracted during Phase 4 checkpoint to decouple `BeholderLocalService` from `SqliteFirewallRuleStore`.
- Proto enum ordinals mirror Core C# enum ordinals exactly so that wire values round-trip by cast. This means proto enums do NOT follow the `*_UNSPECIFIED = 0` convention.

---

### Phase 1 — Storage (SQLite + chain) ✅

**Purpose:** Implement the persistence layer: schema creation, chain-hashed event store, firewall rule store, and process registry.

**Key components:**
- `Beholder.Daemon/Storage/DatabaseInitializer.cs` — idempotent schema creation (5 tables, 2 indexes, WAL mode)
- `Beholder.Daemon/Storage/SqliteEventStore.cs` — `IEventStore`: chain-hashed append with `SemaphoreSlim` single-writer, injected `TimeProvider`
- `Beholder.Daemon/Storage/SqliteFirewallRuleStore.cs` — `IFirewallRuleStore`: upsert via `INSERT ... ON CONFLICT ... DO UPDATE ... RETURNING`
- `Beholder.Daemon/Storage/SqliteProcessRegistry.cs` — `IProcessRegistry`: upsert preserving `first_seen`, nullable SHA-256
- `Beholder.Daemon/Storage/ConnectionFactory.cs` — centralizes connection string construction with optional pooling control

**Tests added:** ~45 (schema verification, chain linkage, 100-task concurrent append stress, corruption detection for row_hash/prev_hash/payload, CRUD for all stores, guard clauses)

**Design decisions:**
- Single-writer semaphore on `SqliteEventStore.AppendAsync` ensures deterministic chain ordering without SQLite-level locking contention.
- `DatabaseInitializer` is synchronous — SQLite DDL is fast enough that async adds complexity for no benefit.
- `ConnectionFactory` accepts an optional `pooling` parameter (default `true`). Tests pass `pooling: false` to avoid `SqliteConnection.ClearAllPools()` process-global interference under parallel xUnit execution.

---

### Phase 2 — Platform provider (Windows) ✅

**Purpose:** Implement Windows-specific network capture, DNS cache, firewall control, and the flow accumulation pipeline.

**Sub-phases:**

**2.1 — EtwFlowSource** ✅
Subscribes to NT Kernel Logger ETW session via `KernelTraceEventParser` for 8 TCP/UDP send/recv events (IPv4 + IPv6). Resolves PID to process name/path via `IProcessPathResolver` with `ConcurrentDictionary` cache.

**2.2 — EtwDnsCache** ✅
Subscribes to `Microsoft-Windows-DNS-Client` ETW provider to passively capture DNS query results. Builds `ConcurrentDictionary<IPAddress, string>` mapping IPs to queried hostnames. Exposes `IDnsCache` interface.

**2.3 — Accumulator** ✅
Consumes `FlowEvent` from `Channel<FlowEvent>`, aggregates per-process byte deltas, emits `CounterSnapshot` batches on a configurable tick interval. Tracks active connections, per-country byte breakdowns, and monotonic totals.

**2.4 — FlowEventPipeline** ✅
Hosted service wiring `IFlowSource → Channel<FlowEvent> → Accumulator → BroadcastService`. Orchestrates start/stop lifecycle. Channels are bounded with `DropOldest` backpressure.

**2.5 — WfpFirewallController** ✅
`IFirewallController` implementation using `INetFwPolicy2` COM interop via `dynamic` dispatch. Encodes `(ProcessPath, Direction)` into rule names via base64 for reliable round-tripping. `FirewallRuleNameEncoder` handles the encoding.

**Tests added:** ~30 (Accumulator: single/multi-event aggregation, multi-process separation, delta reset, monotonic totals, inactive process omission, active connection counting, per-country aggregation; DNS cache address extraction; firewall rule name encoding round-trip)

**Design decisions:**
- ETW session uses `KernelTraceEventParser`, NOT the `Microsoft-Windows-Kernel-Network` manifest provider (TraceEvent cannot decode its events).
- `EtwFlowSource` implements `IAsyncDisposable` to avoid banned sync-over-async in `Dispose`.
- `Accumulator` exposes a `SetWaitSignal` method for deterministic test synchronization with `FakeTimeProvider`. This is production code that exists solely for testability — the alternative was non-deterministic `Task.Delay` polling in tests.
- `dynamic` COM interop is permitted in `WfpFirewallController` per coding standards exception for `[SupportedOSPlatform("windows")]` types.

---

### Phase 3 — GeoIP ✅

**Purpose:** Enrich network flows with country-level geolocation.

**3.1 — DbIpProvider** ✅
`IGeoIpResolver` implementation using DB-IP Lite MMDB via `MaxMind.Db.Reader`. LRU cache capped at 10,000 entries. Private/reserved ranges short-circuit to `CountryCode.Local` without MMDB access.

**3.2 — GeoIpFlowSourceDecorator** ✅
Decorator pattern wrapping `IFlowSource`, rewriting `FlowEvent.Country` via `IGeoIpResolver` before re-forwarding. `NullGeoIpResolver` returns `Unknown` when MMDB is unavailable (graceful degradation).

**Tests added:** ~15 (MMDB lookup for known IPs, private range handling, unknown IP handling, cache behavior, decorator event forwarding, start/stop delegation, null resolver passthrough)

**Design decisions:**
- GeoIP enrichment happens at the source level (decorator on `IFlowSource`), not in the accumulator. This means the `FlowEvent` entering the channel already has its country code, simplifying downstream consumers.
- `NullGeoIpResolver` ensures the daemon starts even without a MMDB file — country codes will be `Unknown` but nothing crashes.

---

### Phase 4 — gRPC protocol and daemon IPC server ✅

**Purpose:** Define the IPC contract and implement the daemon-side gRPC service for UI communication.

**4.1 — Protocol definition** ✅
Two `.proto` files: `beholder_local.proto` (daemon ↔ UI, 5 RPCs) and `beholder_uplink.proto` (daemon → aggregator). `ProtocolConverters.cs` provides extension-method adapters between Core domain types and protobuf types. `FirewallRulePayloadEncoder` produces deterministic JSON for chain payloads.

**4.2 — Subscribe + BroadcastService** ✅
`BroadcastService` fans `OnSnapshotBatch` events into per-subscriber bounded channels. `Subscribe` RPC streams `DaemonEvent` messages for the connection lifetime.

**4.3 — GetSnapshot RPC** ✅
Returns current daemon state: all active process snapshots, firewall rules, and recent alerts in a single response.

**4.4 — ApplyFirewallRule RPC** ✅
Validates request → calls `IFirewallController.AddRuleAsync` → persists to `IFirewallRuleStore` → appends chain event → broadcasts `RuleChange` to subscribers. On persist failure, rolls back the OS rule via `RemoveRuleAsync`. On chain append failure, logs but still returns success (firewall enforcement is more important than audit logging).

**4.5 — VerifyChain + MarkAlertRead RPCs** ✅
`VerifyChain` delegates to `IEventStore.VerifyAsync`. `MarkAlertRead` stamps `first_viewed_at` idempotently (second mark preserves original timestamp).

**Tests added:** ~50 (protocol converter round-trips, broadcast service lifecycle/backpressure/multi-subscriber, accumulator with broadcast, firewall rule application with chain logging, persist-failure rollback, OS-failure abort, chain-append-failure graceful degradation, verify chain for empty/valid/corrupted chains, infrastructure-failure error mapping, mark alert read validation/idempotency)

**Design decisions:**
- `BeholderLocalService` is registered as a singleton, not transient-per-request. It holds references to the pipeline and stores that are themselves singletons.
- `ApplyFirewallRule` uses a compensating transaction pattern: apply OS rule → persist to DB → if persist fails, remove OS rule. This prevents ghost rules that exist in the OS but not in Beholder's database.
- Chain append failures in `ApplyFirewallRule` are logged but do not fail the RPC. The firewall rule is successfully enforced and persisted; only the audit trail is degraded.
- `ChainVerificationResult.ToProto()` uses proto3 sentinel conventions: `FailedAtSeq = 0` and `ErrorMessage = ""` for success (no wrapper types needed).

---

### Phase 4.5 — Stability fixes ✅

**Purpose:** Eliminate flaky test failures discovered during Phase 4 checkpoint reviews.

**Key fixes:**
- **Accumulator test synchronization:** Added settle-signal protocol to `DriveTickAsync` — installs a `TaskCompletionSource` before `FakeTimeProvider.Advance` and waits for the accumulator to re-enter `WaitForEventOrTickAsync` after flushing. This closes the race where the next `DriveTickAsync` call starts before the accumulator is parked.
- **SQLite connection pooling interference:** Replaced `SqliteConnection.ClearAllPools()` (process-global) with per-test `Pooling=false` on `ConnectionFactory` and `DatabaseInitializer`. Eliminated `ObjectDisposedException` under parallel xUnit execution.
- **Phase 4 checkpoint fixes:** Updated stale XML docs, reduced counter logging from `Information` to `Debug`, added `GetSnapshot` exception handling, registered `BeholderLocalService` as singleton, extracted `IFirewallRuleStore` interface, extracted shared test doubles (`FakeServerCallContext`, `FakeFirewallController`, `FakeFlowSource`, `FakeSnapshotBatchSource`), added persist-failure rollback test, added `VerifyChain` infrastructure-failure test, verified `AlertKind` ordinal alignment with proto.

---

### Phase 4.6a — Historical traffic storage (single-tier) ✅

**Purpose:** Replace the `Accumulator` with a `TrafficEngine` that persists per-destination traffic to SQLite, enabling all historical traffic queries. This is the first tier (`traffic_buckets_10s`, 10-second resolution, 30-day retention) of a planned five-tier rollup cascade.

**Key components:**
- `Beholder.Core/TrafficBucket.cs` — Per-destination, per-10-second stored row
- `Beholder.Core/TrafficTimePoint.cs` — Single point on a time-series chart
- `Beholder.Core/DestinationSummary.cs` — Aggregated traffic to one remote host
- `Beholder.Core/CountryTrafficSummary.cs` — Per-country aggregate
- `Beholder.Core/ITrafficStore.cs` — Persistence + query interface (6 methods)
- `Beholder.Core/IDnsCacheStore.cs` — Persistent DNS cache interface (3 methods)
- `Beholder.Daemon/TrafficStorageOptions.cs` — Configuration (retention, bucket size, eviction timeouts)
- `Beholder.Daemon/Storage/SqliteTrafficStore.cs` — `ITrafficStore` implementation
- `Beholder.Daemon/Storage/SqliteDnsCacheStore.cs` — `IDnsCacheStore` implementation
- `Beholder.Daemon/Pipeline/TrafficEngine.cs` — Replaces `Accumulator.cs`, same external contract (`OnSnapshotBatch`, `GetCurrentSnapshotsAsync`, `SetWaitSignal`) plus SQLite persistence
- `Beholder.Daemon/Storage/DatabaseInitializer.cs` — Added `traffic_buckets_10s` table, `dns_cache` table, 3 indexes
- `Beholder.Protocol/Protos/beholder_local.proto` — 8 new messages, 4 new RPCs
- `Beholder.Protocol/ProtocolConverters.cs` — ToProto/ToDomain for traffic types + `FromUnixTimeNanoseconds`
- `Beholder.Daemon/Grpc/BeholderLocalService.cs` — 4 new RPC implementations
- `Beholder.Daemon/Program.cs` — DI registration for stores and options

**Files deleted:** `Beholder.Daemon/Pipeline/Accumulator.cs`, `Beholder.Tests/AccumulatorTests.cs`

**Tests added:** ~106 new tests (4 record type test files, SqliteTrafficStore round-trip/query/prune tests, SqliteDnsCacheStore upsert/resolve/prune tests, TrafficEngine 1s-tick/10s-bucket/DNS-joining/eviction tests, protocol converter round-trip tests, DatabaseInitializer schema verification updates)

**Architecture:**
- **TrafficEngine** produces TWO outputs from one event stream: (1) per-second `CounterSnapshot` batches for live IPC, (2) per-10-second `TrafficBucket` rows in SQLite
- **In-memory state is bounded:** `DestinationAggregate` (tick + bucket deltas, NO cumulative totals) evicted after 5 min idle; `ProcessLifetimeTotals` (session-scoped) evicted after 1 hr idle
- **SQLite is authoritative:** all historical queries hit `traffic_buckets_10s` directly. Per-destination cumulative totals are SQL aggregates, never in-memory
- **DNS hostnames** joined at 10s flush time via `IDnsCache.Resolve()` and persisted to `dns_cache` table

**Design decisions:**
- Replaced `Accumulator` entirely rather than bolting on a second channel. Historical traffic data IS the primary data, not secondary.
- Named table `traffic_buckets_10s` (not `traffic_buckets`) to document it as one tier in a future cascade. Phase 4.6b (merged) adds `traffic_raw` at 1 s and the coarser `_1m`/`_10m`/`_1h` tiers in a single unified rollup cascade.
- Rollup invariant: `SUM(bytes)` over a time range must be identical regardless of which tier is consulted. Coarser tiers are built by summing finer-tier rows.
- Destination eviction flushes non-zero bucket bytes to SQLite before removing — never evict data that has not been persisted.
- Process lifetime totals are NOT reconstructed from SQLite on restart. They start from zero; the UI already handles daemon-reset detection.

---

### Phase 4.7 — Self-traffic filter ✅

**Purpose:** Stop the daemon from recording its own gRPC chatter with the UI. Without the filter, `Beholder.Daemon` and `Beholder.Ui` are the #1 recorded processes in `traffic_buckets_10s`, accumulating ~50 MB/month of noise at default retention.

**Key components:**
- `Beholder.Daemon/RecordingOptions.cs` — New options class bound from the `"Recording"` section of `appsettings.json`. Single flag: `FilterSelfTraffic` (default `true`).
- `Beholder.Daemon/Pipeline/SelfTrafficFilter.cs` — Static helper with an `OrdinalIgnoreCase` `HashSet<string>` of known Beholder executable filenames (`Beholder.Daemon[.exe]`, `Beholder.Ui[.exe]`) and an `IsSelfProcess(processPath)` method.
- `Beholder.Daemon/Pipeline/FlowEventPipeline.cs` — Injects `IOptionsMonitor<RecordingOptions>`; `OnFlowEventReceived` early-returns when the filter is enabled and the event's process matches. Events never reach the channel, engine, store, or broadcast path.
- `Beholder.Daemon/Program.cs` — `Configure<RecordingOptions>(Configuration.GetSection("Recording"))` registration. First option class in the daemon bound via `Configure<T>()`; `TrafficStorageOptions` remains a plain singleton for now.
- `Beholder.Daemon/appsettings.json` — Added `"Recording": { "FilterSelfTraffic": true }` section.

**Tests added:** 7 `SelfTrafficFilterTests` directly unit-testing `IsSelfProcess` — exe match, UI match, unrelated process, case-insensitive, Linux no-extension (daemon + UI), substring-of-known-name rejection.

**Design decisions:**
- **Filter at ingestion, not at storage.** The check sits in `FlowEventPipeline.OnFlowEventReceived`, before the bounded `Channel<FlowEvent>`. This guarantees filtered data is invisible to `TrafficEngine`, `SqliteTrafficStore`, `BroadcastService`, in-memory counters, and the UI — in one place.
- **Filename match, not PID or full path.** Works across Debug/Release builds, installation paths, service vs. console runs, and future Linux/macOS deployments with zero config changes. False-positive risk (unrelated binary named exactly `Beholder.Daemon.exe`) is negligible.
- **`IOptionsMonitor`, not `IOptions`.** Per-event cost is one virtual-call + one field read, trivially cheap. The benefit is that a future settings UI can flip the flag live without restarting the daemon.
- **Testable helper, not inline pipeline code.** `SelfTrafficFilter` is a pure static class with one reason to change (the filter list), unit-testable directly without pipeline plumbing. The seven tests in `SelfTrafficFilterTests.cs` map 1:1 to the matching cases and serve as the regression guard against anyone replacing `HashSet.Contains` with a loose string search.
- **JSON config, not TOML.** The daemon uses ASP.NET Core's `appsettings.json` loader via `WebApplication.CreateBuilder`. No TOML infrastructure exists in the repo; adding the `"Recording"` section to `appsettings.json` is zero-infrastructure. If TOML is adopted later, the section name and shape carry over unchanged.

**Files NOT touched:** `TrafficEngine.cs`, `SqliteTrafficStore.cs`, `BroadcastService.cs`, any UI or protocol file — the filter is invisible to every layer downstream of ingestion, and to every layer upstream of the protocol wire.

**Future work:** The v1 filter is deliberately one switch. Granular recording policy (per-path exclusion lists, localhost-only, port ranges) is deferred to the Settings UI phase and will live behind the same `"Recording"` config section.

---

### Phase 4.6b — Full rollup cascade (merged 4.6b + 4.6c) ✅

**Purpose:** Add the remaining four tiers above and below `traffic_buckets_10s` so every historical query hits the most efficient resolution for its time range. Previously split into 4.6b (raw tier) + 4.6c (coarser tiers); merged because the tier-selection logic, rollup service, and invariant enforcement — the expensive parts — exist only once the cascade has more than one tier.

**Key components:**
- `Beholder.Daemon/RollupOptions.cs` — `RollupOptions` class with `RetentionPreset` enum (`Balanced` / `Compact`), `RollupTier` record with nullable `TimeSpan?` retention. Preset bound from `appsettings.json` `"Rollup"` section via `IOptionsMonitor<RollupOptions>`.
- `Beholder.Daemon/Storage/TierSelector.cs` — Pure static helper: picks the coarsest tier whose `BucketSeconds ≤ resolution` and `Retention ≥ range`. Fallback: finest tier whose retention covers the range. Terminal tier (`_1h`, null retention = infinite) always covers.
- `Beholder.Daemon/Pipeline/RollupService.cs` — New hosted service. Cascades via `INSERT ... SELECT ... GROUP BY` per adjacent tier pair. Watermark via `MAX(bucket_start_ms) + target_bucket_ms`. Null-retention tiers skip pruning. First-tick catch-up runs all pairs regardless of interval.
- `Beholder.Daemon/Storage/DatabaseInitializer.cs` — 4 new tables (`traffic_raw`, `traffic_buckets_1m`, `traffic_buckets_10m`, `traffic_buckets_1h`) + 12 new indexes. Idempotent.
- `Beholder.Daemon/Pipeline/TrafficEngine.cs` — Switched from 10-second to 1-second raw flush. Each tick writes one raw bucket per active destination. `BucketBytesIn/Out` → `RawBytesIn/Out`. Engine no longer owns pruning or bucket-cadence config.
- `Beholder.Daemon/Storage/SqliteTrafficStore.cs` — Writes go to `traffic_raw` via `WriteRawBucketsAsync`. All query methods internally call `TierSelector.Select` to pick the table. `PruneAsync` removed from `ITrafficStore` (pruning moved to `RollupService`).
- `Beholder.Daemon/TrafficStorageOptions.cs` — Shrunk to `IdleDestinationTimeoutMinutes` + `IdleProcessTimeoutHours` only. `RetentionDays`, `PruneIntervalHours`, `BucketSeconds` removed (those concerns moved to `RollupOptions`/`RollupService`).
- `Beholder.Daemon/Program.cs` — `Configure<RollupOptions>(GetSection("Rollup"))`. `RollupService` registered as hosted after `FlowEventPipeline` for startup ordering.
- `docs/ARCHITECTURE.md` — New ~60-line "Storage Rollup Architecture" subsection: cascade diagram, rollup invariant statement, tier-selection rule, watermark strategy, both presets with storage tables, nullable terminal retention, future `RetentionOptions.MaxDataAge` forward hook.

**Tier retentions (Balanced preset, default):**

| Tier | Bucket size | Retention | Rollup interval |
|---|---|---|---|
| `traffic_raw` | 1 s | 10 min | 10 s |
| `traffic_buckets_10s` | 10 s | 7 d | 1 min |
| `traffic_buckets_1m` | 1 min | 14 d | 10 min |
| `traffic_buckets_10m` | 10 min | 1 y | 1 h |
| `traffic_buckets_1h` | 1 h | ∞ (never prune) | — |

Compact preset differs only in retention: `_10s=3d`, `_1m=7d`, `_10m=90d`, `_1h=∞`. Storage: ~1.4 GB year 1 (Balanced) vs ~580 MB (Compact) at ~100 active destinations.

**Tests added:** 26 new tests across 3 new test files:
- `RollupOptionsPresetTests.cs` (7) — both presets' tier shapes, nullable terminal invariant, preset switching, bucket-seconds equality guard.
- `TierSelectionTests.cs` (9) — live range, coarse resolution, medium/long/historical ranges, fallback cases, range-beyond-retention.
- `RollupServiceTests.cs` (10) — empty raw, raw→10s single/multi-process, full cascade, **rollup invariant** (SUM equality across all retained tiers), watermark resume, retention prune, null-retention skip, partial bucket not rolled, first-tick catch-up.

Existing tests updated: `SqliteTrafficStoreTests` (rename pass + `CreateBucket` default to `bucketSeconds: 1`), `TrafficEngineTests` (6 bucket-flush tests → 7 raw-flush tests adapted to 1-second cadence), `FakeTrafficStore` (interface alignment).

**Design decisions:**
- **Engine writes raw, service rolls up.** One write path (raw), one derivation path (cascades). The engine is now unaware of tiers; it just writes 1-second buckets and produces CounterSnapshot batches.
- **Uniform schema across all five tiers.** Same columns everywhere. Tier selection is a table-name swap.
- **Tier selection inside the store.** `ITrafficStore.Get*Async` signatures unchanged. `BeholderLocalService` and the gRPC protocol are unaware of tiering.
- **Watermark = `MAX(bucket_start_ms) + target_bucket_ms`.** The naive `MAX` approach double-counted the last target bucket's source rows (caught by `Watermark_ResumesFromMaxTarget`). Fixed: watermark points to the NEXT expected target bucket.
- **Two hand-tuned presets, not per-tier config.** Exposing individual tier retentions in `appsettings.json` creates invalid combinations that break tier selection. The two presets are hand-checked. Power users switch via `"Rollup": { "Preset": "Compact" }`; full customization deferred to a future settings page.
- **Terminal tier retention is `TimeSpan?`.** Null means "never prune". Both presets use null on `_1h`. ~90 MB/year unbounded growth. Future `RetentionOptions.MaxDataAge` knob caps it.
- **No data migration on upgrade.** Coarser tiers start empty and populate forward. Pre-existing `_10s` data queryable via its (now 7-day) retention window.
- **Self-traffic filter interacts cleanly.** Filtered events never enter raw, so no Beholder-process rows exist in any tier.

**Files NOT touched:** Any UI file, `.proto` files, `BroadcastService`, `BeholderLocalService`, `SelfTrafficFilter`, `RecordingOptions`.

---

### Phase 5.4.1 — Traffic tab corrective fixes ✅

**Purpose:** Fix three user-reported bugs in the Traffic tab and one latent rendering artifact, then ensure the UI properly seeds historical data on reconnect so the tab doesn't start from zero when the UI is closed and reopened while the daemon runs.

**Key components:**
- `Beholder.Ui/ViewModels/TrafficTabViewModel.cs` — `SortProcessList` rewritten from indexer-assignment insertion sort to `ObservableCollection.Move`-based reorder (fixes selection deselecting on every tick). Idle-process filter added to `UpdateFromStates` via new `RemoveProcess` helper — processes whose 5-minute rolling window is all zeros are dropped from the display list. `OnSelectedProcessChanged` falls back to `_allProcessesItem` when selection is cleared (e.g., selected process goes idle and is removed). `LoadHistoricalDataAsync` removed — replaced by `ProcessStateService.SeedAsync` which seeds per-process state from daemon historical data before the live stream starts.
- `Beholder.Ui/Views/Tabs/TrafficTabView.axaml` — Chart title changed from `OUTBOUND · BYTES/SEC` to `TRAFFIC · BYTES/SEC` to match the two-directional (download + upload) chart content.
- `Beholder.Ui/Controls/TrafficChartControl.cs` — Catmull-Rom spline overshoot clamped via `ClampY` helper. Both `StrokeSmoothPath` and `FillSmoothArea` now clamp control-point Y coordinates to `[top, baselineY]`, preventing curves from dipping below the 0 B/s baseline on downward transitions or bulging above the data envelope on upward transitions.
- `Beholder.Ui/Services/ProcessStateService.cs` — New `SeedAsync` method: on daemon connect, calls `GetSnapshotAsync` to populate per-process `TotalBytesIn/Out`, then calls `GetProcessTimelineAsync` per process to backfill the 5-minute `RecentDeltaIn/Out` circular buffers from `traffic_raw`. Constructor now takes `IDaemonClient` as a second parameter.
- `Beholder.Ui/Services/DaemonStreamSubscriber.cs` — New `OnConnected` async callback property, invoked between `WaitForConnected` and `ConsumeStream`. Wired to `ProcessStateService.SeedAsync` in `App.axaml.cs`. Ensures seeding completes before any live `CounterBatch` events arrive, eliminating the chart race condition.
- `Beholder.Ui/App.axaml.cs` — Passes `_daemonClient` to `ProcessStateService` constructor; wires `_streamSubscriber.OnConnected = ct => processStateService.SeedAsync(ct)`.

**Tests added:** 3 regression tests in `TrafficTabViewModelTests.cs`:
- `SelectedProcess_SurvivesReSortFromStateUpdate` — asserts `replaceCount == 0` via `CollectionChanged` observation (the hard regression guard against indexer-assignment sort).
- `IdleProcess_RemovedFromList_OnSubsequentStateUpdate` — all-zero rolling window → process removed.
- `SelectedProcess_GoesIdle_FallsBackToAllProcesses` — null write-back from idle-process removal → `SelectedProcess` recovers to `_allProcessesItem`.

**Design decisions:**
- **Move-based sort, never indexer assignment.** `ObservableCollection.Move` raises `NotifyCollectionChangedAction.Move`, which Avalonia's `SelectingItemsControl` handles by keeping the selection attached to the moved item. Indexer assignment (`coll[i] = x`) raises `Replace`, which clears selection. A code comment at the top of `SortProcessList` documents this invariant.
- **Idle filter at the ViewModel, not the service.** `ProcessStateService` keeps tracking all processes (including idle ones) because `StatusStripViewModel` needs cumulative totals for every-process-ever-seen. The idle display filter is a view concern, not a data concern.
- **Reconnect seeding runs before the live stream.** The `OnConnected` callback in `DaemonStreamSubscriber` fires between `WaitForConnected` and `ConsumeStream`, so the first live `CounterBatch` arrives into pre-populated circular buffers. No race condition, no chart flash-then-clear.
- **Per-process historical backfill is O(N) RPCs.** Each process gets a `GetProcessTimelineAsync(path, now-5min, now, 1s)` call (~300 rows from `traffic_raw` over local IPC). At N=50 processes, total seeding time is ~50-100 ms. Best-effort: individual failures don't block the live stream.
- **Chart clamp, not monotone cubic.** `ClampY(y, top, baselineY)` via `Math.Clamp` is the minimum-diff fix. A full monotone cubic interpolant (Fritsch-Carlson) would also eliminate overshoot but changes the curve character; the clamp preserves the existing Catmull-Rom aesthetic while constraining extrema.

**Files NOT touched:** Any daemon file, `.proto` files, theme files, `ProcessState.cs`, `ProcessListItem.cs`. The fixes are purely UI-layer (ViewModel, View, Control, Services).

---

### Phase 5.4.2 — Time-range selector UI ✅

**Purpose:** Wire the Traffic tab's placeholder LAST-N button to the tiered query layer from Phase 4.6b. User picks `5 Minutes / 1 Hour / 24 Hours / Last 7 Days / Last 30 Days / All Time / Custom`; the `5 Minutes` option streams live from the circular buffers; all other presets trigger a historical query against the daemon and render a point-in-time snapshot.

**Key components:**
- `Beholder.Ui/Models/TimeRangeSelection.cs` — new `TimeRangePreset` enum (7 values) and `TimeRangeSelection` record exposing `From`, `To`, `Label`, `IsLive`, plus static `FromPreset` and `FromCustom` factories. All time math is in this one type.
- `Beholder.Ui/Controls/TimeRangeDropdown.axaml[.cs]` — reusable `UserControl` encapsulating the dropdown button + flyout + custom date-range picker. Exposes a `SelectedRange` bindable property. Internal state machine: preset list ↔ custom picker panel.
- `Beholder.Ui/Views/Tabs/TrafficTabView.axaml` — adds the dropdown to the top bar, shifted GRAPH/COLS to column 2. `ColumnDefinitions="*,Auto,Auto"`, 12 px margin separating the dropdown from GRAPH/COLS.
- `Beholder.Ui/ViewModels/TrafficTabViewModel.cs` — `SelectedTimeRange` observable property defaulting to `Last5Minutes`; `OnSelectedTimeRangeChanged` routes to `LoadHistoricalRangeAsync` for historical presets or resumes live rebuilding for `Last5Minutes`. New `LoadHistoricalRangeAsync` method queries `GetAggregateTimelineAsync` + `GetProcessSummariesAsync` and populates the chart and process list from the response. `UpdateFromStates` gains an early return when not in live mode, so live ticks don't overwrite the historical snapshot.
- `Beholder.Ui/Controls/TrafficChartControl.cs` — added `DataSpan` property. `DrawTimeLabels` adapts label format based on total span: `-M:SS` for ≤10 min, `-Hh Mm` for ≤24 h, `-Nd Hh` for longer. No more hard-coded 5-minute assumption.

**New daemon-side RPC:** `GetProcessSummaries` (added this phase because `GetSnapshot` only surfaces processes currently tracked by the engine — historical views need every process that had traffic in the range, including those evicted after 1 h idle). Single SQL query against the tier-selected table: `SELECT process_path, process_name, SUM(bytes_in), SUM(bytes_out) FROM {tier} WHERE bucket_start_ms BETWEEN from AND to GROUP BY process_path, process_name ORDER BY ... DESC`. Replaces the `GetSnapshot + N × GetProcessTimeline` approach that previously fed the historical process list.

**Tests added:** `TrafficTabViewModelTests` — range switching (live → historical → live round-trip), historical-mode guard (live tick doesn't rebuild chart), historical process list populated from summaries response, custom range applies correctly.

**Design decisions:**
- **Two modes, one ViewModel.** Live (`Last5Minutes`) uses the existing circular-buffer path. All other presets issue a one-shot query and freeze the chart on the result. Live ticks continue reaching `ProcessStateService` (the status strip keeps updating) but `UpdateFromStates` no-ops for chart/list purposes while in historical mode. Avoids the conceptual mismatch of reusing a 300-entry 1-second buffer for a 30-day range.
- **Daemon owns tier selection.** The UI sends `(from, to, resolutionMs)`; the daemon picks the tier. `resolutionMs` is computed UI-side as `(to - from) / 300`, clamped to 1 s, targeting ~300 output buckets. (Phase 5.4.3 later demoted this from "target" to "advisory hint".)
- **X-axis from actual data extent, not requested range.** If a user picks "Last 30 Days" on a daemon with 3 days of data, the chart shows 3 days across its full width — not 3 days crammed into the right 10 % of a 30-day X-axis. Implemented by reading the first/last timestamps from the response and setting `ChartDataSpan` accordingly.
- **Single-point spike padding.** When the historical query returns one point (e.g., one bucket of bursty traffic), the UI pads to `[0, burst, 0, 0, 0, 0, 0, 0, 0, 0, 0]` (11 entries, burst at index 1) so the chart renders a sharp spike on the left instead of an empty canvas. The bezier code early-returns on N≤1 and the axis label code hides below tickCount<2; this workaround preserves both without branching the chart control.
- **Dropdown, not segmented control.** Seven presets + custom would overflow a segmented strip. A dropdown with grouped items (quick recent / longer historical / custom) reads naturally and leaves the top-bar geometry stable.

**Files NOT touched:** `ProcessStateService.cs` (live stream unaffected), `StatusStripViewModel.cs` (still reads from service), daemon pipeline. The phase is UI-side + one new RPC + corresponding protocol/converter/test-double changes.

---

### Phase 5.4.3 — Historical query fidelity and stability ✅

**Purpose:** Fix three closely-related defects surfaced while using Phase 5.4.2 against real data: (a) `All Time` rendered blank, (b) the same underlying data produced visibly different charts at 7d / 30d / All Time, and (c) even after (b) was fixed, the Y-axis visibly jumped 2× when switching ranges. All three failures trace back to how historical queries select tiers and compute output bucket widths. This phase replaces the original single-tier-per-query selection with a multi-tier stitched query, switches bucket-width computation from request-driven to data-extent-driven, and quantizes both the bucket width and the chart's Y-axis to stable discrete sets.

**Key components:**

- `Beholder.Daemon/Storage/TierSelector.cs` — `Select` simplified: drops the retention check, picks the coarsest tier whose `BucketSeconds ≤ resolution`, falls back to the finest tier when nothing matches. The old retention gate was causing `All Time` to pick `_1h` (only tier with `null` retention) which was empty on new daemons, producing blank charts. New method `SelectTierForAge(tiers, age)` returns the **finest** tier whose retention covers the given age — used by the stitched query below.
- `Beholder.Daemon/Storage/SqliteTrafficStore.cs` — `GetAggregateTimelineAsync` and `GetProcessTimelineAsync` delegate to a new private helper `StitchMultiTierTimelineAsync`. The helper partitions the query's time window into non-overlapping slices, finest tier first: `raw` serves `[now−10min, now)`, `_10s` serves `[now−7d, now−10min)`, `_1m` serves `[now−14d, now−7d)`, and so on back to `_1h`. Each slice's SQL runs only against its tier's table. Results are merged by output-bucket timestamp in C#. Recent data is served at 1-second native fidelity; older data progressively coarser. One SQL query per participating tier, not one per output bucket.
- Same file — new helper `ComputeDataExtentAsync` scans `MIN/MAX(bucket_start_ms)` across each participating slice's tier, clipped to the slice bounds. Returns the overall data extent within the request range, or `null` when no tier has data. Used to drive bucket-width selection.
- Same file — new static `NiceResolutionsMs = [1s, 5s, 10s, 30s, 1min, 5min, 10min, 30min, 1h, 6h, 1day]`. Effective bucket width is the smallest entry ≥ `extent/400`, floored at 1 s. Caller's `resolutionMs` is intentionally ignored — this is what makes 7d / 30d / All Time on the same data produce byte-identical arrays.
- Same file — `nowMs` is snapped down to the start of the current minute before any slice boundary is computed. Rapid re-queries within the same minute share slice bounds exactly.
- `Beholder.Ui/Controls/TrafficChartControl.cs` — `NiceMax` expanded from `{1, 2, 5, 10}` to `{1, 1.5, 2, 3, 5, 7, 10}`. Worst-case Y-axis jump at a `10^N` boundary drops from 2× to ~1.4×, so tiny residual drift in peak-bucket values doesn't produce visually jarring flips.
- `Beholder.Ui/ViewModels/TrafficTabViewModel.cs` — removed the adaptive re-query loop (single-point → widen resolution → retry). The daemon now returns well-shaped output in one round-trip because the bucket-width rule is data-driven. Single-point padding (from 5.4.2) is retained for the genuine 1-row edge case.

**Tests added:**
- `TierSelectionTests.cs` — 6 `SelectTierForAge` tests covering each tier boundary under Balanced, plus zero-age and beyond-all-finite-retentions cases. 2 existing `Select` tests updated to the simplified retention-free rule. 1 new `Select_AllTimeCoarseResolution_PicksFinerTierWhenAvailable` locks in the fix for "All Time shows different chart shape than Last 30 Days".
- `SqliteTrafficStoreTests.cs` — `GetAggregateTimelineAsync_StitchesAcrossTiers` seeds one distinguishable row in each of raw/`_10s`/`_1m` and verifies the stitched response pulls each from its correct slice. `GetAggregateTimelineAsync_SameDataDifferentRanges_ReturnsIdenticalArrays` seeds a 2-day extent in `_10s` and asserts 7d/30d/All Time return byte-identical arrays. `GetAggregateTimelineAsync_TimeDriftWithinMinute_ReturnsIdenticalArrays` asserts that advancing the fake clock 30 s within the same minute produces identical output. `GetProcessTimelineAsync_GroupsByResolution` updated: data spread widened to 2700 s so `extent/400` lands naturally at the 9-second grid the test originally exercised.
- Net test count: 457 → 472 (+15).

**Design decisions:**
- **Stitch instead of pick-one-tier.** A single-tier query for "All Time" forced a choice between fine-but-short-retention (no older data) and coarse-but-complete (no recent detail). Stitching eliminates the tradeoff: the chart's right edge is raw 1-second detail while the left edge degrades smoothly to hourly — the way users think about "zoomed out view of everything." Cost is ≤5 SQL queries instead of 1, all indexed; still sub-millisecond over local pipe.
- **Ignore the caller's `resolutionMs` for bucket-width purposes.** Honoring it re-introduced the original bug: different request ranges computed different widths for the same data. The parameter is kept in the RPC for backward compatibility (treated as a hint the daemon is free to ignore), but bucket width is derived entirely from `extent/400` rounded to `NiceResolutionsMs`. This is what makes "same data → same chart" hold as a contract, not an approximation.
- **Minute-snap for rapid-switch stability.** Without snapping, clicking 7d → 30d → All Time within seconds produced three slightly different grids (slice boundaries drifted with sub-second `nowMs`). With minute snapping, any two queries in the same wall-clock minute are bit-identical. The 1-minute ceiling on drift is invisible at chart zoom levels spanning hours/days.
- **Wider `NiceMax` set.** The peak-bucket value sitting at the `10^10` B/s boundary was oscillating between `9.99×10^9` (rounds to nice=10, Y-max = 9.31 GB/s) and `1.01×10^10` (rounds to nice=2 at the next decade, Y-max = 18.63 GB/s) — exactly 2×. Adding intermediate `{1.5, 3, 7}` reduces the worst-case boundary jump to ~1.4× and matches how commercial monitoring tools typically scale. Belt-and-suspenders fix: even if the daemon's output still has tiny per-query variance, the chart doesn't amplify it to a visible 2× flip.

**Files NOT touched:** Proto schemas, `RollupOptions`, `RollupService` (rollup-side invariant was already correct — the bugs were purely in read-side query composition), `TimeRangeSelection.cs`, any UI view XAML. Contract-level RPC signatures unchanged — `resolutionMs` still accepted, just reinterpreted.

---

## 3. Phase-by-Phase Lessons Learned

### Phase 0

- **`default(CountryCode)` must not crash.** The initial `readonly record struct` threw `NullReferenceException` on `default` because it accessed the backing string. Fixed by making the sentinel return `"??"` for `default`. Any value-type domain model should handle `default` gracefully.
- **Records with explicit constructors don't support `with` expressions.** Positional records with custom constructors lose the compiler-generated `With` method. Use primary constructors or accept that `with` won't work.
- **Interface Segregation catches real design mistakes early.** The original `IEventStore` combined append, verify, AND alert queries. Splitting into `IEventStore` + `IAlertStore` prevented downstream consumers from depending on methods they don't use.
- **Exposing mutable collections on immutable records defeats the purpose.** `CounterSnapshot.BytesOutByCountry` was `Dictionary<K,V>` — changed to `IReadOnlyDictionary<K,V>`.

### Phase 1

- **SQLite `INSERT ... ON CONFLICT ... DO UPDATE ... RETURNING` is the cleanest upsert pattern.** Avoids separate SELECT+INSERT/UPDATE round-trips and returns the final row in one statement.
- **`TimeProvider` injection is essential for deterministic tests.** Every store that stamps timestamps must accept `TimeProvider`, not call `DateTimeOffset.UtcNow`. `FakeTimeProvider` from `Microsoft.Extensions.Time.Testing` makes time-dependent tests fully deterministic.
- **`SemaphoreSlim` for async mutual exclusion.** SQLite doesn't handle concurrent writes well. A `SemaphoreSlim(1,1)` in `AppendAsync` serializes chain appends without blocking threads.

### Phase 2

- **ETW manifest providers cannot be decoded by TraceEvent without `Source.Dynamic.All`.** The `Microsoft-Windows-Kernel-Network` provider shows events as `EventID(N)` with no payload names. Use `KernelTraceEventParser` with the NT Kernel Logger instead — it has full built-in parser support for TCP/UDP events.
- **`net10.0-windows` TFM cannot be referenced from `net10.0` projects.** Use plain `net10.0` for platform projects and guard with `[SupportedOSPlatform]` attributes instead.
- **DNS happens before TCP connect.** The `EtwDnsCache` populates its mapping before the corresponding `TcpIpConnect` event arrives, making passive DNS strictly better than reverse DNS (no extra network traffic, captures actual queried domain, not CDN hostname).
- **`Channel<T>` with `BoundedChannelFullMode.DropOldest` is the right backpressure strategy.** Counter data is cumulative — dropping old samples self-corrects on the next tick. Never block the ETW callback thread.
- **`FakeTimeProvider` + `Task.Delay` interaction is subtle.** `Task.Delay(TimeSpan, TimeProvider)` registers a timer with the `FakeTimeProvider`. The timer only fires when `Advance` is called. Tests must ensure the timer is registered BEFORE calling `Advance`, or the timer is created relative to the already-advanced clock.

### Phase 3

- **Decorator pattern for cross-cutting enrichment.** `GeoIpFlowSourceDecorator` wraps `IFlowSource` and rewrites events before forwarding. This keeps the accumulator and pipeline unaware of GeoIP — enrichment is done at the source boundary.
- **Graceful degradation via null object pattern.** `NullGeoIpResolver` returns `Unknown` for all IPs when MMDB is unavailable. The daemon starts and functions; country data is simply absent.

### Phase 4

- **Compensating transactions for multi-system operations.** `ApplyFirewallRule` applies an OS rule, then persists to SQLite. If persistence fails, the OS rule must be rolled back. Without this, the OS firewall and Beholder's database diverge.
- **Proto3 sentinel conventions avoid wrapper types.** `FailedAtSeq = 0` and `ErrorMessage = ""` for success are cleaner than `google.protobuf.Int64Value` wrappers. Document the convention in the proto file comments.
- **`SqliteConnection.ClearAllPools()` is process-global.** It disposes ALL pooled connections across the entire process, not just the calling test's connections. Under parallel xUnit execution, one test's cleanup destroys another test's active connections. Fix: disable pooling in tests via `Pooling=false` connection string parameter.

### Phase 4.6a

- **Historical data is the primary data, not secondary.** The original plan was to bolt a `TrafficRecorder` alongside the `Accumulator`. But the Accumulator was destroying per-destination detail — the very data the system exists to capture. Replacing it with `TrafficEngine` eliminated the false dichotomy between "live" and "historical" data. One pipeline, two output cadences.
- **Unbounded in-memory state is an architectural bug.** The initial `DestinationAggregate` design had cumulative `TotalBytesIn/Out` fields that grow forever. The fix: store only tick deltas and bucket deltas in memory, evict idle entries, and let SQL aggregation serve cumulative queries.
- **`ArgumentException.ThrowIfNullOrWhiteSpace` throws `ArgumentNullException` for null inputs.** xUnit `Assert.Throws<ArgumentException>` requires exact type match and will not catch a subclass. Null test cases must use separate `[Fact]` methods with `Assert.Throws<ArgumentNullException>`.
- **Name tables for their tier, not their function.** `traffic_buckets_10s` (not `traffic_buckets`) documents that this is the first tier in a rollup cascade. When `traffic_buckets_1m` appears in Phase 4.6b (merged), the naming is self-explanatory.

### Phase 4.5

- **Multi-tick test synchronization requires a settle signal.** The first `DriveTickAsync` call works because the accumulator hasn't entered its wait loop yet. Subsequent calls race: the accumulator may re-enter `WaitForEventOrTickAsync` and consume a signal before the test installs one. Fix: install a settle signal before `Advance` and wait for it after the batch, guaranteeing the accumulator is parked before the next call.
- **xUnit v3 runs test classes in parallel by default.** No `[Collection]` attributes or `xunit.runner.json` overrides means all test classes execute concurrently. Any process-global side effect (connection pools, static state, temp file cleanup) will cause cross-test interference.
- **Shared test doubles eliminate duplication without coupling.** Extracting `FakeServerCallContext`, `FakeFirewallController`, etc. into `TestDoubles/` removed 4 identical copies from 3 test files without creating inappropriate dependencies.

### Phase 4.6b

- **Watermark = MAX + bucket_ms, not just MAX.** Using `MAX(bucket_start_ms)` from the target tier as the lower bound for the next rollup re-rolls source rows from the last already-populated target bucket, double-counting them. The correct watermark is `MAX(bucket_start_ms) + target_bucket_ms` — the first NEW target bucket to populate. For an empty target, start from 0. Caught by `Watermark_ResumesFromMaxTarget` test (expected 400, got 500 before fix).
- **Tier retentions should match the tier's natural query domain.** `_10s` serves queries from 30 min to ~5 hours via the tier-selection rule. Retaining it for 30 days (the Phase 4.6a default) wastes ~2 GB on rows nobody queries — 3× more than the entire rest of the cascade. Shortening `_10s` to 7 days (Balanced preset) cuts total year-1 storage from ~4.5 GB to ~1.4 GB with zero UI regression on any standard chart view.
- **Raw-tier pruning shares a tick with cascade.** The rollup service cascades THEN prunes in the same tick. After a time advance past raw's 10-minute retention, raw is empty — but the cascade has already propagated the data to `_10s`. Post-cascade assertions must not query raw if the time advance exceeds raw retention.
- **`INSERT ... SELECT ... GROUP BY` is the right cascade primitive.** Each rollup step is a single SQL statement: no intermediate materialization, no row-by-row iteration, no C# object construction for the moved data. SQLite's query planner handles the grouping and insertion efficiently, and the ACID transaction guarantees consistency if the daemon crashes mid-rollup.
- **Presets beat individual config knobs.** Exposing per-tier retention in config creates invalid combinations that break the tier-selection contract. Two hand-checked presets (Balanced / Compact) give users a meaningful choice without the combinatorial risk. Full customization deferred to a future settings page with validation.

### Phase 5.4.2

- **Two modes in one view are clearer than one unified mode.** Trying to make the chart + process list seamlessly flow between live streaming and historical snapshots creates a conceptual mismatch: a 300-entry 1-second circular buffer cannot meaningfully represent 24 hours of history, and live ticks arriving during historical viewing either overwrite the snapshot or need to be silently dropped. Splitting into explicit live vs historical modes, with a single `IsLive` predicate driving the branching, made the logic trivially correct.
- **Chart X-axis should track actual data extent, not requested range.** Users who pick "Last 30 Days" on a daemon with 3 days of data expect to see 3 days, not 3 days crammed into the right 10 % of a 30-day axis. Read the first/last timestamps from the response and compute `ChartDataSpan` from those, not from `to - from`.
- **`GetSnapshot` is in-memory; `GetProcessSummaries` is on-disk.** The engine evicts processes after 1 hour idle, so historical views that use `GetSnapshot` for the process list silently hide processes that had traffic in the range but happen to be idle now. A dedicated summaries RPC that aggregates against the tier tables is the right abstraction for any list derived from historical data.

### Phase 5.4.3

- **Stitched multi-tier beats pick-one-tier for any "zoomed-out" chart.** A single tier at "All Time" forces a choice between fine-but-short-retention (nothing old) and coarse-but-complete (no recent detail). Stitching serves each time slice from the finest tier that retains it — the chart's right edge is per-second detail while the left edge degrades smoothly to hourly. Five indexed sub-queries compose client-side at negligible cost over local IPC.
- **Same data → same chart requires a data-driven bucket width, not a request-driven one.** Computing `effectiveResolutionMs` from `(to - from)/300` makes 7d / 30d / All Time on the same underlying data produce three different grids, three different sums per bucket, and three different chart shapes. Deriving bucket width from the actual data extent inside the range (`extent/400` rounded to a nice discrete set, caller's hint ignored) restores "same data → same chart" as a hard contract, not an approximation.
- **Discrete "nice" bucket widths are necessary for query-to-query stability.** When bucket width is a continuous function of extent, sub-second drift in `nowMs` between queries shifts the GROUP BY grid by a few ms, re-assigning source rows to slightly different output buckets. A discrete set `{1s, 5s, 10s, 30s, 1min, 5min, 10min, 30min, 1h, 6h, 1day}` absorbs that drift — small extent changes stay inside the same nice bucket until they cross a threshold.
- **Minute-snap `nowMs` inside the query.** Slice boundaries (`nowMs − 10min`, `nowMs − 7d`, etc.) shift with every millisecond of real time if not snapped. Rounding `nowMs` down to the start of the current minute makes all queries issued in the same wall-clock minute bit-identical on the same data. The 1-minute ceiling on drift is invisible at historical-chart zoom levels.
- **`NiceMax` `{1, 2, 5, 10}` amplifies sub-percent value drift to 2× visual jumps.** A peak bucket value sitting at `10^N` flips between `Y-max = 10 × 10^(N−1)` and `Y-max = 2 × 10^N` — exactly 2× — when the value nudges across the decade boundary. Expanding to `{1, 1.5, 2, 3, 5, 7, 10}` caps the worst-case jump at ~1.4× and matches how commercial tools (Grafana, Datadog) scale.
- **Caller parameters can be demoted to hints without breaking the RPC contract.** The IPC proto still accepts `resolution_ms`; the daemon simply ignores it for bucket-width purposes now. This avoids a protocol breaking change while fixing the semantics — the caller's old behavior (send whatever resolution) is still accepted, just reinterpreted.

---

## 4. Checkpoint Review History

| Phase | Date | Scope | Issues Found | Resolution |
|-------|------|-------|-------------|------------|
| 0 | 2026-04-10 | Core models, interfaces, ChainHasher | `IEventStore` ISP violation; `CounterSnapshot.BytesOutByCountry` mutable dictionary; `ProcessInfo.Sha256` mutable byte array; `default(CountryCode)` NRE; missing test coverage for validation paths and ArrayPool branch | Split `IEventStore`/`IAlertStore`; immutable collections; defensive copy; `Unknown` default; added tests |
| 1 | 2026-04-10 | Storage layer (4 stores, schema) | Enum casing drift between docs and code; missing payload corruption test for `VerifyAsync`; missing guard clause tests; incomplete upsert column coverage; concurrent stress test too small | Standardized PascalCase; added corruption test; added guard tests; bumped concurrent tasks to 100 |
| 2 | 2026-04-10 | Platform layer + pipeline | `EtwFlowSource.Dispose` used banned sync-over-async; `InternalsVisibleTo` missing; magic timeout constant; formatting violations; stale terminology in docs | Added `IAsyncDisposable`; added `InternalsVisibleTo`; extracted constant; fixed formatting; updated docs |
| 4 | 2026-04-12 | gRPC service, broadcast, full daemon | Stale XML docs on `BeholderLocalService`; counter logging too verbose; `GetSnapshot` missing exception handling; `BeholderLocalService` not registered as singleton; `SqliteFirewallRuleStore` not behind interface; missing persist-failure rollback test; missing `VerifyChain` infrastructure test; duplicated test fakes across 3 files; `AlertKind` ordinal alignment unverified | Updated docs; `LogDebug`; added try/catch; singleton registration; extracted `IFirewallRuleStore`; added rollback test; added infra test; extracted shared test doubles; verified alignment |
| 4.5 | 2026-04-12 | Test stability (flaky failures) | `AccumulatorTests` ~2% timeout in multi-tick tests (settle signal race); `SqliteConnection.ClearAllPools()` ~3% `ObjectDisposedException` under parallel execution | Settle signal protocol in `DriveTickAsync`; `Pooling=false` in test `ConnectionFactory`/`DatabaseInitializer`; removed all `ClearAllPools` calls |
| 4.6a | 2026-04-13 | Historical traffic storage | `Accumulator` discarded all per-destination detail; no SQLite persistence; DNS cache in-memory only; `ArgumentNullException` vs `ArgumentException` in record test cases | Replaced `Accumulator` with `TrafficEngine`; added `traffic_buckets_10s` + `dns_cache` tables; 4 new query RPCs; bounded in-memory state with eviction; split null test cases into separate `[Fact]` methods |
| 4.6b | 2026-04-15 | Full rollup cascade (5-tier) | Watermark double-count bug (`MAX` vs `MAX + bucket_ms`); raw-tier prune timing in invariant test assertion | Fixed watermark to next-bucket boundary; excluded pruned raw tier from invariant assertion; tier retentions tuned to natural query domains (7d/14d instead of 30d/90d) |
| 5.4.2 | 2026-04-16 | Time-range selector UI + `GetProcessSummaries` RPC | Historical process list hid evicted processes; chart X-axis stretched to requested range instead of data extent; single-point responses rendered as empty canvas | New `GetProcessSummaries` RPC queries tier tables directly; `ChartDataSpan` computed from first/last response timestamps; single-point padding to 11-entry array with burst at index 1 |
| 5.4.3 | 2026-04-16 | Historical query fidelity and stability | "All Time" blank (old retention-gated tier selector picked empty `_1h`); 7d/30d/All Time produced different charts on identical data; Y-axis flipped 2× on rapid re-switching due to `NiceMax` decade-boundary quantization | Stitched multi-tier query partitions range across tier slices; bucket width driven by `extent/400` rounded to discrete `NiceResolutionsMs`; `nowMs` snapped to minute; `NiceMax` expanded to `{1, 1.5, 2, 3, 5, 7, 10}` |

---

## 5. Known Gaps and Forward-Looking Notes

- **RemoveFirewallRule RPC** — needed before Phase 6.4 (firewall tab pill toggle). Currently only `ApplyFirewallRule` exists; removing a rule requires a new RPC or extending the existing one with a `Remove` action.
- **O(n) chain verification** — `VerifyAsync` reads all rows sequentially. Fine for weeks of uptime, but months of data will need checkpoint-based verification (verify from last checkpoint instead of seq 1). Address in Phase 10.
- **Startup OS/SQLite firewall reconciliation** — on daemon restart, OS firewall rules and SQLite `firewall_rules` table may diverge (crash during apply, manual OS changes). A reconciliation pass at startup is deferred to Phase 11.
- **Linux platform (Beholder.Daemon.Linux)** — project stub exists. `NetlinkFlowSource` and `NftablesFirewallController` implementations are deferred. No timeline; Windows is the primary platform.
- **Uplink client (Beholder.Daemon.Uplink)** — project stub exists. Outbound gRPC client with connection state machine, JWT auth, and telemetry forwarding. Phase 9.
- **Uplink test stub (Beholder.Tests.UplinkStub)** — project stub exists. Reference gRPC server for uplink integration testing. Phase 9.
- **Alert pipeline (daemon side)** — `NewProcessDetector`, `BinaryHashMonitor`, `ChainIntegrityMonitor` not yet implemented. The `AlertKind` enum and `IAlertStore` interface are defined, but no daemon code generates alerts yet. Phase 7.
- **TrafficEngineTests residual flakiness** — inherited from AccumulatorTests. The settle-signal fix eliminated most failures, but ~1-2% timeout rate may persist under extreme CPU contention. Monitor during future test runs.
- **UI quality standards enforcement** — Phase 5.4 onward must comply with `docs/UI_QUALITY_STANDARDS.md`. Phases 5.1–5.3 are retroactively compliant (their quality issues were caught and fixed during manual review). Every future UI phase plan must include the verification and reference comparison sections defined in that document.
- **LiveCharts2 Avalonia 12 support** — a dev build (`2.1.0-dev-247`) is installed in `Beholder.Ui` but not yet used in any view. Evaluate stability before building the Phase 8 map tab against it.
- **Tmds.DBus.Protocol vulnerability** — force-upgraded to 0.92.0 via explicit `PackageReference` to avoid Avalonia 12's transitive pull of vulnerable 0.90.3. Monitor for Avalonia updates that resolve this transitively.

---

## 6. Remaining Phases

### Checkpoint — Historical Traffic Feature-Complete

Not a code deliverable. A project milestone marking the point at which the Traffic tab is feature-complete for historical exploration. At this point the user can: watch live traffic streaming at 1-second fidelity for the last 5 minutes, scrub back through any time range up to ~2 years via the range selector, see tier-aware aggregations for each range with the rollup invariant holding, and trust the data is free of Beholder's self-traffic.

**Deferred items** — to be picked up in later phases or at the user's direction:
- Event pins (mark "this is when I started the VPN") — deferred to a later UX pass.
- Destination breakdown panel (hosts/ports inside a selected process) — deferred; data is available via `GetProcessDestinationsAsync` but no panel exists yet.
- Per-tier retention tuning in `appsettings.json` — current values are hard-coded in `TrafficStorageOptions` C# defaults.

**Decision point.** Natural pause to validate the historical-traffic story before moving to the Firewall tab (Phase 5.5 / 6.4) or revisiting any architectural questions surfaced during 4.6b / 5.4.2 implementation (e.g., rollup cadence, eviction timing under load, or the `TrafficStorageOptions` binding wart flagged in Phase 4.7's plan).

### Phase 5 — UI shell and daemon connection

- 5.1 — Avalonia app chrome: dark theme, top navigation bar (TRAFFIC, FIREWALL, ALERTS, MAP, SCANNER tabs), bottom status strip (OUT/IN counters, WAN bar, DEV ID). No tab content yet.
- 5.2 — `DaemonClient` service: connects to named pipe, calls `GetSnapshot`, subscribes to event stream, exposes observable model for ViewModel binding.
- 5.3 — Status strip ViewModel: binds to aggregate counters, formats bytes, shows live throughput.
- Checkpoint: app launches, connects to daemon, status strip shows live numbers.

**Notes from earlier phases:** The gRPC service exposes 5 RPCs. `ProtocolConverters` handles Core ↔ Proto type mapping. The UI should use `ProtocolConverters` extension methods, not duplicate the mapping logic. Country enrichment is already done daemon-side (Phase 3.2 decorator), so the UI receives pre-resolved country codes in `CounterSnapshot`.

### Phase 6 — UI views (one tab per sub-phase)

**Quality gate:** All Phase 6 sub-phases must comply with `docs/UI_QUALITY_STANDARDS.md`. Each sub-phase plan must include the verification (three window sizes, 30s daemon uptime, real data, extreme scenario) and reference comparison sections defined in that document.

- 6.1 — Traffic tab, process list panel (sortable, color-coded, selectable)
- 6.2 — Traffic tab, graph panel (custom `Canvas`-based streaming area chart, not a charting library)
- 6.3 — Traffic tab, sub-view toggles (GRAPH / COLS / MAP within traffic panel)
- 6.4 — Firewall tab (full rule table, ALLOW/BLOCK pill toggle via `ApplyFirewallRule` RPC, undo banner). **Prerequisite:** `RemoveFirewallRule` RPC or equivalent.
- 6.5 — Firewall tab, recent activity strip
- 6.6 — Alerts tab, master-detail layout (read/unread state via `MarkAlertRead` RPC)
- 6.7 — Alerts tab, action buttons (BLOCK HOST, BLOCK PROCESS OUT, ADD RULE)
- Checkpoint: all core tabs functional end-to-end.

### Phase 7 — Alert pipeline (daemon side)

- 7.1 — `NewProcessDetector`: watches flow stream, checks `IProcessRegistry`, emits `NewProcess` alert
- 7.2 — `BinaryHashMonitor`: SHA-256 of binary on first-seen and periodically, emits `HashChanged` alert
- 7.3 — `ChainIntegrityMonitor`: runs `VerifyAsync` on startup and periodically, emits `ChainError` alert
- 7.4 — Wire detectors into pipeline, broadcast alerts via IPC
- Checkpoint: alerts flow end-to-end from daemon detection to UI display.

### Phase 8 — Map tab

- 8.1 — Map tab ViewModel: aggregates per-country byte totals, converts alpha-2 to alpha-3 for LiveCharts2
- 8.2 — Map tab View: LiveCharts2 GeoMap with Mercator projection. **Depends on:** LiveCharts2 `2.1.0-dev-247` stability (currently installed but unused). If unstable, implement as custom `Canvas`-drawn world map.

### Phase 9 — Uplink client

- 9.1 — `UplinkClient` state machine (Disconnected → Connecting → Authenticated → Streaming) with exponential backoff
- 9.2 — Telemetry forwarding (counter batches + alerts to aggregator)
- 9.3 — Remote command handling (firewall commands with capability validation)
- 9.4 — Configuration and Ed25519 key management
- Checkpoint: uplink works end-to-end against `Beholder.Tests.UplinkStub`.

### Phase 10 — Signed checkpoints and chain export

- 10.1 — `CheckpointSigner`: periodic Ed25519 signing of chain head, writes to `checkpoint` table
- 10.2 — Enhanced `VerifyChain`: also verifies checkpoint signatures. **Note:** this improves the O(n) verification gap — verify from last checkpoint instead of seq 1.
- 10.3 — Chain export: CLI subcommand or RPC for signed JSON export of filtered events

### Phase 11 — Polish and hardening

- 11.1 — Windows service installation (sc.exe or WiX installer, auto-start)
- 11.2 — Error handling sweep (every catch, every log call, every edge case)
- 11.3 — Performance profiling (24-hour soak test: memory, CPU, SQLite size, GC pressure)
- 11.4 — Configuration documentation (reference `beholder.toml` with comments)
- 11.5 — Startup reconciliation: sync OS firewall rules with SQLite `firewall_rules` table on daemon start
- Final checkpoint: install on clean machine, run for a week, understand what happened.

---

## 7. How to Update This Document

At every checkpoint review, update sections 1, 2, 3, and 4:

- **Section 1 (Status Summary):** Rewrite the paragraph to reflect current state. Update the test count and "next up" line.
- **Section 2 (Phases Completed):** Add a new entry for the completed phase. Do NOT rewrite existing entries. If a checkpoint review found issues in a previously completed phase, add the findings as an addendum under that phase's entry (e.g., "**Phase 4 checkpoint addendum:** ...").
- **Section 3 (Lessons Learned):** Add lessons from the new phase. Existing lessons are durable — don't edit them unless they're factually wrong.
- **Section 4 (Checkpoint Review History):** Append a new row to the table. Never modify existing rows.
- **Section 5 (Known Gaps):** Remove items that have been addressed. Add newly discovered deferrals. Update "when/why" explanations if scope shifts.
- **Section 6 (Remaining Phases):** Update as phases complete (move to section 2) or as scope changes. Flag invalidated assumptions from earlier phases.

Update the "Last updated" date and "Current checkpoint" marker at the top of the file.
