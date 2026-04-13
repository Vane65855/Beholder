# Beholder NMT — Project Status & Phase Plan

**Last updated:** 2026-04-13
**Current checkpoint:** Phase 4.6a (historical traffic storage)
**Test count:** 379

---

## 1. Status Summary

As of 2026-04-13, the daemon captures per-process network telemetry via ETW on Windows, enriches flows with DB-IP country codes, and now persists per-destination traffic to SQLite via a new `TrafficEngine` that replaced the `Accumulator`. The engine produces two outputs from the same event stream: per-second `CounterSnapshot` batches for the live IPC stream (unchanged from Phase 2) and per-10-second `TrafficBucket` rows in `traffic_buckets_10s` for historical queries. Four new gRPC RPCs (`GetProcessTimeline`, `GetProcessDestinations`, `GetAggregateTimeline`, `GetCountryBreakdown`) serve aggregated traffic data from SQLite. DNS hostname mappings are persisted to a `dns_cache` table, surviving daemon restarts. The gRPC IPC surface now has nine RPCs total. 379 tests pass deterministically. Next up: Phase 5 (UI shell and daemon connection).

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
- Named table `traffic_buckets_10s` (not `traffic_buckets`) to document it as one tier in a future cascade. Phase 4.6b adds `traffic_raw` (1s, 10 min retention), Phase 4.6c adds coarser tiers.
- Rollup invariant: `SUM(bytes)` over a time range must be identical regardless of which tier is consulted. Coarser tiers are built by summing finer-tier rows.
- Destination eviction flushes non-zero bucket bytes to SQLite before removing — never evict data that has not been persisted.
- Process lifetime totals are NOT reconstructed from SQLite on restart. They start from zero; the UI already handles daemon-reset detection.

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
- **Name tables for their tier, not their function.** `traffic_buckets_10s` (not `traffic_buckets`) documents that this is the first tier in a rollup cascade. When `traffic_buckets_1m` appears in Phase 4.6c, the naming is self-explanatory.

### Phase 4.5

- **Multi-tick test synchronization requires a settle signal.** The first `DriveTickAsync` call works because the accumulator hasn't entered its wait loop yet. Subsequent calls race: the accumulator may re-enter `WaitForEventOrTickAsync` and consume a signal before the test installs one. Fix: install a settle signal before `Advance` and wait for it after the batch, guaranteeing the accumulator is parked before the next call.
- **xUnit v3 runs test classes in parallel by default.** No `[Collection]` attributes or `xunit.runner.json` overrides means all test classes execute concurrently. Any process-global side effect (connection pools, static state, temp file cleanup) will cause cross-test interference.
- **Shared test doubles eliminate duplication without coupling.** Extracting `FakeServerCallContext`, `FakeFirewallController`, etc. into `TestDoubles/` removed 4 identical copies from 3 test files without creating inappropriate dependencies.

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
- **Tiered rollup storage (Phase 4.6b/c)** — Phase 4.6a implements only the `traffic_buckets_10s` tier (10s resolution, 30-day retention). Phase 4.6b will add `traffic_raw` (1s, 10 min retention) with raw→10s rollup. Phase 4.6c will add `traffic_buckets_1m`, `traffic_buckets_10m`, `traffic_buckets_1h` with full cascade. Until then, all queries hit the single `_10s` table regardless of requested time range.
- **UI quality standards enforcement** — Phase 5.4 onward must comply with `docs/UI_QUALITY_STANDARDS.md`. Phases 5.1–5.3 are retroactively compliant (their quality issues were caught and fixed during manual review). Every future UI phase plan must include the verification and reference comparison sections defined in that document.
- **LiveCharts2 Avalonia 12 support** — a dev build (`2.1.0-dev-247`) is installed in `Beholder.Ui` but not yet used in any view. Evaluate stability before building the Phase 8 map tab against it.
- **Tmds.DBus.Protocol vulnerability** — force-upgraded to 0.92.0 via explicit `PackageReference` to avoid Avalonia 12's transitive pull of vulnerable 0.90.3. Monitor for Avalonia updates that resolve this transitively.

---

## 6. Remaining Phases

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
