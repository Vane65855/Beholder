# Beholder NMT — Project Status & Phase Plan

**Last updated:** 2026-05-19
**Current checkpoint:** Phase 9.3 (Scanner IPC — adds `ListLanDevices` and `TriggerScan` RPCs to `BeholderLocal` and extends the `DaemonEvent` stream with `LanDeviceFirstSeenEvent` / `LanDeviceMacChangedEvent` variants. RPC surface 15 → 17. Also closes an implicit gap from 9.2: `LanScannerService` now broadcasts the chain events it has been writing all along — every mutable event goes to both chain audit and live stream uniformly. `LanScannerService` exposes a public `RunOnceManuallyAsync(CancellationToken)` for `TriggerScan` to call, guarded by a `SemaphoreSlim _scanGate` that serialises timer-driven and RPC-driven scans so they never overlap. UI side: `DaemonClient` + `DaemonStreamSubscriber` gain wrappers + event dispatch for both new variants; the Scanner-tab stub stays untouched — view layer is Phase 9.4 territory.)
**Test count:** 1060

---

## 1. Status Summary

As of 2026-05-03, the daemon captures per-process network telemetry via ETW on Windows, enriches flows with DB-IP country codes, and persists per-destination traffic to SQLite through a five-tier rollup cascade (`traffic_raw` → `_10s` → `_1m` → `_10m` → `_1h`). Historical timeline RPCs use a stitched multi-tier query that serves each time slice from the finest-retention tier that covers it — recent data at 1-second fidelity, older data smoothly coarser. The Traffic tab ships with a time-range dropdown (5 Minutes live + 1 Hour / 24 Hours / 7 Days / 30 Days / All Time / Custom historical) and a chart that guarantees "same data → same shape" regardless of which range preset is selected. The **Firewall** tab is a working surface: rule table with three-state ALLOW/BLOCK/DEFAULT pills, GlassWire-style Active/Inactive grouping with the orphaned-rule warning glyph for missing binaries, master ON/OFF toggle, recent-activity strip backed by the chain-hashed event log, and Alerts → Firewall deep-linking that auto-expands the row's group and scrolls it into view. The **Alerts** tab is now feature-complete end-to-end: master-detail layout with optimistic mark-read, BLOCK/UNBLOCK toggle and ADD RULE deep-link, dismissable error banner with auto-clear-on-action, OS-native toast notifications via a Windows-conditional `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 dependency (toast surface lives inline in `Beholder.Ui/Services/WindowsNotificationService.cs` behind `#if PLATFORM_WINDOWS` per ADR 008; click-activation restores the window and selects the matched alert), and detail-pane disable when the alert's binary no longer exists on disk. The **alert-generation pipeline** runs entirely daemon-side: `NewProcessDetector`, `BinaryHashMonitor`, and `ChainIntegrityMonitor` emit the three alert kinds via the `AlertEmitter` facade (chain-write + broadcast in one call) and `BroadcastService.BroadcastAlert` streams them to the UI. Phase 7.5 added **logical app identity + Authenticode spoof detection** (ADR 007): NewProcess dedup is now keyed by `(CompanyName, ProductName, install-root)` from PE VersionInfo when a Valid signature is present, so Squirrel auto-updaters (Discord, GitHub Desktop, Slack) stay silent across version bumps; a same-identity match with a different signing publisher fires `HashChanged` with a publisher-mismatch summary — beats GlassWire/SimpleWall on this class. Cold-start races in both `FirewallTabViewModel.ActivateAsync` and `AlertsTabViewModel.ActivateAsync` were resolved by replacing `bool _activated` idempotency with shared `Task? _activationTask` so concurrent callers (tab-switch fire-and-forget + deep-link awaited) await the same underlying load. The hostname-resolution ladder retains its four layers (preload, ETW DNS-Client, reverse-DNS PTR, SNI extraction) per ADRs 004/005/006. Phase 8 wired the Traffic tab's previously-disabled MAP sub-view: a custom-Canvas world heatmap (`WorldMapControl`) rendering Natural Earth 110m country polygons (CC0 public domain, ~170 KB embedded asset) under an equirectangular projection, fills each country from a 5-stop `HeatmapPalette` ramp normalized to the per-fetch max, and overlays a hover tooltip with the country name + bytes in/out. **Phase 8 polish** extended the tooltip with the country's top-3 destinations (by total bytes) via the existing `GetProcessDestinations` RPC, extended with optional `country` filter + `limit` cap so the per-country fetch hits the (country, bucket_start_ms) index directly — hover any country → see "github.com 8.2 MB · amazon.de 3.1 MB · microsoft.com 2.9 MB" without leaving the map. Five tooltip states (No-fetch-yet / Loading / Empty / Populated / Failed) are visually distinct per UI_QUALITY_STANDARDS §3.1; the Failed state silently degrades to a "destinations unavailable" caption rather than raising an ErrorBanner because the country name + total bytes header is still useful. No LiveCharts2 dependency — surveyed the Avalonia 12 ecosystem (LiveCharts2 / Mapsui / XAML.MapControl / Codenizer / Asv) and confirmed every option either lacks Avalonia 12 support or requires external map-tile servers (incompatible with the "no outbound network" stance); custom Canvas mirroring `TrafficChartControl` is the path. The gRPC IPC surface still has eighteen RPCs (Phase 8 is purely UI — the existing `GetCountryBreakdown` RPC from Phase 4.6a is the data source; Phase 9.1 ships no RPCs either — that surface lands in 9.3). **Phase 9.1** scopes the Scanner via [ADR 009](decisions/009-scanner-as-lan-device-discovery.md) (Scanner = LAN device discovery; port scanning / CVE lookup / anomaly detection rejected) and ships the foundation: a `lan_device` SQLite table keyed on MAC (identity per ADR 009; same "prefer stable observable identifier" principle as Phase 7.5's logical app identity), `SqliteLanDeviceStore` implementing the four-method `ILanDeviceStore` interface (GetByMac, GetByIp, List, Upsert), `OuiVendorLookup` loading an embedded `data/oui.csv` snapshot (~39k IEEE OUI prefixes) into an in-memory dictionary at startup with graceful degradation if the file is missing (matches `NullGeoIpResolver` posture), and a `Beholder.Tools.OuiFetcher` console tool that mirrors `Beholder.Tools.GeoIpFetcher` for refreshing the snapshot. **Phase 9.2** ships the scanner that uses 9.1's foundation: `ILanDeviceProbe` interface in `Beholder.Core` (one-shot batched scan, not the `IFlowSource` continuous-stream shape — scanning is bursty), `WindowsLanDeviceProbe` orchestrator + `ArpScanProbe` sub-probe + `IphlpapiInterop` P/Invoke layer for `iphlpapi.dll` `SendARP` (mirrors ADR 004's `DnsApiInterop` `LibraryImport` pattern; documented Win32 export so no `NativeLibrary.TryGetExport` probing needed), cross-platform `LanScannerService` hosted-service driving a `PeriodicTimer(TimeSpan, TimeProvider)` (the .NET 8+ ctor — enables deterministic test-time advance via `FakeTimeProvider.Advance`), and the two new `EventKind` values (`LanDeviceFirstSeen=9`, `LanDeviceMacChanged=10`) with deterministic JSON payload encoders mirroring `AlertPayloadEncoder` / `FirewallRulePayloadEncoder`. ARP-only for 9.2 by explicit scope decision: mDNS via `DnsServiceBrowse` (callback-based async) and NetBIOS via Win32 are deferred to 9.2.5 to keep blast-radius small for the first scanner commit — vendor names from the OUI lookup are already informative on their own. State-transition logic in `LanScannerService.ProcessObservationAsync`: new MAC = `LanDeviceFirstSeen` event; new MAC at a known IP = `LanDeviceMacChanged` event (potential ARP-spoof signal or, more commonly, DHCP reassignment); known MAC = silent upsert. No new alert kinds — LAN events go to `event_log` for audit but never to the Alerts tab per ADR 002 + ADR 009. Per-observation error boundaries: chain-write failure still upserts the store row; per-observation processing failure still processes the rest of the batch; probe-level failure logs and retries on the next tick. Scanner registered inside `#if PLATFORM_WINDOWS` block alongside its dependencies — Linux daemon will get the scheduler outside the `#if` (with a null probe → log warning + skip) once the Linux daemon stabilizes; that hoist is mechanical when the time comes. **Phase 9.2.5** (current checkpoint) completes ADR 009's three-layer hostname-resolution ladder by adding mDNS + NetBIOS probes that run per-IP after the ARP discovery merge. Both implemented as pure managed C# via `System.Net.Sockets.UdpClient` — no new P/Invoke surface, unlike Phases 9.1/9.2/9.2.1's `iphlpapi.dll` work. `Beholder.Core/Discovery/MdnsPacketBuilder` + `MdnsPacketParser` build/parse DNS-format mDNS reverse-IP PTR queries with the QU bit set per RFC 6762 §5.4 (so responders unicast replies to the source ephemeral port); `Beholder.Core/Discovery/NetbiosPacketBuilder` + `NetbiosPacketParser` build/parse RFC 1002 NBSTAT queries with the bizarre NetBIOS first-level name encoding (each byte → two `A..P` letters). All four packet classes mirror `Beholder.Core/Tls/TlsClientHelloParser` from ADR 006: `static bool TryExtractX(ReadOnlySpan<byte>, out string?)` with exhaustive bounds-checks and `false`-on-malformed-no-exception. `HostnameResolutionLadder` (Windows scanner) orchestrates the probes via `Parallel.ForEachAsync(MaxDegreeOfParallelism=32)` over each scan's IPs, trying each probe in priority order (mDNS first; NetBIOS fallback) until one returns non-null. 60s per-ladder deadline mirrors `ArpScanProbe`. `ScannerOptions.EnableHostnameResolution = true` (default-on opt-out) is the kill-switch matching ADR 005's `EnableReverseDnsFallback` precedent. mDNS multicast is TTL=1 (link-local); NetBIOS unicast targets subnet-local IPs — neither leaves the LAN. **Phase 9.2.1** fixes a performance defect surfaced by 9.2's smoke test: the sequential `SendARP` sweep took ~4 minutes wall-clock on a typical /24 because Windows holds `SendARP` for ~1 s per unresponsive IP, and a /24 has ~224 unresponsive hosts on a home LAN. The fix is a two-pass scan: `IphlpapiInterop.TryEnumerateIpv4ArpCache` (new — wraps `GetIpNetTable2` / `FreeMibTable` per the `DnsApiInterop` precedent from ADR 004) reads Windows' existing ARP / neighbor cache in microseconds with zero packets sent, catching most devices instantly on a typical LAN where everything talks to the gateway periodically; `ArpScanProbe.ProbeIpsAsync` (replaces the old single-threaded `ScanSubnetAsync`) issues `SendARP` for cache misses via `Parallel.ForEachAsync(MaxDegreeOfParallelism=64)` plus a 60 s per-scan deadline that returns partial results on expiry. Wall-clock on a /24 drops from ~4 minutes to ~5 s steady-state, ~30 s cold-cache. Active probing per ADR 009 is preserved — the cache walk is a passive read of state Windows already maintains, not a substitute for the active probe. **Phase 9.2.6** (current checkpoint) responds to a 9.2.5 smoke-test finding: per-IP reverse-PTR + NetBIOS lit up **zero** hostnames on the user's real LAN despite 996 unit tests passing. Manual PowerShell mDNS PTR query against the same LAN confirmed 0 replies — the protocol was working, the LAN's responders just don't answer reverse-IP PTR queries. Diagnosis: most Bonjour-style responders (Apple TVs, AirPlay speakers, Chromecasts, network printers, NAS, IoT bridges) advertise *services* via DNS-SD (RFC 6763) using PTR records keyed on `_<service>._<proto>.local`, and ignore reverse-IP PTR queries entirely. The 9.2.6 fix adds the SD browse pattern real-world tools (Fing, GlassWire Things tab, `dns-sd -B`, `avahi-browse`) use: `MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery` builds a PTR query for a service-type name with the QU bit set, `MdnsServiceDiscoveryParser.TryExtractHostname` walks PTR + SRV + A records across answers + authority + additional sections, correlates them, and extracts the device's hostname via priority SRV-target → A-record-owner → PTR-instance-leftmost-label (with `.local` strip). `MdnsServiceDiscoveryProbe.BrowseAsync` sends 12 well-known service-type queries from one ephemeral `UdpClient` and collects replies over a 3-second window. `WindowsLanDeviceProbe.ScanAsync` runs SD-browse first, then falls through to the 9.2.5 per-IP ladder only for IPs without an SD hostname. Same kill-switch: `EnableHostnameResolution` gates both passes uniformly. DNS name compression / pointer-following logic that was duplicated between `MdnsPacketParser` (9.2.5) and the new SD parser was extracted to a shared `DnsNameDecoder` helper per DRY — the existing 9.2.5 parser was refactored to use it, all pre-existing tests continue to pass. **1029 tests pass deterministically** (was 996; +33 new SD builder + SD parser tests across two new files). **Phase 9.3** (current checkpoint) opens the daemon-side LAN scanner to the UI: two new RPCs on `BeholderLocal` (`ListLanDevices` for paged historical reads, `TriggerScan` for on-demand scans returning a structured `success=bool, message, devices_observed` shape) plus two new `DaemonEvent` oneof variants (`LanDeviceFirstSeenEvent` / `LanDeviceMacChangedEvent`) so subscribed UIs receive scanner events in real-time. The RPC surface grows 15 → 17. The proto's `LanDevice` message is shared across `ListLanDevicesResponse` and both stream events for DRY. `BeholderLocalService` gains `ILanDeviceStore` + `LanScannerService` ctor deps; `LanScannerService` gains `BroadcastService` and now fires `BroadcastLanDeviceFirstSeen` / `BroadcastLanDeviceMacChanged` after each successful chain write — closing an implicit gap from Phase 9.2 where the scanner wrote the chain but skipped the broadcast leg every other event kind already used. `LanScannerService` also exposes a public `RunOnceManuallyAsync(CancellationToken)` entry point for `TriggerScan` to call directly; a `SemaphoreSlim _scanGate` serialises timer-loop scans and manual triggers so they never overlap. UI side: `DaemonClient` + `IDaemonClient` get `ListLanDevicesAsync` / `TriggerScanAsync` wrappers; `DaemonStreamSubscriber` adds `LanDeviceFirstSeenReceived` + `LanDeviceMacChangedReceived` C# events and two new cases in its `PayloadOneofCase` dispatch switch. The Scanner tab UI is deliberately untouched — it remains a stub for Phase 9.4 to fill in against this stable IPC surface. **1060 tests pass deterministically** (was 1029; +31 new tests across `ListLanDevicesRpcTests`, `TriggerScanRpcTests`, extensions to `LanScannerServiceTests`, `BroadcastServiceTests`, `DaemonStreamSubscriberTests`, and `ProtocolConvertersTests`). Note: the previous status text claimed "eighteen RPCs" but the empirical count in `beholder_local.proto` was 15; 9.3's two additions land us at 17. Next up: Phase 9.4 (Scanner tab UI replacing the stub), then 9.5 (Traffic-tab cross-link), 9.6 (verification). Settings stays at **Phase 13** as the dedicated final UI phase.

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

### Phase 5.4.4 — Hostname capture quality (cross-cutting) ✅

**Purpose:** Bring per-flow hostname coverage to "every connection that could possibly be named, is named." Real-world testing of the post-5.4.3 daemon surfaced four overlapping gaps in the hostname-resolution path that left raw IPs in the COLS view's HOSTS column for connections users had no other way to identify: cold-start blindness (DNS queries that happened before the daemon launched), direct-IP traffic (apps that bypass DNS entirely — torrent peers, hardcoded IPs), one-off flows that ended before any name could be learned, and long-lived TCP connections whose original DNS lookup had aged out of every cache while the socket itself stayed alive (HTTP/2 keep-alive, WebSocket reuse). Closed all four with a layered resolution ladder.

**Decision records added:** ADR 004 (DNS preload), ADR 005 (reverse-DNS fallback), ADR 006 (SNI capture). ADR 004 was originally written earlier but its `DnsGetCacheDataTableEx` resolution and probe matrix landed during this phase.

**Key components:**

- **Layer 1: DNS resolver-cache preload (`Beholder.Daemon.Windows/DnsApiInterop.cs`).** P/Invoke into the undocumented-but-stable `dnsapi.dll` exports. Tries `DnsGetCacheDataTableEx` first (verified prototype `(uint flags, out IntPtr ppCacheTable)` per the Phase-0 probe of ADR 004); falls back to legacy `DnsGetCacheDataTable` for older Win11 builds. On Win11 22H2+ where the legacy export returns `ERROR_INVALID_FUNCTION`, the Ex export populates the cache (469 entries on the test machine, all hostnames matching the active browsing session). `EnablePreload` kill-switch on `DnsOptions`.
- **Layer 2: Live ETW capture (`EtwDnsCache`).** Already shipped pre-5.4.4 but evolved during this phase: added the `IDnsCacheIngest` interface so other resolution sources can write to the in-memory cache without depending on the concrete type, and made `IngestResolved` public to satisfy the new interface.
- **Layer 3: Reverse-DNS fallback (`Beholder.Daemon.Windows/ReverseDnsFallbackCache.cs` + `Beholder.Core/IReverseDnsResolver.cs` + `Beholder.Daemon.Windows/SystemReverseDnsResolver.cs`).** Decorator over `IDnsCache`. Inner-cache miss → enqueues to a 500-capacity bounded `Channel<IPAddress>`; background worker calls `Dns.GetHostEntryAsync` with a 3 s per-query timeout, ingests successes via `IDnsCacheIngest`, records failures in a 30-min negative-cache to prevent retry storms. Skips private/reserved IPs (`IPAddressExtensions.IsPrivateOrReserved`). `EnableReverseDnsFallback` kill-switch on `DnsOptions`.
- **Layer 3b: SQLite hostname backfill (`Beholder.Core/IDnsHostnameBackfill.cs` + `SqliteTrafficStore.BackfillHostnameAsync`).** When reverse DNS or SNI capture learns a name, retroactively `UPDATE` all five rollup-tier tables (`traffic_raw`, `traffic_buckets_10s..1h`) with `WHERE remote_address = ? AND hostname IS NULL`. Single transaction across tiers. Handles the one-off-flow case where a connection ended before any flush tick could record the resolved hostname.
- **Layer 4: SNI capture (`Beholder.Daemon.Windows/PktmonSniSource.cs` + `Beholder.Core/Tls/TlsClientHelloParser.cs` + `Beholder.Daemon.Windows/SniOptions.cs`).** `Microsoft-Windows-PktMon` ETW provider subscription. Adaptive Ethernet-frame finder scans for the IPv4 ethertype signature (Pktmon's per-event metadata is variable-length across event subtypes — fixed-offset parsing fails). `TlsClientHelloParser` is a defensive zero-allocation TLS record + ClientHello + extension walker that handles TLS 1.2/1.3, returns `false` on truncated/malformed/ECH cases. `EnableSniCapture` kill-switch on `SniOptions`.
- **UI fix (`Beholder.Ui/Views/Tabs/TrafficColsView.axaml`).** The COLS view's three columns (Hosts/Traffic Type/Countries) had no `ScrollViewer` wrapping their `ItemsControl` panels, so overflowing rows were silently clipped. Wrapped each in `<ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">`. Headers stay sticky outside the `ScrollViewer`. Avalonia's `VirtualizingStackPanel` virtualizes against the new bounded viewport, so virtualization keeps working.

**Tests added:**
- `EtwDnsCachePreloadTests.cs` — `IngestResolved` direct-write seam, last-writer-wins semantics, IPv4/IPv6 input.
- `ReverseDnsFallbackCacheTests.cs` (9 tests) — inner-hit passthrough, kill-switch passthrough, private-IP exclusion, public-miss enqueue + ingest, in-flight coalescing, negative cooldown + cooldown-expired re-enqueue, stop-with-pending-query, null-arg guard. Plus 2 tests for backfill-after-ingest and backfill-throws-doesnt-crash-worker.
- `SqliteTrafficStoreTests.cs` — 3 tests for `BackfillHostnameAsync`: null-rows updated, already-resolved-rows preserved, all-five-tier-tables touched.
- `TlsClientHelloParserTests.cs` (13 tests) — TLS 1.2 + 1.3 happy paths, ECH (returns false), no-SNI ClientHello, non-handshake records, non-ClientHello, truncated record, truncated extensions, random bytes, empty hostname, control bytes, hostnames with digits/hyphens/triple-dash.
- New test doubles: `FakeReverseDnsResolver` (Channel-based with SemaphoreSlim signaling — original `ConcurrentQueue` + per-IP TCS pattern had a producer-consumer race that hung the full test class), `FakeDnsHostnameBackfill` (records calls + supports `ThrowOnNextCall` for failure-path coverage), `FakeDnsCache` extended to also implement `IDnsCacheIngest`.
- Net test count: 526 → 611 (+85). 15 sequential reruns of each new test class confirm no remaining flakes.

**Design decisions:**
- **Decorator over interface extension for reverse DNS.** `IDnsCache.Resolve` stays synchronous and non-blocking; the decorator's `Resolve` does fast-path inner-cache lookup, falls back to enqueueing for the worker, and returns `null` immediately. The first flush tick after a new direct-IP destination shows raw; the next tick shows the resolved name. Async at the `IDnsCache` boundary would have rippled through `TrafficEngine`'s flush hot-path.
- **`IDnsCacheIngest` and `IDnsHostnameBackfill` as separate small interfaces.** Each new resolution source (reverse DNS, SNI) needs to push a (hostname, IP) pair into the in-memory cache *and* retroactively backfill SQLite. Two small interfaces beat one fat one — a future Linux SNI source can implement both without taking on the `IDnsCache` read responsibility.
- **Probe-first for both ADRs 004 and 006.** `DnsCacheProbe` (committed at `3a05477`, removed at `8ef32e3`) tested 7 candidate signatures of the undocumented Ex export in separate processes so a wrong-shape AV in one couldn't take down the others. `PktmonProbe` (committed during 5.4.4, removed at the matching revert commit) ran for 30 s while a script drove fresh HTTPS handshakes; extracted SNI from 44/44 ClientHellos before we wrote a line of production wiring. Both probes follow `PRINCIPLES.md §No Dead Weight` — the value was the empirical evidence; the code itself is preserved in git history but absent from `master`.
- **Adaptive Ethernet-frame finder for SNI.** Phase 0's first probe sample showed packets at event-data offset 32, but production runs revealed Pktmon's metadata is *variable-length* across event subtypes (additional 2-byte prefixes appear for some component/edge combinations). A fixed-offset parser silently dropped every TCP/443 packet despite the SNI bytes being present. Replacing the fixed offset with a 48-byte scan for the IPv4 ethertype signature fixed the smoke test from `tcp443=0, sni=0` to `tcp443=356, sni=20` (5 driven URLs × ~4 NDIS-layer appearances = 20 SNIs, exact match).
- **No third-party drivers.** WinDivert, Npcap, and custom WFP-callout kernel drivers were considered for the SNI capture and explicitly ruled out by the user. Pktmon is Windows' built-in tool, ships with the OS, requires no install. If a future Windows update breaks Pktmon's ETW emission, SNI capture is deferred — *not* attempted via fallback.
- **Race fix in test fake (`FakeReverseDnsResolver`) discovered via flake hunting.** The original implementation used a `ConcurrentQueue<string?>` for answers and a per-IP `TaskCompletionSource` for waiting; a producer signal between the consumer's empty-check and waiter creation could leave the consumer parked on a TCS no producer would ever signal. Tests passed individually (no concurrent producers) but hung when the full class ran (more interleaving = race wins eventually). Fixed by replacing the queue with `Channel<T>` and the call-entry signal with `SemaphoreSlim` — both are race-safe regardless of producer/consumer ordering.

**Files NOT touched:** Proto schemas (no RPC changes), `IDnsCache` interface (still single-method `Resolve`), `EtwFlowSource`, the rollup cascade, any UI view beyond the COLS scroll fix.

---

### Phase 6.4 — Firewall tab (rule table + three-state pills + master toggle) ✅

**Purpose:** Make the Firewall tab the working active-queue head: a rule table that surfaces every persisted Beholder firewall rule alongside every process Beholder has ever observed, with per-direction ALLOW/BLOCK/DEFAULT pills that mutate the OS firewall state on click and a master ON/OFF toggle that disables every Beholder-managed rule without losing the configuration. GlassWire-inspired layout (Active apps on top, Inactive collapsed below) but with Beholder's terminal aesthetic and the project's strict UI quality bar.

**Daemon prerequisites (committed first):**
- **`RemoveFirewallRule` RPC** (`BeholderLocalService.cs`, proto). Mirror of `ApplyFirewallRule`'s OS-then-persist-then-chain shape, but inverted on rollback semantics: if SQLite delete fails after the OS removal succeeded, the daemon logs a warning rather than re-applying — the user's intent was "remove" and the OS state already matches that intent.
- **`ListFirewallRules` RPC.** Thin pass-through over `IFirewallRuleStore.ListAllAsync`, routed through `ExecuteQueryAsync` for unified error classification.
- **`SetFirewallEnabled` RPC + `IFirewallEnforcementState` mutable wrapper.** Runtime-toggleable singleton holding the master flag plus an `Action<bool>` event. Separated from `FirewallOptions.EnableEnforcement` (which is now only the *startup default* read at daemon launch) because mutating `IConfiguration` from inside an RPC would require rewriting `appsettings.json` on every toggle. Only chain-audits real transitions to keep the activity strip clean — re-asserting the current state is a no-op.
- **`FirewallEnforcementService`** (`Beholder.Daemon/Pipeline/`). `IHostedService` that subscribes to `IFirewallEnforcementState.StateChanged` and replays every persisted Beholder rule through `IFirewallController` on every transition: toggle off calls `RemoveRuleAsync` for each rule (SQLite copies preserved), toggle on calls `AddRuleAsync`. Per-rule failures are logged and swallowed so one broken rule can't abort enforcement for the rest. The state-changed callback fire-and-forgets the replay so the RPC returns promptly.
- **`FirewallEnforcementTogglePayloadEncoder`.** Deterministic JSON encoder for the new `EventKind.FirewallEnforcementToggled = 8` chain entry, same byte-stable contract as `FirewallRulePayloadEncoder`.
- **`firewall_enforcement_enabled` field on `GetSnapshotResponse`.** UI reads the master state on activation without a second round-trip.

**UI components:**
- **`FirewallTabViewModel`** (`Beholder.Ui/ViewModels/`). On activation fans out three RPCs in parallel (`ListFirewallRules` + `GetSnapshot` + `GetProcessSummaries(from=epoch, to=now)`) and joins the results into a single row dictionary keyed by `processPath`. Subscribes to `ProcessStateService.ProcessStatesUpdated` for the IsActive flag + recent-bytes column, and to `DaemonStreamSubscriber.RuleChangeReceived` for live upserts/removes from broadcast events. Search filters by name OR path case-insensitive. Filter dropdown: ALL / ACTIVE / INACTIVE / BLOCKED / PARTIAL. Three commands — `CycleInPill`, `CycleOutPill`, `ToggleEnforcement` — each with optimistic UI and revert-on-error.
- **`FirewallRuleRow`** (`Beholder.Ui/ViewModels/`). Observable row VM. Derives `OverallStatus` from the (InAction, OutAction) pair — `Allowed` / `Blocked` / `Partial` / `Default` — and notifies on changes so header counts update without manual refresh. `NextState` helper encapsulates the `Allow → Block → Default → Allow` cycle.
- **`FirewallActionPill`** (`Beholder.Ui/Controls/`). Three-state pill UserControl. `State` styled property accepts a boxed enum or int; class-based styling switches on that state (`allow` = green, `block` = red, `default` = muted). Click invokes `Command` with `CommandParameter`. Public surface uses `object` to keep the control independent of the internal enum.
- **`FirewallTabView.axaml`.** Header bar (counts + search + filter dropdown + master ON/OFF toggle replacing the new-rule slot), enforcement-disabled banner, error banner, collapsible group headers, virtualized rule rows. The master toggle uses class-based styling (`active` = teal accent, `danger` = red); pill rows render with reduced opacity in the Inactive group to communicate the soft-disabled state.

**Click semantics for the three-state pill:**
- Current state `Default` → click → `ApplyFirewallRule(action=Allow)` → state becomes `Allow`.
- Current state `Allow` → click → `ApplyFirewallRule(action=Block)` → state becomes `Block`.
- Current state `Block` → click → `RemoveFirewallRule` → state becomes `Default`.

Optimistic UI: visual state flips on click, daemon dispatch happens in the background, RPC failure reverts the local state and surfaces a transient banner.

**Master toggle behaviour:** Toggling OFF removes every persisted Beholder rule from the OS firewall via the enforcement service; SQLite copies are preserved. Toggling ON re-applies them. A banner appears below the header when enforcement is OFF: *"Firewall enforcement is disabled. Rule changes are saved but not applied to Windows Firewall."*

**Tests added:**
- `RemoveFirewallRuleRpcTests.cs` (7 tests) — happy path, idempotent re-remove, validation error, OS-failure abort, double-remove second-call returns `removed=false`, broadcast emits Removed event.
- `ListFirewallRulesRpcTests.cs` (4 tests) — empty/populated/preserves direction+source, ID ordering.
- `SetFirewallEnabledRpcTests.cs` (9 tests) — toggle off/on, no-op same-state, chain-audit on real transitions, no chain-audit on no-op, two toggles append two rows, StateChanged fires correctly, snapshot reflects state both ways.
- `FirewallEnforcementServiceTests.cs` (7 tests) — toggle off removes all rules, toggle on adds all, empty store, preserves SQLite on toggle off, swallows per-rule failures, Start subscribes/Stop unsubscribes.
- `FirewallTabViewModelTests.cs` (16 tests) — empty/populated activation, IN+OUT block → Blocked status, partial block → Partial, snapshot drives master toggle state, idempotent activation, all three pill cycles call correct RPC, RPC failure reverts state, search filters by name, Blocked filter excludes Partial, RPC-error sets error state.
- `FirewallRuleRowTests.cs` (12 tests) — name extraction, null-path guard, all four OverallStatus permutations, mixed-direction theory cases, NextState cycle, RecentBytesLabel formatting, property-change notifications.
- Net test count: 611 → 689 (+78 across both Phase 6.4 and 6.5; ~62 of those landed with 6.4).

**Design decisions:**
- **`IFirewallEnforcementState` separate from `FirewallOptions`.** The first sketch tried to mutate `IOptionsMonitor<FirewallOptions>` from inside the RPC handler; that requires either rewriting `appsettings.json` on every click (unacceptable), wrapping `IOptionsMonitor` with a custom `OnChange`-firing adapter (invasive), or maintaining a parallel mutable bool. Picked the parallel mutable bool — `FirewallOptions.EnableEnforcement` becomes the *startup default* read once at daemon launch, and `IFirewallEnforcementState` is the runtime source of truth from that point forward. The two never need to disagree because no other code mutates the options.
- **Fire-and-forget enforcement replay.** `IFirewallEnforcementState.StateChanged` fires synchronously inside the RPC handler, but `FirewallEnforcementService.OnStateChanged` spawns a `Task.Run` rather than awaiting the replay. Reasoning: a 50-rule replay against `INetFwPolicy2` takes seconds; the user's master-toggle click should not block on it. The controller's own `SemaphoreSlim` serializes against any concurrent `ApplyFirewallRule` so the replay can't corrupt OS state.
- **`object`-typed `State` property on `FirewallActionPill`.** The internal enum stays internal; the public UserControl accepts a boxed value. Avalonia binding doesn't auto-coerce `int` to a specific enum without an explicit converter, but it *does* round-trip a boxed enum through `object` — so the binding from `row.InAction` to `pill.State` works without a converter file. The `UpdateVisuals` method handles the unboxing with a switch expression that defaults to `Default` for any unrecognized value.
- **Three-state pill cycle, not two-state plus right-click.** Earlier draft of the UI had a binary ALLOW/BLOCK pill plus a right-click menu for "Remove rule." Stripped on user feedback: the discoverability cost of a hidden right-click action outweighs the small extra cognitive load of a third state. The cycle direction (ALLOW → BLOCK → DEFAULT) was chosen so the most-likely-next state for an existing rule is one click away.

**Files NOT touched:** No changes to `IFirewallController` or `IFirewallRuleStore` — both interfaces already had the CRUD shape needed. No changes to the rollup cascade, the GeoIP layer, or the chain-hash primitives.

---

### Phase 6.5 — Recent firewall activity strip ✅

**Purpose:** Surface a chronological audit log of firewall-related chain entries below the rule table, so the user can see exactly when each rule was created/changed/removed and when the master toggle flipped — without leaving the Firewall tab.

**Daemon side:**
- **`GetFirewallActivity` RPC + `IEventStore.ListByKindsAsync`.** New chain-query method on `IEventStore` with the shape `(IReadOnlyCollection<EventKind> kinds, int limit, CancellationToken) → IReadOnlyList<EventLogEntry>`. SQLite implementation builds a parameterized `IN (…)` clause sized to the kinds list, orders newest-first by `seq`, clamps the limit at a server-side cap of 500. The handler pulls firewall-related kinds (`FirewallRuleCreated`, `FirewallRuleChanged`, `FirewallRuleRemoved`, `FirewallEnforcementToggled`), decodes each payload via the existing encoders, and packs into the new `FirewallActivityEvent` proto message. Bad payloads degrade gracefully — the kind + timestamp + seq still surface so the UI can render the row.
- **`EventLogEntry` record** in `Beholder.Core`. Mirrors only the columns the activity strip needs (seq, kind, timestamp, payload bytes); chain hashes are intentionally omitted because chain integrity has its own RPC (`VerifyChain`).

**UI side:**
- **`FirewallActivityViewModel`** (`Beholder.Ui/ViewModels/`). Owned by `FirewallTabViewModel` so its lifecycle matches the parent. Initial fetch limit is 100; live-cap at 500 retained events with oldest-first eviction. Lifecycle: on activation, fetches the most-recent 100 firewall events from the daemon; subscribes to `RuleChangeReceived` and prepends new events with synthetic negative seq numbers (the broadcast carries the rule, not the chain entry — the next tab re-activation reloads from the daemon and replaces synthetic rows with their real chain entries). Uses a `HashSet<long>` of seen seqs for deduplication.
- **`FirewallActivityRow`** record-style VM. Pre-formats every column at construction so the view template stays shallow: timestamp label (`HH:mm:ss`), kind label (`RULE` / `ENFORCE`), kind badge class (`info` / `danger` / `muted`), and a one-line description.
- **`FirewallActivityStrip.axaml`** (`Beholder.Ui/Views/Tabs/`). UserControl docked at the bottom of `FirewallTabView`. Sticky `RECENT FIREWALL ACTIVITY` header with live count, scrollable virtualized event list (max 240px so it never dominates the table), color-coded kind badges. Empty/loading/error states all wired.
- **`StringEqualsConverter`** (`Beholder.Ui/Converters/`). Tiny one-way value converter for the badge-class binding — applies a CSS-style class only when the row's `KindBadgeClass` matches the bound class name.

**Activity row formats (matching the mockup):**

| Time | Kind | Description |
|---|---|---|
| `18:27:44` | `RULE` (info) | `created · firefox.exe · out block` |
| `18:25:00` | `RULE` (muted) | `removed · curl.exe · out` |
| `18:24:50` | `ENFORCE` (danger) | `firewall enforcement: OFF` |

Out-of-scope for v1 (per the plan): per-packet enforcement events (a packet was actually dropped — needs WFP callout or `Microsoft-Windows-Security-Auditing` ETW), and `NEW process` events (Phase 7's `NewProcessDetector` will populate them automatically once that phase lands).

**Tests added:**
- `GetFirewallActivityRpcTests.cs` (9 tests) — empty chain, apply+remove returns both newest-first, decodes process_path/direction/action, decodes EnforcementToggled bool, limit clamped at server cap, zero limit uses default, negative limit returns InvalidArgument, only firewall kinds returned (counter events filtered), limit trims correctly.
- `FirewallActivityViewModelTests.cs` (8 tests) — empty response shows empty state, populated adds rows, RPC failure sets error, idempotent activation, FromProto decodes RuleCreated/RuleRemoved/EnforcementToggled correctly with right badge classes.

**Design decisions:**
- **Synthetic negative seq for live-broadcast rows.** The daemon's `Subscribe` stream emits `FirewallRuleChange` (the rule) but not the chain seq — we'd need to widen the broadcast contract to carry the seq, or do a follow-up `GetFirewallActivity(limit=1)` on every broadcast. Both are heavier than this pragmatic solution: stamp the broadcast row with `-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` so it's guaranteed not to collide with any positive chain seq, then let the next tab re-activation reload from the daemon and replace synthetic rows with the real ones. The window of synthetic-only rows is bounded by tab-switch cadence.
- **500-event in-memory cap.** Activity-strip data is *display-only*; the chain itself is the durable audit log and is uncapped. 500 events is enough to cover several days of an actively-managed rule set without exhausting either memory or scrolling patience.
- **Column shapes pre-formatted in the row VM, not in the template.** Per `UI_QUALITY_STANDARDS`: data-binding to a long expression chain in a `DataTemplate` is hard to test and harder to debug. `FirewallActivityRow.FromProto` is a pure function with eight unit tests covering the shape of each event kind.

**Files NOT touched:** No changes to `IFirewallController`, no changes to the broadcast contract, no changes to the rollup or GeoIP paths.

---

### Phase 6.4/6.5 polish addendum (`70ddc79`, `9841306`, `89fc67a`, `909027c`, `5103174`, `81e810b`, `ffcbcdd`, `34c17ab`, `3eeaf87`, `974a833`, `349a960`) ✅

Eleven follow-up commits accumulated against the Firewall tab between the base 6.4 ship and the start of 6.6. Each is small but worth a per-line entry because they collectively define the tab's polish bar:

- **Pill semantics: effective connectivity, not rule state** (`70ddc79`). ALLOW shows when Beholder has no Block rule AND the OS firewall isn't blocking; BLOCK shows when either side blocks. Stops the user from reading "ALLOW" while their app is silently being blocked by a stale Windows Firewall rule.
- **Polish pass** (`9841306`): inactive group collapsed by default, `SOURCE` and `HOSTS` placeholders for not-yet-loaded rows, zero-byte counts muted to `TextMuted`.
- **Orphaned-rule warning glyph + uninstalled-app filter** (`89fc67a`). Rules with no matching binary on disk (uninstalled apps) get a `⚠` glyph in `SeverityWarn` next to the path; uninstalled apps WITHOUT a rule are filtered out entirely (they're noise — the app is gone and no rule references them). `IsOrphanedRule = HasRule && !ExecutableExists`.
- **URL-safe rule name encoding** (`909027c`) + **HResult-based COMException catch** (`5103174`). Two atomic fixes for `0x80070002 ELEMENT_NOT_FOUND` from `INetFwRules.Add` — the old base64 encoding produced rule names with `/` characters that the firewall service rejected on `Item` lookup; the catch was using `COMException` type rather than the HResult so the not-found case wasn't being recognized as the expected branch.
- **Enforcement-OFF bypass in Apply/Remove RPCs** (`81e810b`). When the master toggle is OFF, Apply/Remove still persist + chain-log + broadcast, but skip the OS-firewall call entirely. Previously the OS call would succeed against an empty rule set, producing nothing visible but adding latency.
- **Double-tap to copy parent directory** (`ffcbcdd`). Quality-of-life affordance that caught the "where on disk does this live?" use case without requiring a separate detail pane.
- **Source labels UPPERCASE; MANUAL/REMOTE in TextSecondary, DEFAULT muted** (`34c17ab`). Density tweak so manually-managed rules visually stand out from the default-allow majority.
- **Sticky column header + widened pill columns** (`3eeaf87`). The header stayed sticky as the rule list scrolled; pill columns widened so ALLOW/BLOCK labels stopped clipping at compact widths. Plus rule-source default literal `"default"` instead of empty string so the column never reads blank.
- **Unknown-PID filter on rule table** (`974a833`). Same filter that the Traffic tab applies — rows whose process resolution failed (`pid:1234` style fallback names) are dropped from the rule list. ETW transient-process noise.
- **Reclassify diff-and-mutate** (`349a960`). 1Hz tick was rebuilding `ActiveRows` and `InactiveRows` via `Clear+Add`, which Avalonia's `ItemsControl` recycles on `Reset` — pill containers were being re-templated mid-click, swallowing the click. New `Reclassify` uses single-step `Insert/Remove/Move` ops so stable rows' container instances stay mounted across ticks. Hover-during-tick scenarios now survive.

These eleven commits add ~78 of the ~234 tests gained this checkpoint.

---

### Phase 6.6 — Alerts master-detail (read/unread + auto-select) ✅

**Purpose:** Build the Alerts tab as a master-detail surface that surfaces every alert from the chain (`event_log` rows where `kind ∈ {NewProcess, HashChanged, ChainError}`). Master list on the left; detail pane on the right. Read state managed via `MarkAlertRead` RPC with optimistic UI and revert-on-failure.

**Key components:**

- **`AlertsTabViewModel`** — fans out one RPC on activation (`GetSnapshotAsync` for the alert list + outbound-block cache), subscribes to `DaemonStreamSubscriber.AlertReceived` for live updates and `RuleChangeReceived` for outbound-block state. Auto-selects the newest alert on first paint. `OnSelectedAlertChanged` fires the `MarkAlertRead` RPC opportunistically and reverts `IsRead` on failure.
- **`AlertRow`** — observable row VM. Keeps the wire `Alert` proto fields immutable; only `IsRead`, `IsOutboundBlocked`, and `IsExecutableMissing` are observable.
- **`AlertsTabView.axaml`** — master-detail layout per `UI_DESIGN.md` §5.5. Severity rail on each row (color-coded by `KindBadgeClass`), kind badge, timestamp, summary. Detail pane sections: PROCESS / REMOTE / CONNECTION / ACTIONS.
- Composition root wires `AlertsTabViewModel` with the `_navigateToFirewallRule` delegate (still `Action<string>` here; made async in 6.7-polish).

**Tests added:** ~30 in `AlertsTabViewModelTests.cs` — activation populates list + auto-selects newest, mark-read optimistic + revert, live alert prepends to top, dedupe by seq, snapshot RPC failure surfaces error state, idempotent activation.

**Design decisions:**
- **Optimistic mark-read.** `OnSelectedAlertChanged` flips `IsRead = true` immediately and fires the `MarkAlertRead` RPC in the background. RPC failure reverts. The user sees instant feedback even on slow IPC.
- **Snapshot's `recent_alerts` is the seed.** Avoids a separate `ListAlerts` RPC; the `GetSnapshot` shape was already returning the recent-alerts slice.
- **Reflection-based event raise in tests.** `DaemonStreamSubscriber.AlertReceived` is a public event with `+=`/`-=` only; tests use a `RaiseAlertReceived` helper (mirrors `RaiseProcessStatesUpdated` from `FirewallTabViewModelTests.Reclassify.cs`) to drive the live-update path without spinning up a real gRPC stream.

---

### Phase 6.7 — Action buttons (BLOCK PROCESS OUT, ADD RULE, two-line layout) ✅

**Purpose:** Wire the detail-pane action buttons to actual daemon RPCs. Add the BLOCK/UNBLOCK toggle (calls `ApplyFirewallRule` / `RemoveFirewallRule` against the alert's process path with `Outbound + Block`) and the ADD RULE deep-link to the Firewall tab. Plus a master-list two-line row layout so the timestamp and process name no longer overlap.

**Key components:**
- **Two-line master row layout** (commit `c0b1c3c`). Process name + timestamp on row 1, summary on row 2. Solves the overlap reported during smoke test of 6.6.
- **BLOCK/UNBLOCK toggle.** State derived from `_outboundBlockedPaths` (seeded at activation, updated via `RuleChange` broadcasts). One button that flips between the two RPCs.
- **ADD RULE deep-link.** Navigates to Firewall tab and calls `_firewallTab.HighlightRow(processPath)` via the `_navigateToFirewallRule` delegate from `MainWindowViewModel`.
- **`AlertsTab.ActivateAsync` wiring** (commits `ce33139` → `fb7ef28` revert → `a7e881a` re-apply). The original 6.6 commit added `AlertsTab.ActivateAsync` but never called it from production: the Alerts tab list was being populated only by live broadcasts, so historic alerts at startup never showed. Soft-reverted the unauthorized one-line fix to enforce plan-mode discipline, then re-applied it via a proper plan as `a7e881a`. Workflow lesson — see §3 below.

**Tests added:** ~20 covering the BLOCK/UNBLOCK pill state machine, ADD RULE delegate invocation, two-row layout binding shape.

---

### Phase 6.7 polish — scroll-into-view + cold-start race fix ✅

**Two atomic commits** (`816763f`, `2cb8753`) addressing UX issues surfaced by smoke testing the deep-link from Alerts → Firewall:

- **`816763f`** — `FirewallTabViewModel.HighlightRow` now expands the row's containing group (intrinsic `row.IsActive` selector — robust against the `Reclassify` noise filter) and raises a new `RowScrollRequested` event; the view code-behind subscribes and calls `ItemsControl.ScrollIntoView(row)` (Avalonia 12 surface — verified in `Avalonia.Controls.xml` line 11392) which forces `VirtualizingPanel.ScrollIntoView(index)` to materialize the container before bubbling `BringIntoView` to the outer `ScrollViewer`. The original attempt used `ContainerFromItem(row)?.BringIntoView()` — silently no-oped because `ContainerFromItem` returns null until the row is realized.
- **`2cb8753`** — `MainWindowViewModel.NavigateToFirewallRule` was renamed to `NavigateToFirewallRuleAsync` returning `Task` and now awaits `_firewallTab.ActivateAsync` before calling `HighlightRow`. The actual root cause: `FirewallTabViewModel.ActivateAsync`'s `bool _activated` idempotency lied to concurrent callers — when `OnActiveTabChanged` fired its own fire-and-forget activation, the second call saw `_activated=true` and returned a synthetic completed Task while the first call was still loading. Replaced with `Task? _activationTask` so all callers await the same in-flight load. `INavigationService` delegate becomes `Func<string, Task>`; `AlertsTabViewModel.AddRule` becomes `async Task AddRuleAsync` (the `[RelayCommand]` suffix-strip keeps the existing `AddRuleCommand` AXAML binding stable).

Bundled into the 6.7 entry rather than a separate phase because the surface is unchanged.

---

### Phase 6.8 — OS-native notifications + new `Beholder.Ui.Windows` project ✅

**Purpose:** Fire OS-native notifications when an alert arrives via the live broadcast, with click-activation that restores the window and selects the matched alert. First UI-side platform split (mirrors `Beholder.Daemon.Windows`).

**Key components:**
- **`Beholder.Ui.Windows`** — new project, TFM `net10.0-windows10.0.17763.0` (Windows 10 1809+ for `Microsoft.Toolkit.Uwp.Notifications` 7.1.3). Hosts `WindowsNotificationService : INotificationService` which builds `ToastContentBuilder`, registers an `Activated` callback that parses `seq=N` from the toast args and restores the main window via `MainWindowViewModel.NavigateToAlertAsync`.
- **`Beholder.Core/INotificationService.cs`** — abstract `RaiseAsync(AlertKind, string title, string body, long seq)` + a `Activated` event. Cross-platform; the Windows impl is the only registration today.
- **`Beholder.Ui/Services/NoopNotificationService.cs`** — Linux fallback (no-op `RaiseAsync`, never raises `Activated`). Composition root picks Windows when available, Noop otherwise.
- **Conditional TFM** in `Beholder.Ui.csproj` AND `Beholder.Tests.csproj` (`net10.0-windows10.0.17763.0` on Windows builds, plain `net10.0` on Linux). Required because `Beholder.Ui` references `Beholder.Ui.Windows` conditionally — the consuming TFM must match.
- **`MainWindowViewModel.NavigateToAlertAsync(seq)`** — awaits `_alertsTab.ActivateAsync` then `SelectBySeq(seq)`. Toast click-activation entry point.

**Tests added:** ~25 covering `WindowsNotificationService` (severity → urgency mapping, title/body shape, click-callback parses seq correctly, registration idempotency), `NoopNotificationService` (silent + non-throwing), `MainWindowViewModel.NavigateToAlertAsync` (activates + selects).

**Design decisions:**
- **`Microsoft.Toolkit.Uwp.Notifications` 7.1.3** chosen over Windows App SDK's `AppNotifications` because the Toolkit package supports unpackaged WPF/Avalonia executables without WinAppSDK runtime requirements.
- **`SystemDrawing.Common 4.7.0` vulnerability suppression** — transitively pulled by the toolkit; the toast-image surface is unreachable in our code (we don't attach images), so the suppression is documented in `Directory.Packages.props` with a comment.
- **No app-side rate-limiting.** OS Action Center groups toasts visually; per-alert toast firing is the v1 design. Coalescing ("+N more") deferred to a polish pass.

**Checkpoint addendum (merged back in the Phases 6.4 → 7.5 checkpoint):** `Beholder.Ui.Windows` was merged back into `Beholder.Ui` per ADR 008. The UI's platform delta is one notification service (60 LOC); the mirror to `Beholder.Daemon.Windows` (thousands of LOC across ETW, WFP, Authenticode, version-info P/Invoke, Pktmon SNI extraction, ...) was structural copying without an LOC sanity check. Toast service now lives at `Beholder.Ui/Services/WindowsNotificationService.cs` wrapped in `#if PLATFORM_WINDOWS`; `Microsoft.Toolkit.Uwp.Notifications` is a Windows-conditional `PackageReference` directly in `Beholder.Ui.csproj` under an `<ItemGroup Condition="'$(OS)' == 'Windows_NT'">` block. The conditional TFM on `Beholder.Ui.csproj` and `Beholder.Tests.csproj` stays (still required because the package only resolves on the Windows TFM). The daemon-side split (`Beholder.Daemon.Windows`) stays mandatory. 848 tests unchanged.

---

### Phase 6.9 — Dismissable error banner (`ErrorBanner` reusable control) ✅

**Purpose:** Five inline error-banner sites across `TrafficTabViewModel`, `FirewallTabViewModel`, `AlertsTabViewModel`, `FirewallActivityViewModel`, etc. all rendered slightly different XAML. Consolidate into one reusable control and add the missing X-dismiss affordance per UI_DESIGN.md §5.10.

**Key components:**
- **`Beholder.Ui/Controls/ErrorBanner.axaml*`** — UserControl with `Severity` (`Danger`/`Warn`), `Message`, and optional `DismissCommand` styled properties. When `DismissCommand` is null the X is hidden. Class-based styling for the two severity variants.
- **5 view sites** updated: `TrafficTabView`, `FirewallTabView`, `AlertsTabView`, `FirewallActivityStrip`, plus the `TrafficColsView` overlay banner — all now use `<controls:ErrorBanner Severity="Danger" Message="{Binding ErrorMessage}" DismissCommand="{Binding DismissErrorCommand}" />`.
- **5 VMs** gained `[RelayCommand] DismissError` + a `ClearError()` helper that all action methods call at entry (auto-clear-on-action-retry — the X is for "I don't want to retry").
- **UI_DESIGN.md §5.10** added to document the pattern (this commit landed the doc + the implementation in one go).

**Tests added:** ~20 covering `DismissError` clears state across all 5 VMs, action-method-entry auto-clear, `ErrorBanner` hides the X when `DismissCommand` is null.

**Design decisions:**
- **No new component pattern without a §5 doc entry.** UI_DESIGN.md §9 rule #5 mandates that new component patterns get documented in the same commit. The doc-update is a hard gate, not an after-the-fact cleanup.

---

### Phase 6.10 — Disable action buttons when alert's executable is missing ✅

**Purpose:** Mirror the Firewall tab's orphaned-rule affordance on the Alerts detail pane. When the alert's process binary no longer exists on disk, both action buttons (BLOCK/UNBLOCK and ADD RULE) gray out and an `EXECUTABLE NOT FOUND` caption appears in `SeverityWarn`. The buttons are useless on a missing binary — a Block rule against a path no process can occupy is just noise.

**Key components:**
- **`AlertsTabViewModel._fileExistsCheck` injection** — `Func<string, bool>?` constructor parameter, defaults to `File.Exists`. Mirrors `FirewallTabViewModel`'s exact pattern.
- **`AlertRow.IsExecutableMissing`** — observable. Refreshed in `AlertsTabViewModel.OnSelectedAlertChanged` (selection-driven, not eager — a 500-row alert list shouldn't trigger 500 file-existence checks at activation).
- **`AlertsTabView.axaml` detail-pane footer** — buttons bound to `IsEnabled="{Binding !SelectedAlert.IsExecutableMissing}"` with `ToolTip.ShowOnDisabled="True"` so the existing button tooltips remain readable.

**Tests added:** ~5 covering selection of an alert with missing exe sets `IsExecutableMissing=true`, selection of an alert with present exe leaves it false, `_fileExistsCheck` is invoked exactly once per selection change, buttons-disabled state survives `MarkAlertRead` round-trip.

**Design decisions:**
- **Selection-driven, not eager.** Firewall tab does eager file-existence check for ~80 rules at activation (sub-millisecond on warm caches); Alerts can have 500 rows and selection is rare, so on-demand check is cheaper.
- **Mirror over invent.** Phase 6.10 plan specifically called for matching the Firewall tab's affordance, not designing a new one. ~30 LOC to deliver because the pattern was already established.

---

### Phase 7 — Alert pipeline (daemon side, all 4 sub-phases atomic in `d51c625`) ✅

**Purpose:** Generate the three alert kinds end-to-end: `NewProcess` on first network flow per binary, `HashChanged` on SHA-256 mismatch (or Phase 7.5 publisher mismatch), `ChainError` on chain-verify failure. Detectors run as `IHostedService` instances and emit via the `AlertEmitter` facade.

**Key components:**

- **`Beholder.Daemon/Pipeline/NewProcessDetector.cs`** — subscribes to `IProcessFirstNetworkFlowSource.OnProcessFirstNetworkFlow` (a session-scoped fire-once-per-key event raised by `TrafficEngine`). Three-tier dedup walk: path lookup (daemon-restart re-observation → silent + last-seen refresh), logical-identity lookup (Phase 7.5), genuinely-new (register + emit `NewProcess`).
- **`Beholder.Daemon/Pipeline/BinaryHashMonitor.cs`** — `IHostedService` running `SweepOnceAsync` every `AlertOptions.BinaryHashCheckIntervalMinutes` (default 60). Re-hashes every registered binary, emits `HashChanged` on mismatch, refreshes `last_hash_at` regardless. First hash establishes baseline silently.
- **`Beholder.Daemon/Pipeline/ChainIntegrityMonitor.cs`** — periodic `IEventStore.VerifyAsync` runs; emits `ChainError` (with the failing seq + reason) on integrity failure. Daemon does NOT refuse to start on chain failure — just alerts and continues, so the user can investigate.
- **`Beholder.Daemon/Pipeline/AlertEmitter.cs` + `IAlertEmitter`** — facade combining `IEventStore.AppendAsync` + `BroadcastService.BroadcastAlert`. Saves the three detectors from each juggling both. The `IEventStore.AppendAsync` signature change `Task → Task<long>` (returning the new chain seq) was source-compatible because every existing caller discarded the return value.
- **`Beholder.Daemon/Pipeline/IProcessFirstNetworkFlowSource.cs`** — new interface; `TrafficEngine` implements it by tracking which `(path, displayName)` pairs have already fired in this session. Cross-session re-observations are caught at the registry-lookup tier of `NewProcessDetector`.
- **`Beholder.Daemon/Pipeline/BroadcastService.cs`** — added `BroadcastAlert` hook + `AlertEvent` proto wiring (commit `1eef757`, separate prereq commit before the atomic Phase 7 detector ship).
- **`Beholder.Daemon/Storage/AlertPayloadEncoder.cs`** — deterministic JSON encoder for `Alert` proto. Same byte-stable contract pattern as `FirewallRulePayloadEncoder`.

**Tests added:** ~80 across `NewProcessDetectorTests`, `BinaryHashMonitorTests`, `ChainIntegrityMonitorTests`, `AlertEmitterTests`, `BinaryHasherTests`. Plus 6 new test doubles: `FakeAlertEmitter`, `FakeProcessFirstNetworkFlowSource`, extensions to `FakeProcessRegistry` / `FakeEventStore`.

**Design decisions:**
- **Detector + facade emitter.** Each detector knows when to alert; `AlertEmitter` knows how. SRP intact even though the seam is "small" — the alternative (each detector calls both `_eventStore.AppendAsync` and `_broadcast.BroadcastAlert`) duplicates the chain+broadcast contract three times.
- **Session-scoped fire-once-per-key dedup at the source.** `TrafficEngine.OnProcessFirstNetworkFlow` only fires once per unique `(path, displayName)` per process lifetime. Cross-session dedup is the registry's job. This split keeps the engine stateless across restarts.
- **First hash establishes baseline silently.** A registry entry created by `NewProcessDetector` arrives with `Sha256 == null`. The first hash check stores the value without alerting. Subsequent ticks compare against the stored hash; inequality emits + overwrites so a single patch only alerts once.

---

### Phase 7.5 — Logical app identity + Authenticode spoof detection ✅

**Purpose:** Fix the Discord-auto-update repeated-NEW-alert problem. Squirrel auto-updaters extract each new version into a sibling `app-<version>` folder under the app's install root, so every patch produced a fresh `NewProcess` alert against an essentially identical binary. Solution: dedup by `(CompanyName, ProductName, install-root)` from PE VersionInfo when a Valid Authenticode signature is present. Beats GlassWire/SimpleWall on this class. New ADR 007.

**Key components:**

- **`Beholder.Core/IBinaryIdentityProvider.cs`** — `Task<BinaryIdentity?> ReadIdentityAsync(string path, CancellationToken ct)`. Returns `null` for unreadable / corrupt binaries (graceful degradation).
- **`Beholder.Core/BinaryIdentity` record** + `AuthenticodeInfo` + `SignatureValidationStatus` enum. All in `Beholder.Core` so `NewProcessDetector` doesn't depend on the Windows-specific implementation.
- **`Beholder.Daemon.Windows/PeVersionInfoReader.cs`** — `LibraryImport`-based P/Invoke into `version.dll`'s `GetFileVersionInfoExW` + `VerQueryValueW`. Reads `\StringFileInfo\<lcid>\CompanyName` and `\ProductName`.
- **`Beholder.Daemon.Windows/AuthenticodeVerifier.cs`** — `WinVerifyTrust` for signature validation + `X509Certificate.CreateFromSignedFile` for cert extraction. Returns `null` for catalog-signed binaries (no embedded cert; falls through to path-based dedup).
- **`Beholder.Daemon.Windows/WindowsBinaryIdentityProvider.cs`** — composes the two above. Resolves install root by walking ancestors looking for a folder name matching `ProductName` (case-insensitive). Catches Discord at AppData vs Program Files as different installs.
- **`Beholder.Core/ProcessInfo.cs`** — added 6 fields: `CompanyName`, `ProductName`, `InstallRoot`, `CertSubjectCn`, `CertIssuerCn`, `SignatureStatus`.
- **`Beholder.Core/IProcessRegistry.cs`** — added `FindByLogicalIdentityAsync(companyName, productName, installRoot, ...)`.
- **`Beholder.Daemon/Storage/SqliteProcessRegistry.cs`** — extended with idempotent `ALTER TABLE ADD COLUMN IF NOT EXISTS` migration for the 6 new identity columns; `RegisterAsync` updates them via `INSERT...ON CONFLICT DO UPDATE SET company_name = excluded.company_name, ...`.
- **`Beholder.Daemon/Pipeline/NewProcessDetector.cs`** — three-tier dedup walk now: (1) path → silent; (2) logical-identity match → silent if same publisher (Squirrel update), `HashChanged` with publisher-mismatch summary if different publisher (SPOOF DETECTED); (3) genuinely new → `NewProcess`.
- **ADR 007** (`docs/decisions/007-logical-app-identity-and-spoof-detection.md`) — full rationale, alternatives considered, out-of-scope items.

**Tests added:** ~50 across `WindowsBinaryIdentityProviderTests`, `AuthenticodeVerifierTests`, `PeVersionInfoReaderTests`, plus the Phase 7.5 sections in `NewProcessDetectorTests` (logical-identity match same/different publisher, different install root, no provider, unsigned, identity-provider-returns-null fallback added in checkpoint cleanup).

**Design decisions:**
- **Tier 1 = logical identity (signed publisher + product + install root); Tier 2 = path fallback.** Public-key pinning / Tier 4 ADR considered and ruled out as over-engineered for v1 (false-positive risk on legitimate cert rotation).
- **Catalog-signed binaries fall through to path-based.** notepad.exe et al. have no embedded cert — `AuthenticodeVerifier.Read` returns `null` and `NewProcessDetector` skips identity dedup for them. Documented limitation per ADR 007 § Out of scope.
- **Identity backfill not auto-applied.** Pre-7.5 `process_registry` rows have NULL identity columns; only newly-seen paths get identity resolved. Listed as a known gap in §5.
- **`SignatureValidationStatus.Valid` required for identity dedup.** Untrusted/Expired/Revoked signatures don't qualify — those binaries fall through to path-based dedup so a spoofer can't suppress the alert via a self-signed cert.

---

### Phase 8 — Traffic → Map sub-view (world heatmap) ✅

**Purpose:** Wire the Traffic tab's previously-disabled MAP toggle to a custom-Canvas world heatmap. Visualizes the per-country byte totals already shipped daemon-side via the `GetCountryBreakdown` RPC (Phase 4.6a); no new RPCs, no proto changes, no daemon-side work.

**User scope (per AskUserQuestion):**
- Traffic-tab sub-view, NOT a new top-level MAP tab. The MAP pill in the GRAPH/COLS/MAP toggle group already existed disabled with a "coming soon" tooltip.
- Full SVG country polygons (Natural Earth 110m simplified), not centroid circles.
- Hover tooltip only (no click-to-filter in this phase).
- Equirectangular projection.

**Library survey conclusion:** every Avalonia 12 map library either lacks v12 support (LiveCharts2 2.0.4 stable / Mapsui 5.0.2 both Av11-only) or requires external map-tile servers (Mapsui, XAML.MapControl.Avalonia — incompatible with the "no outbound network" stance). Custom Canvas mirroring the `TrafficChartControl` precedent is the path. Documented in §5 Known Gaps prune below.

**Key components:**

- **`Beholder.Ui/Assets/world-countries-110m.geojson`** (~170 KB asset) — Natural Earth Admin 0 Countries, public domain CC0, trimmed via a one-shot Python preprocessor to `{iso_a2, name, geometry}` per feature with 2-decimal coordinate precision. 177 countries; the two disputed-territory `ISO_A2 = "-99"` entries (Kosovo, Northern Cyprus) remap to `??` so the polygons still render as gray rather than disappearing.
- **`Beholder.Ui/Models/CountryShape.cs` + `GeoPoint.cs`** — immutable shape records, one type per file per CODING_STANDARDS.md §File Naming.
- **`Beholder.Ui/Services/WorldGeometryLoader.cs`** — parses the embedded asset once via `Lazy<IReadOnlyList<CountryShape>>` (thread-safe one-shot init, no static mutable state per CLAUDE.md banned-pattern table). Returns empty list on malformed JSON for graceful degradation; the control renders an empty ocean + "world map unavailable" caption instead of crashing.
- **`Beholder.Ui/Controls/WorldMapControl.cs`** (~200 LOC, under the CLAUDE.md class-size threshold) — custom `Control` subclass. Two `StyledProperty<T>` (`Countries`, `MaxBytes`); lazy brush resolution cached + invalidated on `ResourcesChanged` (theme swap); `Render(DrawingContext)` override; `PointerMoved` → hit-test → tooltip overlay drawn inline.
- **`Beholder.Ui/Controls/WorldMapProjection.cs`** — pure equirectangular `Project` / `Unproject` static helpers (~30 LOC).
- **`Beholder.Ui/Controls/WorldMapHitTester.cs`** — bounding-box prefilter + point-in-polygon ray-cast (~70 LOC). Pure; trivially testable.
- **`Beholder.Ui/Controls/HeatmapPalette.cs`** — 5-stop ramp with named constants for the stop fractions (no magic numbers per CODING_STANDARDS.md). `BrushFor(bytes, maxBytes)` returns the appropriate stop's brush. Test-injectable constructor (internal) so unit tests can verify ramp selection without depending on the runtime theme dictionary.
- **`Beholder.Ui/ViewModels/TrafficTabViewModel.cs`** — adds `SetMapView` command, `MapCountries` / `MaxMapBytes` / `LocalAndUnknownCaption` observable properties, `RefreshMapAsync` method. Single-flight CTS (`_mapCts`) so rapid view-mode / range / process changes cancel prior fetches. The `"--"` (LAN) and `"??"` (Unknown) sentinel countries are filtered off the map and surface in the caption strip instead.
- **`Beholder.Ui/Views/Tabs/TrafficTabView.axaml`** — enables the MAP button (drop `IsEnabled="False"` + "coming soon" tooltip), adds a third `<Grid IsVisible="{Binding IsMapActive}">` sibling to GRAPH and COLS with the `WorldMapControl` + LAN/Unknown caption + `ErrorBanner` overlay.
- **`Beholder.Ui/Themes/DarkTheme.axaml` + `LightTheme.axaml`** — 5 new heatmap tokens (`HeatmapCold` / `HeatmapLow` / `HeatmapMedium` / `HeatmapHigh` / `HeatmapPeak`). Dark values picked deliberately (not opacity-derived from a single base); light TBD per UI_DESIGN.md §10.

**Tests added (~31):**
- `WorldMapProjectionTests` (6) — origin → center, lon at min/max → edges, lat at min/max → edges, round-trip Project/Unproject preserves Berlin coordinates.
- `WorldMapHitTesterTests` (5) — point-inside / point-outside / point-in-bbox-but-outside-polygon / one-of-multiple-shapes / multi-polygon ring.
- `HeatmapPaletteTests` (6) — zero bytes → Cold, max-zero → Cold, sub-low → Low, medium / high / peak stop selection at boundary fractions.
- `WorldGeometryLoaderTests` (6) — valid polygon, multi-polygon flattening, missing-iso-a2 skip, sub-3-point-ring skip, malformed JSON → empty list, missing-features-array → empty list, two-LoadOnce-calls return same instance.
- `TrafficTabViewModelMapTests` (8) — SetMapView flips ViewMode, view-mode flip triggers fetch, LAN/Unknown filtered to caption not map, MaxMapBytes excludes LAN, time-range change while MAP active refetches, time-range change while GRAPH active doesn't, RPC failure surfaces in ErrorBanner.

**Design decisions:**
- **Lat/lon stored unprojected; projection applied per render.** Future polish can swap to Mercator or Equal Earth by replacing `WorldMapProjection.Project` only — the asset doesn't need re-processing.
- **5 heatmap stops as deliberate tokens, not opacity overlays.** Per UI_DESIGN.md §9 rule #6: every effective on-screen color is a token. Opacity overlays of a single base would resolve to a color not in the token table, breaking the "every color comes from §2" contract; baked-alpha tokens preserve it.
- **Asset preprocessing one-shot at fetch time, not at runtime.** The Python trimmer ran once during dev (~819 KB → ~170 KB, 79% reduction); the runtime loader does minimal parsing. Avoids shipping 819 KB to every install for ~5 ms of dev-time savings.
- **Hit-test in geographic coordinates, not screen coordinates.** Same logic works for any projection without modification — the caller unprojects the screen point once, the hit-tester compares lat/lon.
- **No new ADR.** The "custom Canvas not LiveCharts2" decision mirrors Phase 5's `TrafficChartControl` precedent (no ADR shipped for that either). The library-survey findings are recorded in this entry + §5 Known Gaps prune.

**Files NOT touched:** Daemon side (no new RPCs), `Beholder.Core` (no new types — uses existing `CountryCode` + `CountryTrafficSummary`), `Beholder.Protocol` (no proto changes), tests for the rest of the Traffic tab (existing 31 `TrafficTabViewModelTests` still pass unchanged).

**Checkpoint addendum (Phase 8 polish: top-3 destinations per country on hover):**

Hover tooltip extended to show the country's top-3 destinations by total bytes, lazy-fetched per country and cached. Daemon-side: `GetProcessDestinationsRequest` gained two optional fields (`country`, `limit`); `SqliteTrafficStore.GetDestinationsAsync` signature changed to take a new `Beholder.Core/DestinationsQuery` record (5 query params grouped per PRINCIPLES.md "Group related parameters into a record"); SQL gained an optional country clause + `LIMIT` after the existing `ORDER BY total bytes DESC`. UI-side: extracted tooltip layout into a new `WorldMapTooltipRenderer.cs` so `WorldMapControl.cs` stays under the CLAUDE.md ~200 LOC class threshold; the renderer handles five visually distinct states (No-fetch-yet / Loading / Empty / Populated / Failed) per UI_QUALITY_STANDARDS §3.1; named constants `HeaderFontSize`/`RowFontSize`/`Top3DestinationsLimit` replace inline magic numbers. VM gained `_topDestCts` single-flight CTS + per-`(country, range, process)` cache, plus four observable bool flags + `IReadOnlyList<DestinationRow>?` driving the tooltip state. Failed fetch silently degrades to "destinations unavailable" — destinations are opportunistic, an ErrorBanner would obscure the map; regression test (`OnHoveredCountryChanged_RpcFails_SetsFailedFlagAndNoErrorBanner`) locks in the design. 879 → 890 tests (+11 across `SqliteTrafficStoreTests`, new `GetProcessDestinationsRpcTests`, `TrafficTabViewModelMapTests`).

### Phase 9.1 — Scanner foundation (LAN device storage + OUI vendor lookup) ✅

**Purpose:** First sub-phase of Phase 9. Ships the storage and OUI plumbing only — no probe, no scheduler, no UI, no chain-event writes, no options. The deliverables establish the data surface that 9.2–9.6 build on. Scoping context lives in [ADR 009](decisions/009-scanner-as-lan-device-discovery.md), which rejects port scanning / CVE lookup / anomaly detection in favor of LAN device discovery (GlassWire-Things-style + cross-link to the Traffic tab as the differentiator).

**Daemon side:**
- **`Beholder.Daemon/Storage/DatabaseInitializer.cs`** — gains `CREATE TABLE IF NOT EXISTS lan_device` (mac PRIMARY KEY, ip, vendor, hostname, first_seen_unix_ns, last_seen_unix_ns) + two indexes (`idx_lan_device_ip` for 9.2's MAC-change detection, `idx_lan_device_last_seen` for 9.3's `ListLanDevices` "seen since" filter). Idempotent via `IF NOT EXISTS` per the existing convention.
- **`Beholder.Core/LanDevice.cs`** + **`LanDeviceQuery.cs`** + **`ILanDeviceStore.cs`** + **`IOuiVendorLookup.cs`** — four new Core types, each with XML docs per `CODING_STANDARDS.md`. `LanDeviceQuery` bundles `SeenSince` + `Limit` so `ListAsync` stays at 2 args (PRINCIPLES.md "group related parameters into a record" — same precedent as Phase 8 polish's `DestinationsQuery`).
- **`Beholder.Daemon/Storage/SqliteLanDeviceStore.cs`** (~150 LOC) — implements `ILanDeviceStore` with the established `ConnectionFactory` + real-async pattern (mirrors `SqliteFirewallRuleStore`). `UpsertAsync` uses `INSERT … ON CONFLICT(mac) DO UPDATE SET …` with `first_seen_unix_ns` deliberately omitted from the SET clause so the original observation timestamp is preserved across re-observations (same trick as `firewall_rules.created_at`). `GetByIpAsync` returns the first match with `LIMIT 1` — IP uniqueness is deliberately NOT enforced, so 9.2's "known IP + new MAC = potential ARP spoof" detection can find one of the rows and compare its MAC explicitly.
- **`Beholder.Daemon/Scanner/OuiVendorLookup.cs`** + **`OuiCsvParser.cs`** — new `Scanner/` folder under `Beholder.Daemon/` (parallel to `Detectors/` / `Storage/`). The lookup loads the OUI CSV once at construction into an in-memory `Dictionary<string, string>` keyed on uppercase 6-hex-char OUI prefix; `GetVendor(mac)` normalizes input (strips `:` / `-`, uppercases, takes first 6 chars, validates hex) and returns the matching vendor or null. **Missing file degrades gracefully**: warning logged, empty dictionary, every lookup returns null — daemon stays functional, LAN devices just don't get vendor names. Matches the existing `NullGeoIpResolver` posture. Parser isolated as a static class with a `TextReader` input so it's unit-testable without touching the filesystem; per-row `FormatException` / `IndexOutOfRangeException` are caught and the row skipped silently (the IEEE file occasionally contains odd quoting).
- **`Beholder.Daemon/Program.cs`** — two new singleton registrations after the existing store block: `SqliteLanDeviceStore` + `ILanDeviceStore` (two-step concrete-then-interface pattern), plus `IOuiVendorLookup` constructed with `AppContext.BaseDirectory/data/oui.csv`.

**Tools side:**
- **`Beholder.Tools.OuiFetcher/`** (new project) — console tool mirroring `Beholder.Tools.GeoIpFetcher` precisely: `<OutputType>Exe</OutputType>`, bare `HttpClient`, no package references. GETs `https://standards-oui.ieee.org/oui/oui.csv` (~3.7 MB plain text), writes `data/oui.csv` (or `--output` override), idempotently appends an IEEE OUI section to `data/ATTRIBUTION.md`. Exit 0 on success, 1 on network failure (deletes partial files on error so the daemon never loads half-downloaded data).
- **`Beholder.tools.OuiFetcher/README.md`** — 5-line operational doc matching the GeoIpFetcher README's shape (justified as project precedent for tool projects).

**Build wiring:**
- **`Beholder.Daemon/Beholder.Daemon.csproj`** — `<Content Include="..\data\oui.csv">` block with `Condition="Exists(...)"` so the build doesn't fail if the file is absent (matches the GeoIP MMDB pattern).
- **`Beholder.slnx`** — new project entry for `Beholder.Tools.OuiFetcher`.

**Data:**
- **`data/oui.csv`** is **NOT** committed — added to `.gitignore` alongside the existing `/data/*.mmdb` and `/data/ATTRIBUTION.md` rules. Users run `Beholder.Tools.OuiFetcher` after cloning to populate it, same operational pattern as `Beholder.Tools.GeoIpFetcher` for the MMDB. The daemon's csproj `<Content Include … Condition="Exists(...)">` block means the build doesn't fail when the file is absent, and `OuiVendorLookup` degrades gracefully at startup with a warning log.
- **`data/ATTRIBUTION.md`** is also gitignored; `OuiFetcher` idempotently appends an IEEE OUI Registry section to whatever the user has fetched (so running both `GeoIpFetcher` + `OuiFetcher` produces a complete attribution file locally).

**Tests added (~30):**
- **`SqliteLanDeviceStoreTests`** (~190 LOC, 13 tests): null-factory guard, upsert insert / update / first-seen preservation, GetByMac known / unknown, GetByIp known / unknown / two-MACs-share-an-IP (locks in 9.2's design), list ordering / SeenSince filter / Limit / empty table, null-vendor-and-hostname round-trip.
- **`OuiVendorLookupTests`** (~90 LOC, 9 tests including a Theory): file-missing graceful-degradation (the PRINCIPLES.md "every error path tested" guard), known prefix + dash / colon / lowercase / mixed-case normalization, embedded comma in vendor name preserved, unknown prefix returns null, theory over malformed inputs (empty / too-short / non-hex).
- **`OuiCsvParserTests`** (~80 LOC, 7 tests): empty reader, header only, MA-L extraction, non-MA-L skip, malformed-row tolerance (the parser-side error-path test), quoted-comma preservation, null reader throws.

**Out of scope (lands in 9.2–9.6 per ADR 009):**
- `ILanDeviceProbe` + `WindowsLanDeviceProbe` P/Invoke (ARP / mDNS / NetBIOS) — Phase 9.2.
- `LanScannerService` hosted-service scheduler + chain-event writes (`LanDeviceFirstSeen` / `LanDeviceMacChanged` constants + emission) — Phase 9.2.
- `ScannerOptions` (`Scanner:ScanIntervalSeconds`) — Phase 9.2 (no consumer in 9.1).
- RPC additions (`ListLanDevices`, `TriggerScan`) — Phase 9.3.
- Scanner tab UI (replace the stub) — Phase 9.4.
- Cross-link from Scanner row → Traffic tab filtered by `remote_address` — Phase 9.5.
- IPv6, multi-subnet, OS fingerprinting, port-scan-per-device, WiFi metadata — all out per ADR 009.

**Design decisions:**
- **Identity = MAC, not IP.** ADR 009 §Identity model. IP is mutable (DHCP); MAC is the durable layer-2 identifier. MAC randomization on modern phones is acknowledged as a known limitation — we record what's observable, same philosophical stance as ADR 007's path-based fallback for unsigned binaries.
- **No new alert kinds.** ADR 002's three-alert cap is preserved. LAN-discovery events go to the chain (audit) and will be surfaced in a Scanner-tab activity strip (Firewall pattern), NOT in the Alerts tab. An opt-in "alert me on new device" toggle is a Phase 13 Settings concern + a future ADR superseding 002.
- **Daemon-side embedded assets follow the GeoIP precedent, not the Phase 8 Avalonia precedent.** Side-by-side `data/oui.csv` copied via MSBuild `<Content>`, not `<EmbeddedResource>` baked into the assembly. The OUI lookup is daemon-side, and the existing daemon-side precedent for embedded data is the GeoIP MMDB.
- **Foundation-only commit, not full Phase 9.** Phase 9 splits into six sub-phases. Shipping 9.1 in isolation matches the project's existing cadence (Phase 5.1 shipped `SqliteTrafficStore` before any rollup or RPC code; Phase 6.1 shipped the firewall store before the WFP controller). Reviewable in isolation; testable end-to-end at the store and lookup layer; 9.2's scanner drops in on top.

**Files NOT touched:** UI side (no UI changes in 9.1; Scanner tab UI is 9.4), `Beholder.Protocol` (no proto changes; RPC additions are 9.3), `Beholder.Daemon.Windows` (no platform-specific code in 9.1; probe is 9.2), `Beholder.Daemon.GeoIp` / `Beholder.Daemon.Uplink` (unrelated).

890 → 919 tests (+29 across the three new test files). One atomic commit including the ADR.

### Phase 9.2 — Scanner ARP probe + scheduler + chain events ✅

**Purpose:** Second sub-phase of Phase 9. Ships the actual scanner that consumes 9.1's storage + OUI lookup foundation: a Windows-side ARP probe, the cross-platform `IHostedService` scheduler that drives probes on a configurable cadence, MAC-vs-IP state-transition detection that emits chain events for new devices and MAC changes, and the `ScannerOptions` config surface that 9.1 deferred. After 9.2 lands the daemon log shows `LAN scanner: N devices observed, M first-seen, 0 mac-changed` every 5 minutes and the `lan_device` table populates with real data. **Per the explicit scope decision recorded in this commit's plan: ARP only for 9.2 — mDNS + NetBIOS hostname resolution lands as 9.2.5.** Vendor names from 9.1's OUI lookup are already informative enough to ship a meaningful scanner; the callback-based `DnsServiceBrowse` and Win32 NetBIOS surfaces are better held back to a follow-up commit where any surprises don't balloon the blast radius.

**Core (Beholder.Core):**
- **`EventKind.cs`** — gains `LanDeviceFirstSeen = 9` and `LanDeviceMacChanged = 10` (ordinal-stable additions at the end). Documented in the `event_log.kind` comment from 9.1's ARCHITECTURE.md forward-reference; now consumed.
- **`ILanDeviceProbe.cs`** — single-method interface: `Task<IReadOnlyList<LanDeviceObservation>> ScanAsync(CancellationToken)`. Request/response shape rather than `IFlowSource`'s continuous-event shape, because scanning is bursty (one full sweep every N minutes, batched results), not per-packet streaming.
- **`LanDeviceObservation.cs`** — record with Mac / Ip / Hostname / ObservedAt. Vendor is deliberately NOT on the observation; `LanScannerService` enriches via `IOuiVendorLookup` (Phase 9.1) before storing, per SRP — the probe layer stays focused on "what's on the wire."

**Cross-platform daemon (Beholder.Daemon):**
- **`ScannerOptions.cs`** — `ScanIntervalSeconds` (default 300; floor 30 enforced at scheduler-startup by `Math.Max`). Bound via `IOptionsMonitor<ScannerOptions>` so the value can live-reload between daemon restarts; the current implementation snapshots at scheduler start (matches `ReverseDnsFallbackCache`'s `EnablePreload` snapshot pattern from ADR 005).
- **`Scanner/LanScannerService.cs`** (~210 LOC) — `IHostedService` with `IAsyncDisposable`. Runs an immediate first scan on `StartAsync` (so log activity appears within seconds of daemon start rather than after a 5-minute wait), then loops on `PeriodicTimer(TimeSpan, TimeProvider)` (.NET 8+ ctor; bound to the injected `TimeProvider` so tests use `FakeTimeProvider.Advance` for deterministic tick firing). Per scan: invoke probe → for each observation, enrich vendor → check existing by MAC and by IP → emit chain event for new MAC (LanDeviceFirstSeen) or known-IP-different-MAC (LanDeviceMacChanged) → upsert lan_device row (preserving original first_seen). Per-observation error boundary: chain-write failure still upserts; per-observation processing failure still processes the rest of the batch; probe-level failure logs and retries on the next tick. Internal `TotalScansCompleted` counter exposed for deterministic test polling (avoids racing the probe's call counter against in-flight processing).
- **`Storage/LanDevicePayloadEncoder.cs`** — `EncodeFirstSeen` / `TryDecodeFirstSeen` / `EncodeMacChanged` / `TryDecodeMacChanged` + two nested payload records (`LanDeviceFirstSeenPayload`, `LanDeviceMacChangedPayload`). Mirrors `AlertPayloadEncoder` / `FirewallRulePayloadEncoder` exactly: `Utf8JsonWriter` with explicit field order for byte-deterministic output (chain hash covers exact payload bytes). `Try…` decode methods return null on malformed JSON or missing required fields (matches the established convention; no throwing).
- **`Program.cs`** — registers `ScannerOptions` + `LanScannerService` (as both singleton + hosted service via the two-step pattern). Currently inside the `#if PLATFORM_WINDOWS` block alongside its dependencies (storage, OUI lookup, event store are all Windows-only today). When the Linux daemon stabilizes, both the 9.1 storage block and this 9.2 scheduler block can be hoisted outside the `#if` — the `LanScannerService` constructor accepts `ILanDeviceProbe?` (nullable, default null) per ADR 007's pattern, so Linux without a probe registration produces a "scanner inactive" warning at startup rather than a DI activation failure.

**Windows daemon (Beholder.Daemon.Windows/Scanner/):**
- **`WindowsLanDeviceProbe.cs`** (~90 LOC) — implements `ILanDeviceProbe`. For 9.2 orchestrates only the ARP probe; mDNS + NetBIOS sub-probes plug in here during 9.2.5. Discovers the primary IPv4 subnet via `NetworkInterface.GetAllNetworkInterfaces` (skip Loopback / Tunnel / Ppp / Unknown; require Up + non-empty GatewayAddresses + IPv4 mask present + non-degenerate mask). `public sealed` (not `internal sealed`) because the cross-project DI registration in `Beholder.Daemon/Program.cs` needs access — matches the visibility convention of `EtwFlowSource` and `WfpFirewallController`.
- **`ArpScanProbe.cs`** (~100 LOC) — iterates the discovered subnet with 5 ms inter-probe spacing (~1.3 s for a /24, ~5 s for /22). Defensive `MaxHostsPerScan = 4094` (a /20) ceiling — anything larger would imply a corporate LAN where ARP-sweeping every host is impolite and a more targeted scope would need to be configurable. `EnumerateHostAddresses` is a pure static method exposed `public static` for future unit testing of the bit-twiddling logic in isolation. `ArpResult` nested record (`public sealed record`) — precedent: `OuiCsvParser`'s parsing data shape is similarly nested.
- **`IphlpapiInterop.cs`** (~75 LOC) — `LibraryImport` source-generated marshalling for `iphlpapi.dll` `SendARP`. Mirrors `DnsApiInterop`'s pattern from ADR 004 but simpler: `SendARP` is a documented Win32 export since NT 4.0, so no `NativeLibrary.TryGetExport` probing is needed — just catch `DllNotFoundException` / `EntryPointNotFoundException` as defensive fallbacks (older / stripped Windows installs). Returns null on every expected failure mode (status != 0, len != 6, missing export, non-IPv4 input). `internal static partial` so the source generator can write the marshalling stubs.

**Tests added (~24):**
- **`LanScannerServiceTests.cs`** (~280 LOC, 11 tests): null-store ctor guard, null-probe Linux fallback (warning + no scans), with-probe immediate first scan, new-MAC inserts + FirstSeen event, known-MAC preserves first_seen and updates last_seen with no chain event, known-IP-new-MAC emits MacChanged event (with payload round-trip verification), vendor-unknown stores null, empty-probe yields no writes, probe-throws survives and continues next tick (uses `FakeTimeProvider.Advance` to trigger the recovery tick), chain-write failure still upserts (the "every error path tested" guard for the chain seam), sub-floor interval clamps to MinIntervalSeconds. Helper `WaitForScansCompletedAsync` polls `TotalScansCompleted` (incremented AFTER per-observation processing finishes) so assertions don't race in-flight scans — separate `WaitForProbeInvocationsAsync` polls the probe's invocation counter for the throwing-probe case where TotalScansCompleted never reaches the target.
- **`LanDevicePayloadEncoderTests.cs`** (~110 LOC, 10 tests): round-trip both kinds, null-vendor-and-hostname round-trip as null, malformed-payload returns null (decoder error path), missing-required-field returns null, null mac/ip throws ArgumentNullException (not ArgumentException — `ArgumentException.ThrowIfNullOrWhiteSpace(null)` throws the more specific subtype, a subtle gotcha caught by tests), byte-identical output for identical input (chain-hash determinism guard).
- **`ScannerOptionsTests.cs`** (~30 LOC, 2 tests): defaults applied when no section; section overrides defaults.
- **`FakeLanDeviceProbe.cs`** test double — Responder hook + atomic ScanCount counter. Mirrors `FakeFlowSource` / `FakeReverseDnsResolver` shape.

**Docs:**
- **`ARCHITECTURE.md`** — new `### LAN Discovery (cold-path)` subsection under "Data Flow" with the end-to-end pipeline diagram. Explicitly distinguishes from "Network Telemetry (Hot Path)" because LAN scanning is rate-limited periodic background work, not per-packet hot-path.
- **`docs/manual-tests/lan-scanner.md`** (new) — runbook mirroring the etw-flow-source.md / etw-dns-cache.md shape exactly. Covers what unit tests can't reach: real LAN, ARP responses from actual devices, vendor lookup against the production-size `data/oui.csv`, fallback when no NIC is available, fallback when OUI snapshot is missing.
- **`phases.md`** (this file) — header refresh + §1 status update + this Phase 9.2 entry + two §3 lessons (cold-path vs hot-path PeriodicTimer choice; race-condition trap in test polling).
- **`README.md`** — status line bump (919 → 943); Working Today section gets a brief Phase 9.2 mention.

**Out of scope (deferred to 9.2.5 and beyond):**
- mDNS via `DnsServiceBrowse` — Phase 9.2.5.
- NetBIOS name queries — Phase 9.2.5.
- `GetIpNetTable2` pre-scan ARP cache pull — Phase 9.2.5 (currently `SendARP` alone is sufficient since active probing populates fresh data).
- IPv6 device discovery (NDP) — per ADR 009, IPv4 only for v1.
- Multi-subnet enumeration — per ADR 009.
- RPCs / UI / Traffic-tab cross-link — Phases 9.3 / 9.4 / 9.5 respectively.

**Files NOT touched:** UI side (no UI changes; Scanner tab UI is 9.4), `Beholder.Protocol` (no proto changes; RPC additions are 9.3), `Beholder.Daemon.GeoIp` / `Beholder.Daemon.Uplink` (unrelated).

919 → 943 tests (+24 across `LanScannerServiceTests` + `LanDevicePayloadEncoderTests` + `ScannerOptionsTests`). One atomic commit.

### Phase 9.2.1 — ARP cache walk + parallel SendARP (fix slow scan) ✅

**Purpose:** Bug-fix follow-up to Phase 9.2 (commit `fd57f39`). The 9.2 smoke test surfaced a real performance defect: `ArpScanProbe` issued `SendARP` calls sequentially with a 5 ms inter-probe delay, but Windows internally holds `SendARP` for ~1 s per non-responsive IP. On a typical home /24 with 5-30 active devices, that meant ~4 minutes wall-clock just for the per-IP timeouts — the user observed 3+ minutes with no scan-result log line and ctrl-c'd before any scan completed. Two compounding fixes restore sub-30-second wall-clock and a sub-5-second steady-state.

**Windows daemon (`Beholder.Daemon.Windows/Scanner/`):**
- **`IphlpapiInterop.cs`** — file roughly doubles in size (~75 → ~210 LOC). Adds `GetIpNetTable2` + `FreeMibTable` `LibraryImport` declarations + `MibIpNetRow2` struct definition (using `[MarshalAs(UnmanagedType.ByValArray, SizeConst = N)]` for the inline `SOCKADDR_INET` union and `PhysicalAddress[32]` buffer rather than `fixed` fields, to avoid introducing `unsafe` to the project) + `TryEnumerateIpv4ArpCache(ILogger)` enumeration helper. The helper mirrors `DnsApiInterop.TryEnumerateResolverCache`'s shape line-for-line per ADR 004's P/Invoke precedent: `NativeLibrary.TryLoad` + `TryGetExport` probe, acquire-or-skip, `IEnumerable<T>` + `yield return`, try/finally `FreeMibTable` for the unmanaged table pointer, graceful-degrade empty on every expected failure (export missing, non-zero status, empty cache, marshalling exception). Filters to `NlNeighborStateReachable / Stale / Permanent` only — skipping `Incomplete / Delay / Probe / Unreachable` would give stale or wrong MACs. Filters to Ethernet MACs (`PhysicalAddressLength == 6`) — other media (InfiniBand) aren't LAN devices in this scanner's sense.
- **`ArpScanProbe.cs`** — restructured. Old `ScanSubnetAsync` (single-threaded foreach with 5 ms inter-probe `Task.Delay`) deleted. New `ProbeIpsAsync(IEnumerable<IPAddress>, CancellationToken)` uses `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 64` and a linked `CancellationTokenSource` enforcing a 60 s per-scan deadline (deadline expiry returns partial results rather than throwing — distinguished from outer-cancel-token expiry which DOES rethrow, via a filtered `OperationCanceledException` catch). New `IsInSubnet(IPAddress, IPAddress, IPAddress)` static helper for cache-filtering. Constructor gains a test-injection overload: production callers use the default ctor that wires `IphlpapiInterop.TrySendArp` and 60 s; tests use the internal ctor to inject a fake probe delegate + a short deadline (mirrors Phase 8's `HeatmapPalette` test-injection idiom).
- **`WindowsLanDeviceProbe.cs`** — `ScanAsync` reshapes to the two-pass orchestration: cache walk via `IphlpapiInterop.TryEnumerateIpv4ArpCache` filtered by `ArpScanProbe.IsInSubnet` → diff against `EnumerateHostAddresses` → parallel probe the residue → merge into a `Dictionary<ip-string, observation>` with cache-wins semantics on IP collisions.

**Tests added (~12):**
- **`ArpScanProbeTests.cs`** (~190 LOC, 12 tests). Subnet math: `IsInSubnet` inside/outside/boundary/22-bit-mask/IPv6-input; `EnumerateHostAddresses` /24/over-ceiling/IPv6-input regression guards. `ProbeIpsAsync` against a fake probe delegate: empty input, all-responders, no-responders, mixed responses, parallel-dispatch (asserts 64 × 100 ms-sleep IPs complete in <1.5 s, proving parallelism), deadline-expires-returns-partial (asserts a 200 ms deadline against 500 ms-sleep probes returns within 2 s), outer-cancellation-throws, null-ctor guard.

**Tests NOT touched:** `LanScannerServiceTests` (11 tests) — all still pass. The probe-side change is hidden behind the `ILanDeviceProbe` interface; `LanScannerService` sees the same `IReadOnlyList<LanDeviceObservation>` it already consumed. `LanDevicePayloadEncoderTests` and `ScannerOptionsTests` unchanged.

**Docs:**
- **`ARCHITECTURE.md`** "LAN Discovery (cold-path)" subsection updated with the two-pass cache-walk + parallel-probe pipeline diagram. Explicitly notes that active probing per ADR 009 is preserved (cache walk is a passive READ of state Windows already maintains, not a substitute for the active probe — `SendARP` still runs for cache misses).
- **`docs/manual-tests/lan-scanner.md`** updated: expected scan-result log appears within ~5 s (steady-state) or ~30 s (cold-cache), NOT 4+ minutes. Added "if you've been waiting >30 s with no scan-result log, something is wrong" guidance. New "ARP cache walk skipped: ..." Known-non-issue covering pre-Win10 degraded mode.
- **`phases.md`** (this file) — header date / test count / checkpoint refresh + §1 status patch + this Phase 9.2.1 entry + §3 lesson.

**Out of scope (deferred):**
- `GetIpNetTable2` for IPv6 (NDP table) — same export supports `AF_INET6`; defer until IPv6 LAN discovery as a whole lands (still out per ADR 009).
- `ResolveIpNetEntry2` for actively populating the cache via the modern documented API — not needed; cache + `SendARP` is sufficient.
- `ScannerOptions.MaxParallelProbes` / `ScannerOptions.PerScanDeadlineSeconds` knobs — fixed at 64 / 60 for v1; tune via constants if real-world data demands it.
- mDNS + NetBIOS hostname resolution — still Phase 9.2.5.

**Design decisions:**
- **Cache wins on IP collisions during merge.** If both the cache and a fresh `SendARP` report the same IP with different MACs (rare race during DHCP reassignment), trust the cache. `LanScannerService.ProcessObservationAsync` will detect any genuine MAC change via its `GetByIpAsync` lookup against the previous scan's persisted row anyway — the in-scan merge only needs to be self-consistent.
- **Active probing preserved.** The cache walk is a passive READ of OS state Windows maintains as a side effect of normal traffic. It's not "passive discovery" in the ADR 009 §Discovery method sense (which referred to skipping active ARP probes entirely). Active `SendARP` still runs for cache misses — that's how we catch devices Windows hasn't seen recently.
- **Byte-array struct marshalling, not `fixed` buffers.** Using `[MarshalAs(UnmanagedType.ByValArray, SizeConst = N)]` byte[] fields for the `SOCKADDR_INET` (28 bytes) and `PhysicalAddress[32]` inline buffers in `MibIpNetRow2` works without requiring `unsafe` blocks. Each `Marshal.PtrToStructure` call allocates 2 byte arrays (28 + 32 bytes) per row — fine for a once-per-scan-tick call against ~10-50 cache entries.
- **Test-injection via constructor overload, not DI.** Production `ArpScanProbe` is constructed without args by DI; tests use the `internal` ctor to inject a probe delegate + deadline. Avoids registering a new probe-function abstraction in the cross-platform `Beholder.Core` for a Windows-only test seam.

943 → 959 tests (+16 in `ArpScanProbeTests`). One atomic commit.

### Phase 9.2.5 — mDNS + NetBIOS hostname resolution via raw UDP ✅

**Purpose:** Completes ADR 009's three-layer hostname-resolution ladder by adding mDNS (RFC 6762, multicast PTR query for the reverse-IP arpa name with the QU bit set per §5.4) and NetBIOS NBSTAT (RFC 1002 §4.2.17, unicast query for the wildcard `*` name) probes after the existing ARP discovery pass. Both implemented as pure managed C# via `System.Net.Sockets.UdpClient` — no new P/Invoke surface, unlike Phases 9.1/9.2/9.2.1's `iphlpapi.dll` work. The exploration agent confirmed both standards are well-documented with no undocumented API unknowns, so we skipped the probe-then-impl pattern from ADRs 004/006 and went direct to production.

**Core (Beholder.Core/):**
- **`IHostnameProbe.cs`** — single-method interface for "resolve one IP to one hostname via one protocol." Two Windows impls; future Linux impls reuse unchanged.
- **`Discovery/MdnsPacketBuilder.cs`** + **`MdnsPacketParser.cs`** — DNS-wire-format build for PTR query of `<reversed-ip>.in-addr.arpa` with `QCLASS = 0x8001` (IN + QU bit); parser handles RFC 1035 §4.1.4 name-pointer compression with forward-pointer guard (loop prevention).
- **`Discovery/NetbiosPacketBuilder.cs`** + **`NetbiosPacketParser.cs`** — NBSTAT query build with the bizarre NetBIOS first-level name encoding (each byte → two `A..P` characters; wildcard `*` becomes `CKAAAAA...A`). Parser extracts the workstation name (suffix `0x00`, unique-type bit clear) from the response's name list, strips trailing space-padding.
- All four packet classes mirror `Beholder.Core.Tls.TlsClientHelloParser` from ADR 006: `public static bool TryExtractX(ReadOnlySpan<byte>, out string?)` with exhaustive bounds-checks and `false`-on-malformed-no-exception contract. Pure / static / no I/O / unit-testable against hand-built RFC examples.

**Windows daemon (Beholder.Daemon.Windows/Scanner/):**
- **`MdnsHostnameProbe.cs`** — implements `IHostnameProbe` (`ProtocolName => "mDNS"`). Per `ResolveAsync` call: fresh `UdpClient` bound to `IPAddress.Any:0`, multicasts the query to `224.0.0.251:5353`, waits up to 1 s for unicast reply on the source port. `MulticastLoopback = false` so we don't receive our own query. Catches `SocketException` (network down → null), propagates `OperationCanceledException`.
- **`NetbiosHostnameProbe.cs`** — same UdpClient pattern but unicast to `<target-ip>:137`. NBSTAT replies natively unicast to the source port — no QU-bit equivalent needed.
- **`HostnameResolutionLadder.cs`** (~110 LOC) — orchestrator. `ResolveAllAsync(IEnumerable<IPAddress>, CancellationToken)` returns `Dictionary<ip-string, hostname>`. Internally: `Parallel.ForEachAsync` over IPs with `MaxDegreeOfParallelism = 32` (half of `ArpScanProbe`'s 64, since each IP runs up to two probes sequentially). Per-IP fall-through (first non-null wins). 60 s linked-CTS deadline with the deadline-expires-returns-partial filtered-catch pattern from 9.2.1. Test-injection ctor accepts a custom deadline so deadline-expiry tests run fast.

**Cross-platform daemon (Beholder.Daemon/):**
- **`ScannerOptions.cs`** — gains `EnableHostnameResolution = true` (default-on, opt-out matching `DnsOptions.EnableReverseDnsFallback` per ADR 005). Snapshot at startup; not hot-reloadable.
- **`Scanner/LanScannerService.cs`** — the periodic-tick log message now includes `(K with hostname)` suffix where K is the count of observations with a non-null Hostname. Service didn't otherwise need changes; the hostname resolution lives entirely inside the probe.

**Integration (`Beholder.Daemon.Windows/Scanner/WindowsLanDeviceProbe.cs`):**
- Gains a nullable `HostnameResolutionLadder?` constructor parameter (default null). After the existing cache + parallel-probe merge in `ScanAsync`, if the ladder is non-null AND the merged observation set is non-empty, runs the ladder over the IPs and patches each observation's Hostname from the result dictionary.
- Kill-switch is honored at DI registration in `Program.cs` (not inside the probe): `WindowsLanDeviceProbe`'s factory lambda reads `IOptionsMonitor<ScannerOptions>.CurrentValue.EnableHostnameResolution` and passes either the ladder or null. This keeps `WindowsLanDeviceProbe` free of any `ScannerOptions` dependency — avoiding the circular reference (`Beholder.Daemon.Windows` cannot reference `Beholder.Daemon`).

**Tests added (~37):**
- **`MdnsPacketParserTests.cs`** (~14 tests): build correctness (header fields, reverse-IP encoding, QU bit), parse correctness (valid response, wrong TID, no answers, truncated, empty buffer, name-pointer compression, non-PTR answers, forward-pointer rejection).
- **`NetbiosPacketParserTests.cs`** (~13 tests): build correctness (fixed 50-byte length, header, wildcard first-level encoding, QTYPE/QCLASS), parse correctness (valid response with workstation name, space-padding strip, only-group-names rejection, group-first-unique-second ordering, non-workstation suffix skip, wrong TID, truncated, empty buffer, no answers).
- **`HostnameResolutionLadderTests.cs`** (~10 tests): null-probes guard, empty input, first-probe-wins, fall-through-to-second, all-null-no-entry-in-result, parallel-dispatch (32 × 100 ms < 1.5 s wall-clock), deadline-expires-returns-partial, outer-cancellation-throws, probe-throws-logs-and-continues, partial-batch-only-responders-in-result.
- **`FakeHostnameProbe.cs`** test double — Responder hook + call counter. Mirrors `FakeLanDeviceProbe` shape.

**No tests for `MdnsHostnameProbe` / `NetbiosHostnameProbe`** themselves (real socket I/O). Manual runbook covers them.

**No changes to `LanScannerServiceTests`** — the service's interface contract didn't change; observations flow through with hostname populated or not. All 11 existing tests still pass.

**Docs:**
- **`ARCHITECTURE.md`** — "LAN Discovery (cold-path)" subsection extended with the hostname-resolution pass in the pipeline diagram. Explicitly notes mDNS multicast TTL=1 + NetBIOS unicast-to-LAN-IP — neither leaves the subnet. New paragraph documenting the "pure managed UDP, no new P/Invoke" pattern and the link to ADR 006's `TlsClientHelloParser` precedent.
- **`docs/manual-tests/lan-scanner.md`** — expected scan-result log gains `(K with hostname)` suffix; new SQLite query example shows non-null hostname column; new Known-non-issue covers the typical "random-MAC phones don't respond" case.
- **`phases.md`** (this file) — header refresh + §1 status patch + this Phase 9.2.5 entry + §3 lessons (3 bullets).
- **`README.md`** — status line bump (959 → 996); Working Today section gains a Phase 9.2.5 mention; appsettings.json sample gains the `EnableHostnameResolution` field.

**Out of scope (deferred):**
- **Reverse-DNS PTR fallback for LAN IPs** (ADR 009's third layer of the ladder) — PTR records on home routers are rare; explicit 9.2.6 polish if real-world data shows it's worth wiring up the existing `ReverseDnsFallbackCache` from ADR 005 for LAN scan use.
- **mDNS service-discovery browsing** (`_workstation._tcp.local`, `_airplay._tcp.local`, etc.) — higher hit rate but materially more parsing surface; future polish if PTR-only hit rate is too low on real-world LANs.
- **mDNS continuous listener** binding 5353 and passively absorbing announcements — different architecture (continuous vs probe-per-scan). Deferred.
- **NetBIOS via `netapi32.dll` `Netbios()` NCB API** — raw UDP sends the same wire packet without depending on NetBIOS-over-TCP/IP being enabled on the host.
- **LLMNR (RFC 4795)** — Microsoft's near-mDNS protocol on 224.0.0.252:5355. Adoption is fading.
- **Per-protocol enable/disable knobs** (`EnableMdns` / `EnableNetbios` separately) — single master switch for v1; per-protocol knobs are 9.2.6 polish.
- **Hostname caching across scans** — every scan re-queries every device. 5-min scan cadence + per-IP <1s probe latency means the cache-invalidation complexity isn't justified yet.

**Design decisions:**
- **Raw UDP via `UdpClient`, not Windows `DnsServiceBrowse` API.** The Windows mDNS API surface (`DnsServiceBrowse`, `DnsQuery_W + DNS_QUERY_USE_MULTICAST_ONLY`) is callback-based with internal timeout behavior similar to `SendARP` (the 9.2.1 surprise). Raw UDP gives us full control over per-probe timeouts (1 s), is fewer LOC, and is cross-platform-friendly. No third-party libraries needed.
- **Kill-switch lives at DI registration, not inside the probe.** `WindowsLanDeviceProbe` can't reference `ScannerOptions` (circular project dep: `Beholder.Daemon.Windows` ← / → `Beholder.Daemon`). The factory lambda in `Program.cs` reads the option once at construction time and passes either a real ladder or null. Snapshot-at-startup matches the ADR 005 precedent ("not hot-reloadable"). Cleaner than plumbing the bool through the `ILanDeviceProbe` interface.
- **mDNS PTR for reverse-IP, not service-discovery browsing.** Simplest mDNS query type. Apple devices (iPhones, Macs) respond; many Linux/Avahi setups respond; recent Android with mDNS responder responds. Real-world hit rate to be measured during smoke test; if low, 9.2.6 can add service-discovery browse (`_services._dns-sd._udp.local`).
- **mDNS QU bit + ephemeral source port avoids competing with Bonjour for port 5353.** Many Windows machines have the Bonjour service installed (bundled with iTunes, Adobe Acrobat, etc.) which permanently binds port 5353. Our UdpClient binds an ephemeral port instead and sets the QU bit so responders unicast the reply to that ephemeral source port per RFC 6762 §5.4 — no port conflict, no privilege requirement beyond what the daemon already has.

**Files NOT touched:** UI side (no UI; Scanner tab UI is 9.4), `Beholder.Protocol` (no proto changes; RPC additions are 9.3), `Beholder.Daemon.GeoIp` / `Beholder.Daemon.Uplink` (unrelated). Existing `LanScannerServiceTests` (11 tests) pass unchanged.

959 → 996 tests (+37 across `MdnsPacketParserTests` + `NetbiosPacketParserTests` + `HostnameResolutionLadderTests`). One atomic commit.

---

### Phase 9.2.6 — mDNS service-discovery (DNS-SD) browsing ✅

**Purpose:** Replace 9.2.5's per-IP reverse-PTR + NetBIOS ladder as the primary hostname-resolution path with the DNS-Based Service Discovery (RFC 6763) browse pattern that real-world LAN-discovery tools (Fing, GlassWire Things tab, `dns-sd -B`, `avahi-browse`) actually use. 9.2.5's smoke test on the user's real LAN returned **0/9 hostnames** despite 996 passing unit tests — empirical diagnosis (manual PowerShell mDNS PTR query against the same LAN also returned 0 replies) confirmed the protocol was correct, but the LAN's responders just don't answer reverse-IP PTR queries. Most Bonjour-style responders advertise *services* via `_<service>._<proto>.local` PTR records and ignore reverse-IP queries. 9.2.6 adds the SD browse pattern to actually populate hostnames on a typical LAN.

**Core (Beholder.Core/Discovery/):**
- **`DnsNameDecoder.cs`** (NEW, ~140 LOC, `internal static`) — extracted shared DNS-name decoding logic per DRY. Three methods: `TrySkipName` (advances past an encoded name; used when we don't need the value), `TryReadName` (decodes the name into a string, with a `strict` flag — `true` = DNS-safe chars only, `false` = printable ASCII to allow space-containing service-instance names), `TryReadFirstLabel` (decodes just the leftmost label, follows one compression-pointer hop max). Forward-pointer guard (target offset must be < current cursor; defeats malformed packets crafted to loop). Hard ceiling on compression-pointer hops (16) for adversarial inputs. The existing 9.2.5 `MdnsPacketParser` was refactored to use this helper; all 14 pre-existing `MdnsPacketParserTests` continue to pass unchanged.
- **`MdnsServiceDiscoveryPacketBuilder.cs`** (NEW, ~95 LOC) — `public static byte[] BuildServiceTypeQuery(string serviceType, ushort transactionId)`. Builds a DNS-format PTR query for `_<service>._<proto>.local` names with `QCLASS = 0x8001` (IN class + QU bit per RFC 6762 §5.4). Service-type validation: must match `_<service>._<proto>.local` shape, protocol must be `_tcp` or `_udp`, label length ≤ 63 bytes (RFC 1035 §2.3.4) — `ArgumentException` on malformed input (programmer error, not adversarial).
- **`MdnsServiceDiscoveryParser.cs`** (NEW, ~160 LOC) — `public static bool TryExtractHostname(ReadOnlySpan<byte> packet, IReadOnlySet<ushort> expectedTransactionIds, out string? hostname)`. Walks all records (answers + authority + additional) collecting first SRV target / A record owner-name / PTR instance leftmost-label seen. Returns the best one per priority (SRV > A > PTR-label). Trailing `.local` stripped. Short-circuits once an SRV target is found — no fallback outranks it. Defensive bounds-checking at every length field, no allocation on failure, no exception on malformed input — same posture as `TlsClientHelloParser` per ADR 006.

**Windows daemon (Beholder.Daemon.Windows/Scanner/):**
- **`MdnsServiceDiscoveryProbe.cs`** (NEW, ~170 LOC) — single `BrowseAsync(CancellationToken)` method. Fresh `UdpClient` bound to an ephemeral port (avoids competing with Bonjour for 5353). Sends one PTR query per service type for 12 curated well-known types: `_workstation`, `_smb`, `_airplay`, `_googlecast`, `_printer`, `_ipp`, `_raop`, `_hap`, `_spotify-connect`, `_hue`, `_ssh`, `_companion-link` (all `._tcp.local`). Then a 3-second receive loop reads inbound replies and parses each. First non-empty hostname per source IP wins. Returns `Dictionary<ip-string, hostname>`. Failures (`SocketException` on send / receive) collapse to whatever partial results were collected. Test-only ctor accepts a custom deadline.

**Integration (`Beholder.Daemon.Windows/Scanner/WindowsLanDeviceProbe.cs`):**
- Gains a nullable `MdnsServiceDiscoveryProbe?` constructor parameter (default `null`). After the existing ARP cache + parallel-probe merge, runs SD-browse **first**, patches `Hostname` for any matching IPs, then runs the 9.2.5 ladder over IPs whose Hostname is still empty. Same `EnableHostnameResolution` master switch gates both passes uniformly.
- Debug log line now reports two hit counts: `hostnames-via-SD {SdHits}` and `hostnames-via-ladder {LadderHits}` so a future verification phase can see the split.

**Cross-platform daemon (Beholder.Daemon/):**
- **`Program.cs`** — gains `AddSingleton<MdnsServiceDiscoveryProbe>()`. The `ILanDeviceProbe` factory lambda now passes either the SD probe or `null` based on `EnableHostnameResolution`, matching the existing pattern for `HostnameResolutionLadder`.

**Tests added (+33):**
- **`MdnsServiceDiscoveryPacketBuilderTests.cs`** (~15 tests, ~150 LOC): header field correctness, QNAME label encoding (`_airplay._tcp.local` → `[8]_airplay[4]_tcp[5]local[0]`), QU bit on QCLASS, QTYPE = PTR, TID propagation invariant, `_udp` protocol acceptance, total length math, malformed-input rejection (null / empty / whitespace / missing-proto / missing-.local / wrong protocol / no underscore / label > 63 bytes).
- **`MdnsServiceDiscoveryParserTests.cs`** (~18 tests, ~370 LOC): PTR + SRV + A returns SRV target, PTR + SRV prefers SRV, PTR + A falls back to A owner, PTR-only falls back to instance leftmost label, `\032`-equivalent space-in-instance accepted as printable ASCII, `.local` strip, non-`.local` suffixes unchanged, multi-TID-set match, wrong TID rejected, empty / truncated buffer rejected, no records rejected, TXT-only records skipped, null expected-set throws, `RDLENGTH` overflow rejected, truncated question section rejected, DNS name compression in SRV target resolved, SRV-in-additional-only (no answer-section PTR) still resolves. Fluent `MdnsResponseBuilder` test helper composes multi-section response packets.

**No tests for `MdnsServiceDiscoveryProbe`** itself (real socket I/O — same precedent as `MdnsHostnameProbe` / `NetbiosHostnameProbe` from 9.2.5).

**No changes to existing tests** — `WindowsLanDeviceProbeTests` doesn't exist (orchestrator covered by integration smoke); `LanScannerServiceTests` (11) and `HostnameResolutionLadderTests` (10) continue to pass unchanged because the SD probe lives behind `ILanDeviceProbe`. The 14 existing `MdnsPacketParserTests` also continue to pass after the `DnsNameDecoder` refactor (the parser's behaviour is unchanged; only the internal helper organisation moved).

**Docs:**
- **`ARCHITECTURE.md`** — "LAN Discovery (cold-path)" pipeline diagram extended with the SD-browse pass before the per-IP ladder. New paragraph documenting the broadcast-shape SD probe + per-IP ladder fallback split, plus the `DnsNameDecoder` DRY extraction.
- **`docs/manual-tests/lan-scanner.md`** — expected hit-rate guidance updated: K (hostname count) should now be non-zero on most modern LANs; troubleshooting note added for the rare K=0 case.
- **`phases.md`** (this file) — header refresh + §1 status patch + this Phase 9.2.6 entry + §3 lessons (2 bullets).
- **`README.md`** — status line bump (996 → 1029); Working Today section gains a Phase 9.2.6 mention.

**Out of scope (deferred):**
- **`_services._dns-sd._udp.local` meta-query** for auto-discovering service types — adds a round-trip and many responders don't support it well. Curated list of 12 popular types is the v1 approach; meta-query is 9.2.7 polish if hit-rate is still low.
- **Joining the multicast group on 5353 with `ReuseAddress`** to absorb multicast-shape replies — would let us catch responders that ignore QU. The current QU-bit + ephemeral-port approach should catch most modern responders; this is 9.2.7 polish.
- **Passive listening for ambient mDNS announcements** — bind 5353 and absorb device announcements as they happen. Different architecture (continuous vs probe-per-scan); defer.
- **DHCP option 12 sniffing** — capture client hostnames from DHCP DISCOVER/REQUEST. Higher hit rate on devices that don't advertise services but DO send DHCP; requires raw socket privilege. Defer.
- **Service-type customization via `ScannerOptions`** — let users add their own service types. Fixed list for v1; defer the configurability.
- **TXT record extraction** — TXT carries service metadata (AirPlay model, AppleTV serial number). Useful for UI display, not for hostname resolution. Out of scope.

**Design decisions:**
- **Extract shared DNS-name decoding into `DnsNameDecoder` per DRY.** When writing the SD parser I recognized the name-compression / pointer-following logic was duplicated from `MdnsPacketParser`. PRINCIPLES.md "extract duplicated logic to the lowest common ancestor" applies — moved to a shared `internal static` helper, refactored both parsers to use it. The `strict` flag captures the only behavioural difference: reverse-IP PTR names use DNS-safe chars only, service-instance names allow printable ASCII for spaces.
- **Broadcast-shape probe + per-IP ladder fallback is the right architecture for hostname resolution.** 9.2.5's per-IP shape (one query per IP, parallel) is what you want for protocols where the device must be addressed directly (NetBIOS NBSTAT unicasts to `<ip>:137`). 9.2.6's broadcast shape (one query, many devices respond) is what you want for service-discovery (one multicast → all advertisers reply). Different shapes need different orchestration. Running SD first (broadcast, ~3 s for the whole LAN) and the ladder second (per-IP, only for residue) gets the best of both — bulk hostname population for the SD-aware majority, per-IP coverage for the holdouts.
- **Curated service-type list, not meta-query.** The DNS-SD meta-query `_services._dns-sd._udp.local` SHOULD return the set of service types being advertised on the LAN, but real-world support is spotty (many responders ignore it). A curated list of 12 popular service types covers the high-hit-rate categories with a known-bounded ~36-byte send budget. If hit-rate is still low after 9.2.6 the meta-query approach can ride along as 9.2.7.

**Files NOT touched:** `Beholder.Protocol` (no proto changes; RPC additions are 9.3), `Beholder.Daemon.GeoIp` / `Beholder.Daemon.Uplink` (unrelated), `Beholder.Ui` (Scanner tab UI is 9.4). `LanScannerServiceTests` (11) + `HostnameResolutionLadderTests` (10) + `MdnsPacketParserTests` (14) + `NetbiosPacketParserTests` (13) all pass unchanged.

996 → 1029 tests (+33 across `MdnsServiceDiscoveryPacketBuilderTests` + `MdnsServiceDiscoveryParserTests`). One atomic commit.

---

### Phase 9.3 — Scanner RPCs + LAN-device stream events ✅

**Purpose:** Open the LAN scanner's daemon-side state to the UI via the IPC surface. After Phase 9.2.6, devices were being discovered, persisted to `lan_device`, and chain-audited as `LanDeviceFirstSeen` / `LanDeviceMacChanged` events — but there was no IPC for the UI to read those rows or receive those events live. Phase 9.3 closes the IPC contract so Phase 9.4 has a stable RPC + stream surface to build the Scanner tab against. The phase also closes an implicit gap from 9.2: the scanner had been writing to the chain but skipping the broadcast leg every other mutable-event kind (counter batches, firewall rule changes, alerts) used.

**Proto (Beholder.Protocol/Protos/beholder_local.proto):**
- Two new RPCs on `BeholderLocal`: `ListLanDevices` (paged historical read with `seen_since` + `limit`) and `TriggerScan` (on-demand scan returning structured success/message/observation-count).
- Two new `DaemonEvent` oneof variants (field 4 + field 5): `LanDeviceFirstSeenEvent` and `LanDeviceMacChangedEvent`. Different shapes from `FirewallRuleChange` (which uses an inner `ChangeKind` enum because all three rule events carry the same `FirewallRule` payload) because LAN first-seen and mac-changed carry different data — `MacChanged` needs the `previous_mac` field, FirstSeen doesn't.
- Five new messages: `LanDevice` (shared between `ListLanDevicesResponse` and both stream events — DRY), `ListLanDevicesRequest` / `Response`, `TriggerScanRequest` / `Response`, and the two stream-event envelopes.
- Proto3 default strings used for nullable `vendor` / `hostname` (empty-string-as-null), matching `Alert.summary` precedent.

**Core (Beholder.Protocol/):**
- `ProtocolConverters` gains `LanDevice ↔ proto LanDevice` mappers with the empty-string ↔ null convention.

**Daemon (Beholder.Daemon/Pipeline/):**
- `BroadcastService` gains `BroadcastLanDeviceFirstSeen(LanDevice)` and `BroadcastLanDeviceMacChanged(string previousMac, LanDevice)`. The four existing fan-out call sites' inlined `foreach (var (_, channel) in _subscribers) channel.Writer.TryWrite(...)` block is extracted to a private `FanOut(DaemonEvent)` helper so the new methods (and any future broadcast paths) reuse it.

**Daemon (Beholder.Daemon/Scanner/):**
- `LanScannerService` gains a `BroadcastService` ctor dep. `ProcessObservationAsync` now fires `BroadcastLanDeviceFirstSeen` / `BroadcastLanDeviceMacChanged` after each successful chain write, wrapped in best-effort try/catch (`TryBroadcastLanDevice*` helpers mirror the existing `TryEmitChainEventAsync` resilience pattern).
- New public `RunOnceManuallyAsync(CancellationToken)` entry point for `TriggerScan` to call. Throws `InvalidOperationException` when the probe is null (Linux daemon) so the RPC can convert to `success=false` with a "scanner inactive" message.
- New `SemaphoreSlim _scanGate` field serialises the timer-driven `SafeRunOnceAsync` and the RPC-driven `RunOnceManuallyAsync` — at most one scan runs at a time, and concurrent `TriggerScan` calls queue cleanly.

**Daemon (Beholder.Daemon/Grpc/):**
- `BeholderLocalService` gains `ILanDeviceStore` + `LanScannerService` ctor deps and two new RPC handler methods.
- `ListLanDevices` clamps `Limit` to `[1, 1000]` with a 200 default (`DefaultLanDeviceListLimit` / `MaxLanDeviceListLimit` private constants). `Limit < 0` throws `RpcException(InvalidArgument)`; everything else delegates to the shared `ExecuteQueryAsync` helper (same exception-classification path as the other read RPCs).
- `TriggerScan` returns `success=false` with a structured `message` for recoverable failures (scanner inactive, probe threw) and re-throws `OperationCanceledException` so gRPC surfaces `StatusCode.Cancelled` for client-side cancels. Mirrors `ApplyFirewallRule`'s soft-failure precedent.

**UI (Beholder.Ui/Services/):**
- `IDaemonClient` + `DaemonClient` gain `ListLanDevicesAsync` / `TriggerScanAsync` wrappers (canonical unary RPC shape).
- `DaemonStreamSubscriber` gains `LanDeviceFirstSeenReceived` + `LanDeviceMacChangedReceived` C# events; `DispatchEvent` adds two cases on `PayloadOneofCase`. The Scanner-tab `ViewModel` stub is intentionally untouched — Phase 9.4 wires it up against this surface.

**Tests added (+31):**
- **`ListLanDevicesRpcTests.cs`** (NEW, 8 tests): empty store, last-seen-DESC ordering, `SeenSince` filter, default limit (200), cap clamp (1000), explicit limit honored, negative-limit rejection, store-throws maps to Internal, null vendor/hostname maps to empty string on the wire.
- **`TriggerScanRpcTests.cs`** (NEW, 5 tests): happy path returns devices-observed count, no-probe-registered returns `success=false`, probe-throws returns `success=false`, outer cancellation propagates as `OperationCanceled` (not converted to success=false), concurrent calls serialise via `_scanGate`.
- **`LanScannerServiceTests.cs`** (+5 tests): FirstSeen broadcasts, MacChanged broadcasts with `previous_mac`, known-MAC silent upsert no-broadcast, no-subscribers fan-out doesn't break chain+store, `RunOnceManuallyAsync` throws when probe is null.
- **`BroadcastServiceTests.cs`** (+5 tests): null-device guards, theory-style null/empty `previous_mac` guard (using `ThrowsAny<ArgumentException>` to cover both `ArgumentNullException` and `ArgumentException`), fan-out parity for both new methods.
- **`DaemonStreamSubscriberTests.cs`** (+2 tests): dispatch for both new oneof variants raises the matching C# event.
- **`ProtocolConvertersTests.cs`** (+4 tests): all-fields-preserved, null vendor/hostname → empty string, round-trip preserving null semantics, round-trip with non-null values.

Two new test doubles in `Beholder.Tests/TestDoubles/`: `FakeLanDeviceStore` (in-memory `ILanDeviceStore`), `FakeFirewallRuleStore` + `FakeAlertStore` (in-memory stubs so unrelated RPC tests can satisfy the new `BeholderLocalService` ctor deps without spinning up SQLite stores), and `TestServiceFactory.CreateInactiveLanScannerService(...)` factory so the 9 existing RPC test files could each add the two new ctor args in one line.

**Docs:**
- `docs/ARCHITECTURE.md` — "IPC Protocol" section: BeholderLocal stub expanded to all 17 RPCs (was 9 in the snippet — phase-by-phase additions had not been folded back into the doc), the `DaemonEvent` oneof note bumped to 5 variants, new paragraph explaining the chain-write+broadcast invariant + the `ListLanDevices` server-clamped limit + `TriggerScan` structured failure shape.
- `docs/phases.md` — header date + test count refresh, §1 status appended with the IPC additions and the corrected RPC count, this Phase 9.3 entry, §3 lessons (2 bullets).
- `README.md` — status line bump (1029 → 1060 tests); Working Today section notes scanner IPC surface ships in 9.3.

**Files NOT touched:** `Beholder.Protocol/Protos/beholder_uplink.proto` (uplink RPCs unchanged), `Beholder.Daemon.GeoIp` / `Beholder.Daemon.Uplink` (unrelated), `Beholder.Ui/ViewModels/ScannerTabViewModel.cs` (stub stays — 9.4 territory), `docs/manual-tests/lan-scanner.md` (deferred — no clean grpcurl-over-named-pipe story to document; will revisit when 9.4 lands the UI).

1029 → 1060 tests (+31). One atomic commit.

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

### Phase 6.4/6.5 polish addendum

- **Pill state should reflect effective connectivity, not stored rule state.** A user reading "ALLOW" while their app is silently blocked by a stale Windows Firewall rule is worse than no UI at all. The pill's truth source is the union of "is there a Beholder block rule?" + "is the OS firewall blocking this path?" — derived, not stored.
- **`Reclassify` via `Clear+Add` recycles every container.** Avalonia's `ItemsControl` handles `Reset` (the event raised by `Clear+Add`) by recycling all containers, which made pill containers re-template mid-click and swallowed the click. Fix: `Insert/Remove/Move` ops keep stable rows' container instances mounted across 1Hz daemon ticks. Hover-during-tick scenarios now survive.
- **HResult-based COMException catch over type-based.** `0x80070002 ELEMENT_NOT_FOUND` from `INetFwRules.Add` was being treated as an unhandled exception because the catch was on `COMException` type rather than the HResult — the not-found branch is the EXPECTED path on first-add and needed explicit catch. Always switch on HResult for COM interop.

### Phase 6.6/6.7

- **Optimistic UI + revert-on-failure for any RPC-backed user action.** `MarkAlertRead` flips `IsRead` immediately and fires the RPC in the background; failure reverts. Same pattern for the BLOCK/UNBLOCK toggle. The user gets instant feedback; the wire-side failure surfaces as an `ErrorBanner` without blocking the UI.
- **Reflection-based event raise is an acceptable test seam.** `DaemonStreamSubscriber.AlertReceived` is a public event with `+=`/`-=` only; tests can't `Invoke` it directly. A `RaiseAlertReceived` reflection helper (mirrors the `RaiseProcessStatesUpdated` pattern in `FirewallTabViewModelTests.Reclassify.cs`) drives the live-update path without a real gRPC stream. Worth the type-coupling cost — the alternative (exposing an internal raiser) leaks state to production for test convenience.
- **Method-exists + tests-pass isn't enough; production wiring needs explicit verification.** Phase 6.6 added `AlertsTab.ActivateAsync` but never called it from production — the Alerts list was being populated only by live broadcasts, so historic alerts at startup were invisible. Manual smoke-test caught the miss. Lesson: the composition root must be reviewed alongside any new VM lifecycle method.
- **Plan-mode discipline is non-negotiable for any non-trivial fix, even one-liners.** Soft-reverted an unauthorized ActivateAsync wiring fix (`ce33139` → `fb7ef28`) and re-applied it via a proper plan as `a7e881a`. The plan-mode workflow exists to catch missed-context errors that "trivial fix" intuition doesn't.

### Phase 6.7 polish (the scroll-into-view + cold-start race saga)

- **`ContainerFromItem(item)?.BringIntoView()` silently no-ops for unrealized `VirtualizingStackPanel` rows.** `ContainerFromItem` returns null until the row is materialized; the null-conditional then skips the call entirely. The correct Avalonia 12 API is `ItemsControl.ScrollIntoView(item)` (verified in `Avalonia.Controls.xml:11392`) which delegates to `VirtualizingPanel.ScrollIntoView(int index)` — that method *forces* container realization before bubbling `BringIntoView` up to the outer `ScrollViewer`.
- **`bool _activated` idempotency in async lazy-load lies to concurrent callers.** When `OnActiveTabChanged` fires `_ = _vm.ActivateAsync()` (fire-and-forget) right before another caller awaits its own `ActivateAsync`, the second call sees `_activated = true` and returns a synthetic completed Task while the first call is still loading. The fix: store the in-flight `Task?` instead so all callers await the same underlying work. Same bug existed in BOTH `FirewallTabViewModel` and `AlertsTabViewModel` (the second one resolved in this checkpoint's cleanup commit `7299702`).
- **Approved plans need smoke-test before commit, not after.** Committed the original scroll-into-view fix (`816763f`) without manual UI verification because unit tests passed; user smoke-test then surfaced two real bugs (wrong API + cold-start race). Subsequent fix commits (`2cb8753`) explicitly gated commit on user smoke-test confirmation. The "build clean + tests green" bar is a necessary but not sufficient check.

### Phase 6.8

- **Multi-targeting cost is non-local.** `Microsoft.Toolkit.Uwp.Notifications` requires the Windows TFM. Pulling it forced conditional TFMs in `Beholder.Ui.csproj` AND `Beholder.Tests.csproj` (`net10.0-windows10.0.17763.0` on Windows builds, plain `net10.0` on Linux). Any consumer of a Windows-TFM consuming PackageReference must match TFM at the consuming side too — there's no "this reference is conditional" syntax. MSBuild *does* have `<ItemGroup Condition="...">` for the reference itself, which is what we use to keep the package off Linux builds; the TFM still has to flip in lockstep.
- **NuGet vulnerability suppressions need a "why this is safe" comment.** `System.Drawing.Common` 4.7.0 (transitive of the toolkit) has a documented vuln; we suppress because the toast-image surface is unreachable in our code. The suppression in `Beholder.Ui.csproj` carries that justification inline so a future audit doesn't have to re-derive it.
- **Mirror-the-precedent intuition needs an LOC sanity check.** Phase 6.8's original ship split off `Beholder.Ui.Windows` to mirror `Beholder.Daemon.Windows`. The daemon split is right; the UI split was wrong, and the checkpoint review caught it. The trigger for "separate platform project" is the size of the platform delta, not "we already do it on the other side." Daemon-side delta = thousands of LOC across multiple OS subsystems (ETW, WFP, Authenticode); UI-side delta = one notification service (60 LOC). [ADR 008](decisions/008-ui-single-project-policy.md) records the trigger threshold (~500 LOC of platform code, divergent UX, or multi-package dependency) that would justify re-splitting.

### Phase 6.9

- **Reusable controls pay back at scale.** Five inline error-banner sites collapsed into one `ErrorBanner` UserControl + five 4-line callsites. Net code reduction was modest, but the consistency win was large — the dismiss affordance and severity-token usage are now uniform across all five tabs.
- **Auto-clear-on-action-entry is the right UX pattern.** When the user retries the failing action, the stale banner clears automatically — no need to dismiss first. The X is for "I don't want to retry; just acknowledge and move on." Two paths to clearing the banner, neither one mandatory.
- **New component pattern? §5 doc entry in the same commit.** UI_DESIGN.md §9 rule #5 enforces this. Phase 6.9 landed §5.10 ErrorBanner spec + the implementation atomically.

### Phase 6.10

- **Mirror existing affordances rather than invent new ones.** Phase 6.10's `_fileExistsCheck` injection on `AlertsTabViewModel` is byte-for-byte identical to `FirewallTabViewModel`'s pattern. ~30 LOC to deliver the feature because the pattern was already designed and tested in 6.4.
- **Selection-driven > eager for sparse use cases.** Firewall tab does eager file-existence checks at activation (~80 rules, sub-millisecond on warm caches); Alerts can have 500 rows and selection is rare, so the on-demand check in `OnSelectedAlertChanged` is cheaper. Match the cost model to the access pattern.

### Phase 7

- **Hosted-service detector + facade emitter pattern.** Each detector knows when to alert; `AlertEmitter` knows how (chain-write + broadcast in one call). Saved 3 detectors from each duplicating both contracts. Adding a new alert kind is a new detector + a new `AlertKind` enum value, zero changes to existing detectors.
- **Source-compatible signature change via discarded return value.** `IEventStore.AppendAsync Task → Task<long>` was non-breaking because every existing caller discarded the return value. Whenever you can change a return type from `Task → Task<T>` without anyone needing to await the new T, the change is free at the wire.
- **Session-scoped fire-once-per-key dedup at the source.** `TrafficEngine.OnProcessFirstNetworkFlow` only fires once per unique `(path, displayName)` per process lifetime — the engine doesn't know about cross-restart history. Cross-session dedup is the registry's job. This split keeps the engine stateless across daemon restarts and makes both sides independently testable.

### Phase 7.5

- **Catalog-signed Windows binaries (notepad.exe et al.) have no embedded cert.** `AuthenticodeVerifier.Read` returns `null` when cert extraction fails — these binaries silently fall through to path-based dedup. The "validate then extract" path needs explicit handling for catalog signatures, can't assume embedded cert iff signed. Documented limitation per ADR 007 § Out of scope.
- **ADR-driven semantic shifts deserve their own ADR.** Changing NewProcess from "once per path" to "once per logical identity" is a behavior change visible to users (Discord no longer spams new alerts on auto-update). ADR 007 captures the rationale + alternatives + out-of-scope items so a future contributor doesn't try to "fix" it back to per-path.
- **`SignatureValidationStatus.Valid` required for identity dedup.** Untrusted/Expired/Revoked signatures don't qualify — those binaries fall through to path-based dedup so a spoofer can't suppress the alert via a self-signed cert. Tier 1 is "Microsoft trusts this signing chain"; weaker tiers are excluded by design.
- **Walk ancestors looking for a folder name matching ProductName.** Discord's install root is whatever AppData folder is named "Discord" (case-insensitive); same Discord under Program Files is a DIFFERENT install. The walk catches both shapes without hard-coding install-root heuristics per app.

### Checkpoint cleanup (post-2cb8753, this round)

- **When adding columns to a record/proto, audit every callsite that constructs it.** `BinaryHashMonitor.UpdateRegistryAsync` was the canonical example: passed only the pre-7.5 6 args to `new ProcessInfo`, so the 6 new identity columns silently became NULL on every hash check, breaking Phase 7.5's spoof-detection guarantee. The compiler couldn't catch it because all 6 new columns are optional with defaults. Fix: a regression test that asserts "the right side-effect happened on the registry" beats one that asserts "the function returned." `HashChange_PreservesIdentityColumns_OnRegistryUpdate` is the lock.
- **Field-default-on-omission silently breaks downstream contracts.** Any time you add an optional field/parameter to a widely-constructed type, search the codebase for the constructor and audit every caller. Even when the language makes the omission legal, the semantics may not be.

### Phase 8

- **Avalonia 12 has no offline map control today.** Surveyed LiveCharts2 (Av11 only), Mapsui (Av11 + external OSM tiles), XAML.MapControl.Avalonia (Av12 ✅ but tile-based), Codenizer.Avalonia.Map (generic canvas wrapper), Asv.Avalonia.Map (aviation). Every option either lacks Av12 support or requires outbound network for map tiles — incompatible with Beholder's "no outbound network by default" stance. Custom Canvas mirroring `TrafficChartControl` is the durable path. `Beholder.Ui.csproj`'s "LiveChartsCore removed: incompatible with Avalonia 12" comment is the precedent — Phase 8 confirmed it still holds.
- **Country boundary data can't be authored from scratch.** Borders are surveyed historical/political artifacts with ~50K vertices total at 110m resolution. Generating them algorithmically is impossible; hand-authoring would produce visibly wrong maps; LLM-generated coordinates would hallucinate. Bundling Natural Earth's CC0 public-domain dataset is the only realistic path — same shape as the existing `dbip-country-lite.mmdb` asset (a one-time download committed as a static asset, zero runtime network).
- **Trim the asset at dev-time, not runtime.** Natural Earth raw is 819 KB with 80+ properties per feature. A 30-line Python preprocessor (`Beholder.Ui/Assets/world-countries-110m.geojson` is the committed output) trimmed to `{iso_a2, name, geometry}` + 2-decimal coordinate precision → 170 KB, 79% reduction. The runtime loader stays simple (System.Text.Json over the asset); the trimmer ran once and its output is the asset.
- **Hit-test in geographic coordinates, not screen coordinates.** Unproject the screen point once per `PointerMoved`, then bounding-box prefilter + point-in-polygon ray-cast across countries in lat/lon. Same logic survives any projection change (Mercator / Equal Earth swaps the projection helper without touching the hit-tester). At 177 countries × 1–8 rings each, the whole pass is sub-frame.
- **Asset preprocessor `ISO_A2 = "-99"` policy: remap to "??" rather than drop.** Two Natural Earth features (Kosovo, Northern Cyprus) have no assigned ISO_A2. Dropping them produces visible holes in the ocean; remapping to the existing "Unknown" sentinel `??` renders them as gray and they never get a traffic hit (the daemon's MMDB doesn't produce `??` for real flows). Visual consistency without a special-case branch.
- **5 deliberate heatmap tokens, not opacity overlays of one base.** Per UI_DESIGN.md §9 rule #6: every effective on-screen color must be a token. Runtime opacity overlays of `AccentPrimary` would resolve to colors not in the token table, defeating the rule and making the future light theme harder (a reviewer would have to pick one base + figure out which alpha each stop got). Five separately-named tokens with their alphas baked into the color value (`#4000BCD4`, `#9900BCD4`, ...) keep the contract intact.
- **Test the brush selection logic, not the brush instances.** `HeatmapPalette.Resolve()` falls back to `Brushes.Gray` for every stop in headless tests (no Avalonia app = no theme dictionary), so testing the production ramp via reference-equality fails. Fix: expose an internal constructor so tests can build a palette with five distinct test brushes — the ramp logic is testable independently of the resource resolution. Same pattern would help any future custom control that depends on theme tokens.
- **`AssetLoader.Open(avares://...)` requires a running Avalonia application.** The project's "no headless Avalonia tests" pattern (per existing `TrafficChartControl`'s absent tests) means `WorldGeometryLoader.LoadOnce` can't be unit-tested against the real asset. Workaround: the loader exposes an `internal static Parse(Stream)` overload so all parsing logic IS unit-tested via `MemoryStream` fixtures. The asset → loader integration is deferred to manual smoke-test.
- **Extending an existing RPC with optional fields beats a new RPC when the new shape is structurally identical to the old.** Phase 8 polish needed a per-country top-N destinations fetch. Two options: (a) extend `GetProcessDestinations` with optional `country` + `limit` fields, or (b) add a new `GetTopDestinationsForCountry` RPC. Picked (a): proto3 scalar defaults preserve existing callers (the COLS view sends empty string + 0 limit = no-op), the same handler / fake / index / test infrastructure handles both modes, and the new RPC would have been ~95% the same SQL with a different signature. New RPC is the right call when the response shape differs (e.g., different aggregation key) or when the existing RPC's auth/throttling semantics shouldn't apply — neither here.
- **Opportunistic data silently degrades; mandatory data shows an ErrorBanner.** The top-3 destinations on hover are *augmentation*: the country name + bytes header is still useful without them, so RPC failure flips a `Failed` flag that the tooltip renders as a dim "destinations unavailable" caption — no global ErrorBanner. By contrast the parent `RefreshMapAsync` IS mandatory (the whole map shows zero without it) and DOES raise an ErrorBanner. The discriminator: "does the primary surface stay useful without this data?" If yes, silent-degrade with a visible-in-context indicator; if no, surface a banner. A regression test (`OnHoveredCountryChanged_RpcFails_SetsFailedFlagAndNoErrorBanner`) locks in the design so a future contributor doesn't "fix" it by adding a banner.
- **Five tooltip states needs five visually distinct treatments.** UI_QUALITY_STANDARDS §3.1 says "the loading state must be visually distinct from both the empty state and the populated state." Phase 8 polish's hover tooltip ended up with FIVE states (No-fetch-yet / Loading / Empty / Populated / Failed); each needs its own visible cue or the user can't tell "data on the way" from "no data" from "data here." Solution: a `DividerState` enum inside the renderer keys off four bool flags from the VM; the renderer's `Draw` method branches once on the enum and delegates to small per-state helpers. Cleaner than a chain of `if (loading) ... else if (failed) ...` and trivially extensible if a future state appears.

### Phase 9.1

- **Scoping ADR before code is non-negotiable for ambiguous phases.** Phase 9 sat unscoped on the roadmap for months with four plausible directions (port scan, CVE lookup, anomaly detection, LAN discovery) and four matching-but-incompatible designs. The ~2 hours spent writing ADR 009 before any code paid for itself by ruling out three directions with explicit rationale (port scan = reconnaissance, out of character; CVE = needs outbound daemon network, violates posture; anomaly detection = different concept altogether). Without the ADR, mid-implementation realization that "actually, port scanning crosses an ethical line" would have wasted a sub-phase of work.
- **Daemon-side embedded assets follow the GeoIP pattern, not the Phase 8 Avalonia pattern.** Two viable shapes for the OUI snapshot: (a) `<EmbeddedResource>` baked into the daemon assembly + `Assembly.GetManifestResourceStream` loader (the Avalonia analog used by the Phase 8 Natural Earth GeoJSON), or (b) side-by-side `data/oui.csv` copied via MSBuild `<Content>` (the GeoIP pattern). Picked (b) for three reasons: the OUI lookup is daemon-side (Avalonia resources are UI-side), the existing daemon precedent is the GeoIP MMDB which uses (b), and (b) lets a fresh `OuiFetcher` run drop in updated data without a daemon rebuild. The Phase 8 wording "embedded as a build-time asset" in the ADR loosely meant "shipped alongside the binary," not literally `<EmbeddedResource>` — picking the wrong pattern would have hurt operational ergonomics.
- **No dead-weight options classes.** First-draft plan registered `ScannerOptions` (with `ScanIntervalSeconds`) in 9.1 even though the only consumer (`LanScannerService`) doesn't land until 9.2. Doc-compliance audit caught this against CLAUDE.md §"No Dead Weight" and pushed it to 9.2. Same call for the `LanDeviceFirstSeen` / `LanDeviceMacChanged` `EventKind` constants — defined alongside the writer in 9.2, not in 9.1 with no writer. 9.1 only updates the ARCHITECTURE.md `event_log.kind` comment to forward-document the eventual taxonomy. Easier to recognize at plan-time than fix in code review.
- **No new alert kinds even when adding new chain-event kinds.** ADR 002 caps alerts at three. ADR 009 honors that by routing LAN-discovery events to the chain (audit purpose) but never to the Alerts tab (UX purpose). The two are separable: chain events are written by detectors and serve forensic queries; alerts are a UI surface with toast notifications and read/unread state. Mixing them would have re-introduced exactly the alert-fatigue problem ADR 002 was written to prevent (home LAN: phones reconnect, IoT power-blips, guests come and go → 20 alerts/day of zero signal value). The opt-in "notify me when a new device appears" toggle is deferred to Phase 13 Settings + a future ADR superseding 002.

### Phase 9.2

- **Cold-path scanning wants `PeriodicTimer`, not `Channel<T> + worker`.** Phase 9.2's `LanScannerService` deliberately uses `PeriodicTimer(TimeSpan, TimeProvider)` (the .NET 8+ ctor) rather than the bounded-channel + drain-worker pattern from `PktmonSniSource` (ADR 006) or `ReverseDnsFallbackCache` (ADR 005). The discriminator: continuous high-throughput streams (ETW events, DNS lookups) need backpressure machinery — drop oldest/write modes, single-reader semantics, periodic stats with drop counts. Bursty periodic scans (one full sweep every 5 minutes) don't: the work is naturally rate-limited and the per-cycle volume is bounded by subnet size. Using `Channel<T>` for cold-path work adds cognitive overhead for no measurable benefit. The right primitive matches the work's actual cadence, not "what the other hosted services use."
- **Probe-call-count is not "scan completed."** First-draft `LanScannerServiceTests` polled `FakeLanDeviceProbe.ScanCount` (incremented at the START of `ScanAsync`) as the signal that a scan had finished. That races the foreground assertion against the still-running `foreach (observation)` body in `RunOnceAsync`. Symptom: intermittent test failures asserting `eventStore.Appended.Single()` when the chain write hadn't completed yet. Fix: expose `LanScannerService.TotalScansCompleted` (incremented AFTER the foreach finishes), poll that instead. Lesson: a test-side "did the thing happen" signal must come from the SAME side of the awaited boundary as the side effects you're asserting on. For throwing-probe survival tests where the increment never happens because the exception aborts before it, the probe's invocation count IS the right signal (we're asserting on "did the probe get called," not "did the processing complete"). Two distinct helpers, two distinct invariants.
- **`ArgumentException.ThrowIfNullOrWhiteSpace(null)` throws `ArgumentNullException`, not `ArgumentException`.** `Assert.Throws<T>` is exact-match in xUnit v3, so guarding tests need to assert the more specific subclass. Fixed three failing tests by changing `Assert.Throws<ArgumentException>` → `Assert.Throws<ArgumentNullException>` for the null-input cases. The rule: when the input is specifically null (not empty/whitespace), the .NET helper picks the more specific exception type, and the test should mirror that.
- **Cross-project DI requires `public` types, even within the same solution.** First-draft `WindowsLanDeviceProbe` and `ArpScanProbe` were `internal sealed`. `Beholder.Daemon/Program.cs` couldn't resolve them in `AddSingleton<...>` because they live in `Beholder.Daemon.Windows` and that project's `InternalsVisibleTo` only allows `Beholder.Tests`. Existing precedent: `EtwFlowSource`, `WfpFirewallController`, etc. are all `public sealed` for the same reason. The DI container's type-load is reflection-based and respects accessibility; "the same solution" isn't the boundary, project visibility is.

### Phase 9.2.1

- **OS calls that "wait for a response" need bounded parallelism by default unless the per-call timeout is verifiably small.** The Phase 9.2 `ArpScanProbe` was structured around a 5 ms inter-probe `Task.Delay` because I'd reasoned the bottleneck would be probe-burst rate-limiting. The real bottleneck turned out to be `SendARP` itself: Windows internally holds the call for ~1 s per unresponsive IP. On a /24 with 224 unresponsive hosts that's ~4 minutes single-threaded — and the inter-probe `Task.Delay` is irrelevant noise on top. The lesson generalizes: any OS API documented as "waits for a response" should default to bounded parallelism (`Parallel.ForEachAsync(MaxDegreeOfParallelism = N)`) unless the per-call worst-case latency is documented AND small. The `SendARP` docs are silent on the unresponsive-IP timeout — the actual value is folklore. Trust empirical wall-clock, not docs, for "how long does this OS call take?"
- **`FakeLanDeviceProbe` test doubles cannot catch this class of defect.** All 11 `LanScannerServiceTests` passed against `FakeLanDeviceProbe` — which returns instantly because it's a test fake with no real network I/O. The performance bug was 100% inside the real probe stack, invisible to the service-level tests. The only way to surface it was to run the daemon against a real LAN. Manual smoke tests are a NECESSARY part of phase verification when the unit-test seam is above the OS-API boundary. Phase 9.2's commit message even noted "End-to-end smoke test of the scanner against a real LAN deferred to user verification" — that deferral surfaced the bug, exactly as intended. Don't skip manual smoke for "the unit tests pass."
- **Cache-first + fallback-probe is the right pattern for any "expensive enumeration" OS call.** ADR 004 established it for `DnsQuery_W` (preload from `DnsGetCacheDataTableEx` first, fall through to live ETW events). ADR 005 established it for hostname resolution (DNS cache + ETW first, reverse-DNS only for residue). ADR 006 added SNI capture as another layer of the same pattern. Phase 9.2.1 applies it to ARP. The general shape: read whatever the OS already knows for free, then actively probe only the residue. Lower latency AND lower load on whatever upstream service (DNS server, neighboring LAN devices) the active probe would otherwise hammer.
- **`OperationCanceledException` filtered by which CTS fired is the cleanest way to distinguish "user cancel" from "internal deadline."** `Parallel.ForEachAsync` throws `OperationCanceledException` when any registered `CancellationToken` fires — but you can't tell from the exception which one. Solution: hold references to both CTS (the user's external token and the internal deadline CTS), then use a `when` clause: `catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !userToken.IsCancellationRequested)`. The internal deadline expiry is converted to "return partial results"; the outer user cancel propagates. Avoids try/catch-and-rethrow ceremony and keeps the failure semantics explicit at the catch site.

### Phase 9.2.5

- **Raw UDP via `UdpClient` avoids the P/Invoke complexity tax for documented RFC standards.** Phases 9.1 / 9.2 / 9.2.1 all required substantial P/Invoke (iphlpapi.dll: `SendARP`, `GetIpNetTable2`, `FreeMibTable`) for OS-internal-state access. Phase 9.2.5 looked like it would similarly need P/Invoke for `DnsServiceBrowse` (mDNS) and `Netbios()` (NetBIOS NCB API) — but the actual *wire protocols* (RFC 6762 mDNS, RFC 1002 NetBIOS NBSTAT) are documented standards that we can implement with `UdpClient` + bytes-on-wire parsing. Zero new P/Invoke. The lesson generalizes: when the OS API exists primarily to wrap a documented network protocol, going direct to UDP is often simpler and gives finer control (per-probe timeouts, cancellation, observable failure modes) than the OS wrapper. P/Invoke pays off for accessing OS-internal state (ETW, ARP cache, Authenticode); it pays less for sending and receiving standardized packets.
- **mDNS QU bit + ephemeral source port avoids competing with Bonjour for port 5353.** Many Windows machines have the Bonjour service installed (bundled with iTunes, Adobe Acrobat, etc.) which permanently binds UDP port 5353. Naively trying to bind 5353 ourselves would fail with `SocketException(AddressInUse)`. The RFC 6762 §5.4 QU bit ("unicast response wanted") sidesteps this: we bind an ephemeral port, send the query with QU set, and responders unicast the reply to our ephemeral source port instead of multicasting it. No port conflict, no privilege escalation required, and we get one-to-one query/response pairing as a bonus (instead of having to filter our own multicast replies out of the noise). Generalizes: link-local-multicast protocols designed with unicast-response provisions let us be a polite peer without needing exclusive ownership of the well-known port.
- **Avoid circular project references by keeping kill-switches at the DI boundary, not in the service.** `WindowsLanDeviceProbe` (in `Beholder.Daemon.Windows`) initially needed `ScannerOptions` (in `Beholder.Daemon`) for the hostname-resolution kill-switch — but that creates a circular project reference since `Beholder.Daemon` already references `Beholder.Daemon.Windows`. Fix: keep the option-reading at the DI registration site in `Program.cs`. The factory lambda reads `IOptionsMonitor<ScannerOptions>.CurrentValue.EnableHostnameResolution` once at construction and passes either a real `HostnameResolutionLadder` or `null` to `WindowsLanDeviceProbe`. The probe just checks `if (_ladder is not null) ...`. Avoids the circular ref, keeps the probe's dependencies minimal, and matches the "snapshot-at-startup, not hot-reloadable" semantics of similar options (`DnsOptions.EnableReverseDnsFallback` per ADR 005).
- **Defensive bounds-checked packet parsers in Core are a reusable pattern.** Phase 8/ADR 006 established `TlsClientHelloParser` with the `public static bool TryExtractX(ReadOnlySpan<byte>, out string?)` shape: exhaustive bounds-check at every length field, no allocation on failure, no exception on malformed input. Phase 9.2.5 mirrors it line-for-line for `MdnsPacketParser` and `NetbiosPacketParser`. The pattern works because it's restating the same security/robustness invariant — "anything from the wire is potentially adversarial; the parser is the trust boundary." Future protocol parsers (LLDP, CDP, SSDP, mDNS service-discovery in 9.2.6 if shipped, IPv6 neighbor discovery if Phase 9 expands) should also use this shape. Putting them in `Beholder.Core/` with no platform deps means future Linux scanners reuse them unchanged.

### Phase 9.2.6

- **Reverse-PTR mDNS misses most modern devices because they advertise services, not reverse-IP records — service-discovery browsing is the protocol pattern real-world tools use.** 9.2.5 shipped per-IP reverse-IP-PTR + NetBIOS hostname probes with 996 passing unit tests. The first smoke test on a real LAN returned **0/9 hostnames**. Manual PowerShell mDNS PTR query against the same LAN also returned 0 replies — the protocol implementation was correct, the LAN's responders just don't answer reverse-IP PTR queries. The actual modern idiom is DNS-Based Service Discovery (RFC 6763): query `_<service>._<proto>.local` and devices that advertise that service respond with PTR + SRV + A records. Real-world tools (Fing, GlassWire Things tab, `dns-sd -B`, `avahi-browse`) all use the SD browse pattern. The lesson is methodological: when an implementation passes its unit tests but field-fails everywhere, the bug is almost always in the *choice of protocol pattern*, not the encoding. Sanity-check against the empirical idiom (what does `dns-sd -B` send? what does Wireshark see when iOS finds an AirPlay speaker?) before trusting "unit tests pass = ready to ship" on protocol code.
- **Broadcast-shape probes and per-IP probes are different architectural patterns; the natural integration is "broadcast first, per-IP for residue."** 9.2.5's `HostnameResolutionLadder` was a per-IP architecture (one IP → one query × N protocols, parallelised across IPs). 9.2.6's `MdnsServiceDiscoveryProbe` is a broadcast architecture (one multicast → many devices respond, parsed in a receive loop). They are not interchangeable. NetBIOS NBSTAT must be unicast to a specific IP; mDNS service-discovery must be multicast to the whole subnet. The natural integration is *not* to fold one into the other — instead run the broadcast pass first (~3 s for the entire LAN), patch hostnames for any IP that responded, then run the per-IP ladder *only over IPs without a hostname*. This minimises wall-clock (most LANs get bulk-resolved by the broadcast) and minimises per-IP load (the ladder runs over fewer IPs in steady state). The lesson generalises to any future probe: figure out whether the protocol is "broadcast-shape" or "per-IP-shape" before you wire it into the orchestrator — different shapes belong in different orchestration layers.

### Phase 9.3

- **"Mutable event → chain audit AND broadcast" is a project-wide invariant, not a per-event-kind option.** Phase 9.2 wired `LanScannerService` to chain-write `LanDeviceFirstSeen` / `LanDeviceMacChanged` events but never added the broadcast leg — every other mutable event kind (counter batches, firewall rule changes, alerts) already went to both paths uniformly, so the gap was an oversight rather than a design choice. Critically, the gap was invisible to unit tests: the chain-write was tested, the broadcast leg simply didn't exist to test. The lesson: when an event kind goes to the chain, the test suite should check the broadcast leg in the same fixture — preferably with assertions that fail loudly if a new event kind is added but only one of the two legs is wired. Phase 9.3 formalises this by adding "FirstSeen broadcasts" and "MacChanged broadcasts" tests alongside the existing chain-write tests in `LanScannerServiceTests`. Future event kinds should follow suit.
- **Doc-drift is real and worth treating like a build break.** `phases.md` §1 status had been claiming "eighteen RPCs" for several phases — but the empirical count in `beholder_local.proto` was 15 (the doc was written when the count was 18 in an early proto draft, never updated when later phases trimmed). Adding 2 RPCs in 9.3 surfaced the discrepancy because the new doc text had to commit to a real number. The fix is mechanical (corrected to 17 in the same commit) but the prevention is structural: any phase that touches the RPC surface should recount and update the doc, the same way a phase that changes the test count updates the header. Consider adding a "count claims to check" checklist to the phase-completion verification step.

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
| 5.4.3 cleanup | 2026-04-17 to 2026-04-18 | Full audit across daemon, UI, protocol, tests (40 findings) | Missing guard clauses on public APIs (#1–#17); UI deviations from `UI_QUALITY_STANDARDS.md` — hardcoded FontSize literals, 4 buttons lacking hover feedback, `ct` vs `cancellationToken` naming drift (#18–#24); `_10m`/`_1h` tiers uncovered in stitched-timeline test (#29); `fromMs == toMs` zero-width range case untested across all 5 query RPCs (#30); query-RPC errors from the #17 store guards surfaced as `Internal` instead of `InvalidArgument` (#32–#33); `ProtocolConverters.AlertKind` asymmetric (forward-only converter) + `FakeDaemonClient` exception-hook gaps on 2 RPCs (#34, #40); `TrafficStorageOptions` registered as bare singleton inconsistent with `Configure<T>` siblings, `IOptionsMonitor` live-reload untested (#35–#36); `MainWindowViewModel` stub-tab VMs undisposed + 10 hand-fired `PropertyChanged` for derived props + `ClearProcessList` O(n) `CollectionChanged` churn (#37–#39); `phases.md` stale (#31) | All closed. Added guard clauses (`ArgumentOutOfRangeException.ThrowIfLessThan`, `ArgumentException.ThrowIfNullOrWhiteSpace`). Added 5-token FontSize scale (`Small/Caption/Body/Subheading/Heading`) to theme files, replaced 38 literals, added hover styles to 4 buttons, renamed 28 UI `ct` → `cancellationToken` sites. Added 5 `_ZeroWidthRange_ReturnsEmpty` tests + expanded stitched test to all 5 tiers. Extracted `BeholderLocalService.ExecuteQueryAsync<T>` helper that classifies `ArgumentOutOfRangeException`/`ArgumentException` as `InvalidArgument`. Added `AlertKind.FromProto` + strengthened existing theory to full round-trip + added 2 missing `FakeDaemonClient` exception hooks. Moved `TrafficStorageOptions` to `Configure<T>` + `IOptionsMonitor<T>` everywhere + added `PresetSwitchedLive` test + `FakeOptionsMonitor.Set()` mutator + XML doc on `RollupService` documenting live-reload contract. Stub tab VMs now implement `IDisposable`, `MainWindowViewModel` disposes all 4, `[NotifyPropertyChangedFor]` replaces manual notifications, `ClearProcessList` uses `Clear + Add` (2 events vs N). 472 → 526 tests. |
| 6.4 → 7.5 + 6.7 polish | 2026-05-03 | Daemon alert pipeline (Phase 7 + 7.5 logical identity + spoof detection + ADR 007), UI Phases 6.6–6.10 (Alerts master-detail, action buttons, OS notifications, ErrorBanner reusable control, executable-not-found disable), `IDispatcher` abstraction (commit `6b5de2e`), new `Beholder.Ui.Windows` project (first UI platform split), Firewall tab polish (~11 follow-up commits), scroll-into-view + cold-start race fixes for the Alerts → Firewall deep-link (commits `816763f` + `2cb8753`). ~32 commits since `80ab759` total. | **2 Critical**: (C1) `BinaryHashMonitor.UpdateRegistryAsync` constructed `new ProcessInfo` with only the pre-7.5 6 args, dropping all 6 Phase 7.5 identity columns to NULL on every hash check — `INSERT...ON CONFLICT DO UPDATE SET` then NULL'd the existing row's identity, breaking ADR 007's spoof-detection guarantee. (C2) `AlertsTabViewModel.ActivateAsync` had the same `bool _activated` cold-start race that `FirewallTabViewModel` had pre-`2cb8753`: synthetic completed Task returned to the second concurrent caller (toast-click → `NavigateToAlertAsync`) while the first call's snapshot RPC was still in flight, so `SelectBySeq` ran against an empty `Alerts` collection. **3 Minor** (test gaps): no regression test for identity-column preservation through the SHA-256 update path; no test for `FirstFlow_NoIdentityProviderResult_FallsBackToPathBased` (provider exists but `ReadIdentityAsync` returns null); no test for the AlertsTab cold-start race. **Deferred** (kept in §5): verified-publisher UI badge, identity backfill for pre-7.5 rows, catalog-signed binary path-fallback documentation, master-list orphaned-binary indicator, notification rate-limiting. | Commit `b05442b`: BinaryHashMonitor passes all 11 ProcessInfo args mirroring `NewProcessDetector.RefreshLastSeenAsync` + 2 regression tests. Commit `7299702`: `AlertsTabViewModel.ActivateAsync` stores `Task? _activationTask` instead of `bool _activated` so concurrent callers await the same load (mirrors FirewallTab `2cb8753` change exactly), `FakeDaemonClient` gains `SnapshotResponder` for delay-injection in the regression test. Resolves the spawned-task chip filed after `2cb8753`. **845 → 848 tests**. |

---

## 5. Known Gaps and Forward-Looking Notes

- **O(n) chain verification** — `VerifyAsync` reads all rows sequentially. Fine for weeks of uptime, but months of data will need checkpoint-based verification (verify from last checkpoint instead of seq 1). Address in Phase 11.
- **Startup OS/SQLite firewall reconciliation** — on daemon restart, OS firewall rules and SQLite `firewall_rules` table may diverge (crash during apply, manual OS changes). A reconciliation pass at startup is deferred to Phase 12.
- **Linux platform** — `Beholder.Daemon.Linux` (`NetlinkFlowSource` + `NftablesFirewallController`) and Linux UI work both deferred. No timeline; Windows is the primary platform. **UI shape when the Linux port lands:** a future Linux notification impl (D-Bus `org.freedesktop.Notifications`) goes inline at `Beholder.Ui/Services/LinuxNotificationService.cs` behind `#if PLATFORM_LINUX` (would need adding to `Beholder.Ui.csproj` alongside the existing `PLATFORM_WINDOWS` define), NOT in a separate `Beholder.Ui.Linux` project. [ADR 008](decisions/008-ui-single-project-policy.md) sets the threshold for revisiting (~500 LOC of platform-specific UI code, divergent UX, or platform deps that can't be a single conditional `PackageReference`). The daemon-side split (`Beholder.Daemon.Linux`) stays mandatory.
- **Uplink client (Beholder.Daemon.Uplink)** — project stub exists. Outbound gRPC client with connection state machine, JWT auth, and telemetry forwarding. Phase 10.
- **Uplink test stub (Beholder.Tests.UplinkStub)** — project stub exists. Reference gRPC server for uplink integration testing. Phase 10.
- **TrafficEngineTests residual flakiness** — inherited from AccumulatorTests. The settle-signal fix eliminated most failures, but ~1-2% timeout rate may persist under extreme CPU contention. Monitor during future test runs.
- **UI quality standards enforcement** — Phase 5.4 onward must comply with `docs/UI_QUALITY_STANDARDS.md`. Phases 5.1–5.3 are retroactively compliant (their quality issues were caught and fixed during manual review). Phases 6.4–6.10 each followed the discipline (three-window-size verification, real-data 30s+ uptime, reference comparison against GlassWire / Windows Firewall). Every future UI phase plan must continue including the verification and reference comparison sections defined in that document.
- **No off-the-shelf Avalonia 12 map control.** Phase 8 surveyed the ecosystem (LiveCharts2 / Mapsui / XAML.MapControl.Avalonia / Codenizer.Avalonia.Map / Asv.Avalonia.Map) and confirmed every option either lacks Avalonia 12 support or requires external map-tile servers (incompatible with the "no outbound network" stance). Custom Canvas mirroring `TrafficChartControl` is the durable path; the `WorldMapControl` shipped in Phase 8 follows that precedent. Re-evaluate if a future Avalonia 12-compatible vector-map library appears, but no urgent need — the custom control is tuned for this use case.
- **NuGet vulnerability suppressions (2 active):** (1) **Tmds.DBus.Protocol** — force-upgraded to 0.92.0 via explicit `PackageReference` to avoid Avalonia 12's transitive pull of vulnerable 0.90.3. Monitor for Avalonia updates that resolve this transitively. (2) **System.Drawing.Common 4.7.0** — added in Phase 6.8 because `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 transitively pulls it; the toast-image surface is unreachable in our code (we don't attach images), so the suppression in `Directory.Packages.props` is documented inline. Drop both when their respective parent packages release fixed versions.
- **Catalog-signed binary fallback** (Phase 7.5 limitation per ADR 007 §Out of scope) — `AuthenticodeVerifier.Read` returns null for catalog-signed binaries (notepad.exe et al. — no embedded cert, signature lives in a `.cat` file). These binaries fall through to path-based dedup, identical to pre-7.5 behavior. Catalog-signature reading is doable (`WinVerifyTrust` + `CryptCATAdminCalcHashFromFileHandle`) but adds platform code for a class of binaries that rarely matter for spoof detection.
- **Identity backfill** (Phase 7.5 limitation) — pre-7.5 `process_registry` rows have NULL identity columns; only newly-seen paths get identity resolved at first observation. A backfill sweep (re-read VersionInfo + signature for every existing row) is straightforward but deferred — the practical impact is just that pre-7.5 binaries get one extra `NewProcess` alert when the identity provider catches up on next observation.
- **Verified-publisher UI badge** (Phase 7.5 deferred per ADR 007 §Out of scope) — protocol surface is complete (cert subject + status stored per `process_registry` row), but no UI affordance ships yet. Candidates: small "verified" badge in the Alerts detail pane PROCESS subsection, lock icon next to the path in the Firewall rule list. Defer until at least one user complains the data is invisible.
- **Notification rate-limiting** (Phase 6.8 deferred) — toast fires per alert with no app-side coalescing. Windows Action Center groups them visually but bursts (e.g., 20 NewProcess alerts when a user opens Steam) still produce 20 individual toast pop-ups. A polish pass would add "+N more" coalescing keyed on a small time window.
- **Master-list orphaned-binary indicator** (Phase 6.10 deferred) — only the detail-pane action buttons disable when the alert's binary is missing. The master-list row gives no visual hint, so a user scrolling the list doesn't know which alerts are actionable. A `⚠` glyph next to the kind badge (mirroring the Firewall tab's orphaned-rule treatment) is the obvious extension.

---

## 6. Remaining Phases

### Checkpoint — Historical Traffic Feature-Complete

Not a code deliverable. A project milestone marking the point at which the Traffic tab is feature-complete for historical exploration. At this point the user can: watch live traffic streaming at 1-second fidelity for the last 5 minutes, scrub back through any time range up to ~2 years via the range selector, see tier-aware aggregations for each range with the rollup invariant holding, and trust the data is free of Beholder's self-traffic.

**Deferred items** — to be picked up in later phases or at the user's direction:
- Event pins (mark "this is when I started the VPN") — deferred to a later UX pass.
- Destination breakdown panel (hosts/ports inside a selected process) — deferred; data is available via `GetProcessDestinationsAsync` but no panel exists yet.
- Per-tier retention tuning in `appsettings.json` — current values are hard-coded in `TrafficStorageOptions` C# defaults.

**Decision point.** Natural pause to validate the historical-traffic story before moving to the Firewall tab (Phase 6.4) or revisiting any architectural questions surfaced during 4.6b / 5.4.2 implementation (e.g., rollup cadence, eviction timing under load, or the `TrafficStorageOptions` binding wart flagged in Phase 4.7's plan).

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
- ~~6.4 — Firewall tab (rule table, three-state ALLOW/BLOCK/DEFAULT pill, Active/Inactive grouping, master ON/OFF toggle).~~ ✅ See §2.
- ~~6.5 — Firewall tab, recent activity strip.~~ ✅ See §2.
- ~~6.6 — Alerts tab, master-detail layout.~~ ✅ See §2.
- ~~6.7 — Alerts tab, action buttons (BLOCK PROCESS OUT, ADD RULE) + scroll-into-view + cold-start race fix.~~ ✅ See §2.
- ~~6.8 — OS-native notifications + new `Beholder.Ui.Windows` project.~~ ✅ See §2.
- ~~6.9 — Dismissable error banner (`ErrorBanner` reusable control).~~ ✅ See §2.
- ~~6.10 — Disable action buttons when alert's executable is missing.~~ ✅ See §2.
- Checkpoint: all core tabs functional end-to-end. **Reached.**

### Phase 7 — Alert pipeline (daemon side) ✅

All four sub-phases shipped atomically in commit `d51c625`; Phase 7.5 (logical app identity + Authenticode spoof detection, ADR 007) shipped in `a3515f4`. See §2 for full entries.

- ~~7.1 — `NewProcessDetector`~~ ✅
- ~~7.2 — `BinaryHashMonitor`~~ ✅
- ~~7.3 — `ChainIntegrityMonitor`~~ ✅
- ~~7.4 — Wire detectors into pipeline, broadcast alerts via IPC~~ ✅
- ~~7.5 — Logical app identity + Authenticode spoof detection (out-of-roadmap addition).~~ ✅ See §2.
- Checkpoint: **alerts flow end-to-end from daemon detection to UI display, with logical-identity dedup so Squirrel auto-updaters stay silent and a spoofed publisher fires `HashChanged`.** Reached.

### Phase 8 — Traffic → Map sub-view ✅

Scope clarified during planning: this is the Traffic tab's existing MAP toggle, NOT a new top-level tab. The roadmap text below was the original 8.1/8.2 plan; the actual implementation shipped as one atomic phase. See §2 for the full entry.

- ~~8.1 — Map ViewModel state (per-country byte totals + filters)~~ ✅ rolled into `TrafficTabViewModel`
- ~~8.2 — Map View (custom Canvas `WorldMapControl`, equirectangular projection)~~ ✅ Phase 8.2's "If unstable, implement as custom Canvas" branch was the chosen path — LiveCharts2 still doesn't support Avalonia 12, Mapsui needs external tiles.

### Phase 9 — Scanner

*Feature set to be scoped before implementation begins.* The SCANNER tab is wired into the top navigation but `ScannerTabView.axaml` and `ScannerTabViewModel.cs` are stubs ("Scanner tab content (deferred)") and no ADR or design doc currently defines what Scanner does. Likely candidate features: port scan of locally-known destinations, vulnerability lookup against a CVE feed, anomaly detection (deviation-from-baseline alerts on existing flows), network discovery (LAN sweep). The phase plan begins with a scoping ADR proposing the feature surface, gets user approval, then splits into sub-phases per the chosen scope. The slot exists in the roadmap so Phase 13 (Settings) can sit "after Scanner" as a cleanly-ordered queue position even before Scanner's contents are decided.

### Phase 10 — Uplink client

- 10.1 — `UplinkClient` state machine (Disconnected → Connecting → Authenticated → Streaming) with exponential backoff
- 10.2 — Telemetry forwarding (counter batches + alerts to aggregator)
- 10.3 — Remote command handling (firewall commands with capability validation)
- 10.4 — Configuration and Ed25519 key management
- Checkpoint: uplink works end-to-end against `Beholder.Tests.UplinkStub`.

### Phase 11 — Signed checkpoints and chain export

- 11.1 — `CheckpointSigner`: periodic Ed25519 signing of chain head, writes to `checkpoint` table
- 11.2 — Enhanced `VerifyChain`: also verifies checkpoint signatures. **Note:** this improves the O(n) verification gap — verify from last checkpoint instead of seq 1.
- 11.3 — Chain export: CLI subcommand or RPC for signed JSON export of filtered events

### Phase 12 — Polish and hardening

- 12.1 — Windows service installation (sc.exe or WiX installer, auto-start)
- 12.2 — Error handling sweep (every catch, every log call, every edge case)
- 12.3 — Performance profiling (24-hour soak test: memory, CPU, SQLite size, GC pressure)
- 12.4 — Configuration documentation (reference `beholder.toml` with comments)
- 12.5 — Startup reconciliation: sync OS firewall rules with SQLite `firewall_rules` table on daemon start
- Final checkpoint: install on clean machine, run for a week, understand what happened.

### Phase 13 — Settings (final UI deliverable)

**Why last.** Each prior phase adds options to surface (firewall defaults, uplink server URL + JWT, alert thresholds, scanner-specific config, etc.). Building Settings last means the information architecture is designed once, around the full set of options that exist, rather than being retrofitted every time another phase adds a configurable. The five toggles already known at this writing (`RollupOptions.Preset`, `RecordingOptions.FilterSelfTraffic`, `DnsOptions.EnablePreload`, `DnsOptions.EnableReverseDnsFallback`, `SniOptions.EnableSniCapture`) plus whatever Phases 6–12 add gives a meaningful surface area to design against.

**Quality gate.** Full compliance with `docs/UI_QUALITY_STANDARDS.md`. Each sub-phase plan must include the states-implemented, verification (three window sizes, 30 s daemon uptime, real data, extreme scenario), and reference-comparison sections that document defines.

- 13.1 — **Settings shell.** Vertical category sidebar + content pane (Windows 11 Settings pattern). Replaces `SettingsTabView.axaml`'s "Settings content (deferred)" placeholder. `SettingsTabViewModel` orchestrates child section VMs (one per category). Theme tokens, FontSize tokens, hover states consistent with the rest of the app.
- 13.2 — **Capture & DNS section.** Surfaces `DnsOptions.EnablePreload`, `DnsOptions.EnableReverseDnsFallback`, `SniOptions.EnableSniCapture`. Each toggle: label + one-line description (drawn from the option's existing XML doc comment), "Live" or "Restart required" indicator, privacy-impact icon for toggles that change observability surface (turning SNI off = some flows lose hostnames; turning reverse-DNS off = direct-IP flows show as raw IPs forever).
- 13.3 — **Storage section.** `RollupOptions.Preset` (Balanced default vs. Compact picker, with estimated disk-footprint labels). `RecordingOptions.FilterSelfTraffic`. Plus any storage-related options added by intervening phases.
- 13.4 — **Firewall section.** *Content depends on Phase 6.4–6.5 outcomes.* Likely candidates: default action for unknown apps, alert thresholds, undo retention window.
- 13.5 — **Map section.** *Content depends on Phase 8 outcome.* Likely candidates: projection, color scheme, dot opacity.
- 13.6 — **Scanner section.** *Content depends on Phase 9 outcome.* Likely candidates: TBD; the Scanner feature itself is unscoped at this writing.
- 13.7 — **Connection / Uplink section.** *Content depends on Phase 10 outcome.* Server URL, JWT credentials, enable/disable, retry interval, keepalive.
- 13.8 — **About / Diagnostics section.** Daemon version, build hash, last-restart timestamp, storage size (sum across the five tier tables + dns_cache + event_log + firewall_rules), test count + skipped count from the most recent build, "Open log folder" button, "Restart daemon" button (admin gate, confirmation dialog), ADRs hyperlinked.
- 13.9 — **Dirty-state + apply/cancel UX.** Track per-option whether it's been changed since load. "Apply" button enabled only when dirty. "Cancel" reverts the in-memory copy to last-saved state. Leaving with unsaved changes prompts confirmation. Options that are *always live* (`IOptionsMonitor.OnChange`-driven) bypass the apply pattern; the UI clearly distinguishes immediate-effect from apply-required.
- 13.10 — **Settings persistence + reload.** Settings persist to `appsettings.json` (or a sidecar override file — design choice). Live-reload validated end-to-end: toggle a setting, observe the daemon's log line confirming `IOptionsMonitor.OnChange` fired and the relevant component re-read the value. "Reset to defaults" available per-option, per-category, and all-at-once (the all-at-once version requires confirmation).
- 13.11 — **Quality-gate sign-off.** States documented (loading, populated, error, extreme). Three window sizes verified (1100×720, 1280×800, maximized) with screenshots. 30 s daemon uptime against real network activity. Reference comparison against Windows 11 Settings or 1Password Settings at the same resolution. Banned-pattern audit (no fixed widths, no raw hex colors, no unvirtualized 100+ item lists, no buttons without hover states).
- 13.12 — **Tests.** ViewModel tests for dirty-state tracking, apply/cancel, validation, reset-to-defaults. Integration smoke: toggle each option, observe daemon log line, verify behavior changed. State tests for daemon-disconnected mid-edit + dirty-state preservation on reconnect. Test-count target: existing 611 + ~30 new = ~641, depending on how much of the persistence + live-reload path is unit-testable vs. integration-only.
- Final checkpoint: every option in the daemon and UI is reachable from Settings, every toggle's effect is observable in real time or with a clearly-indicated restart, the page passes the UI quality bar at all three window sizes, and the project is shippable.

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
