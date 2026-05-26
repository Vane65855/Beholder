# 010: Runtime-Mutable Settings — Per-Section State Singletons + SQLite Overrides (Phase 13.2)

## Context

Phase 13.1 shipped the Settings tab's read-only foundation — Data Storage stats, Maintenance (chain verify), About. Phase 13.2 is the first interactive sub-phase: it adds two sections (Recording, Hostname Resolution) with toggles that actually mutate daemon state from the UI. Sub-phases 13.3 (Alerts), 13.4 (Scanner), and 13.5 (Storage retention) will each follow the same shape. This ADR fixes the pattern up-front so those sub-phases don't relitigate it.

The design space had four obvious shapes:

| Shape | Description | Why we didn't pick it |
|---|---|---|
| **Edit `appsettings.json` directly** | UI writes to the daemon's config file; `IOptionsMonitor<T>` picks up the change. | Re-introduces a file-write path the daemon doesn't currently have. File-locking corner cases on Windows. Comment preservation is non-trivial. Not durable against `appsettings.json` being a packaged resource (read-only on locked-down installs). |
| **One big `ISettingsState` interface** | Single singleton exposes every Settings knob across every section. | Violates Single Responsibility — Recording's `FilterSelfTraffic` and Storage's retention preset have nothing in common except "user can toggle them." The interface would grow unbounded with every sub-phase. |
| **Per-section state singletons + persistent overrides** | One singleton per Settings section, each mirroring `IFirewallEnforcementState`'s shape. Persistence is a separate SQLite table loaded at startup. | Chosen — see below. |
| **Per-toggle state singletons** | One singleton per individual toggle. | Excessive surface area — Recording's "section" semantically groups one toggle today but will hold more (`FilterLocalLoopback`, `FilterMulticast`, etc.) later. Grouping by section keeps the atomicity story honest. |

`IFirewallEnforcementState` (Phase 6.4) had already established the precedent for runtime-mutable daemon state: a lock-protected backing field, a `SetEnabled(bool) → bool changed` method that's idempotent, and a `StateChanged` event for in-process subscribers. Phase 13.2 generalises that shape to the Settings surface.

## Decision

### Per-section state singletons mirror `IFirewallEnforcementState`

Each Settings section gets one interface in `Beholder.Core` + one implementation in `Beholder.Daemon`:

- `IRecordingSettingsState` — exposes `FilterSelfTraffic` getter + `SetSettings(bool filterSelfTraffic) → bool changed` + `StateChanged` event.
- `IHostnameResolutionSettingsState` — three getters (`EnablePreload`, `EnableReverseDnsFallback`, `EnableSniCapture`) + atomic three-arg `SetSettings(...)` + event.

Each singleton:

1. Reads its initial values from `IOptions<T>` at construction (so the `appsettings.json` shape remains the source of *defaults*).
2. Uses a single private `_gate` lock around the read getters and the `SetSettings` write — no `volatile`, no `Interlocked` games. Locks are cheap; the per-event hot path (`FlowEventPipeline.OnFlowEventReceived` reading `FilterSelfTraffic`) takes the lock once per event, ~1 ns uncontended.
3. Fires `StateChanged` **outside** the lock to avoid re-entrancy deadlocks if a subscriber's handler tries to call back into the same singleton.
4. Returns `true` from `SetSettings` only on a real value transition. Idempotent re-asserts return `false` and skip the event.

This is verbatim the `FirewallEnforcementState` pattern. Future sub-phases adding their own sections follow the same template.

### Persistent overrides live in a single SQLite table

```sql
CREATE TABLE IF NOT EXISTS settings_overrides (
    name        TEXT PRIMARY KEY,    -- dotted section path, e.g. "Recording.FilterSelfTraffic"
    value_json  TEXT NOT NULL,        -- "true" / "false" today; JSON for richer future types
    updated_at  INTEGER NOT NULL      -- Unix ns
);
```

One row per overridden setting. Keys are dotted-section strings centralised in `SettingsKeys.cs` (`RecordingFilterSelfTraffic`, `DnsEnablePreload`, `DnsEnableReverseDnsFallback`, `SniEnableSniCapture`). Values are JSON to keep the table type-agnostic — booleans today, integers / strings / enum names as future sections add new knob types, all without schema migration.

Persistence happens in two places:

1. **At startup**, a dedicated `SettingsOverridesService : IHostedService` runs *first* (registered before any consumer of the state singletons in `Program.cs`). Its `StartAsync`:
   - Reads every row from `settings_overrides`.
   - For each known dotted key, deserialises the JSON value and calls the corresponding state singleton's `SetSettings(...)` with the override.
   - Unknown keys (future-version downgrade scenario) are logged and skipped.
2. **At RPC time**, the `SetRecordingSettings` / `SetHostnameResolutionSettings` handlers in `BeholderLocalService` upsert the affected keys after applying the in-memory change. Failures here surface as `success=false` on the RPC response (the in-memory change still happened — see "Soft-failure on persistence" below).

Hosted-service registration-order is a load-bearing contract: hosted services' `StartAsync` runs sequentially in registration order. The state singletons themselves are registered as plain DI singletons (constructed lazily on first resolve, which is during the consumer's `StartAsync`); `SettingsOverridesService` resolves them eagerly in its own `StartAsync` so persisted overrides land *before* any consumer's `StartAsync` reads them.

### Chain-audit per section, payload per change

Each Settings section gets one `EventKind` ordinal and one payload encoder:

- `EventKind.RecordingSettingsChanged = 11` + `RecordingSettingsPayloadEncoder` (JSON: `{ "filterSelfTraffic": bool, "changedAtUnixNs": int64 }`).
- `EventKind.HostnameResolutionSettingsChanged = 12` + `HostnameResolutionSettingsPayloadEncoder` (JSON: three bools + `changedAtUnixNs`).

Encoders mirror `FirewallEnforcementTogglePayloadEncoder` exactly — deterministic JSON (no indentation, fixed field order), Unix-ns timestamps (ms-precision × 1_000_000), symmetric `TryDecode` that returns null on malformed input. One chain entry per Set RPC, regardless of how many fields in the section's bundle actually changed.

No `DaemonEvent` stream variant for settings changes. Mirrors the `FirewallEnforcementState` precedent: Settings tab is the only UI for these knobs in any reasonable foreseeable scope, so cross-instance live broadcast isn't needed. Other subscribed UIs (if any) catch up on next tab refresh.

### Asymmetry: "live" vs "next-start" toggles

Of the four toggles in Phase 13.2, only two can take effect immediately:

| Toggle | Consumer | Read pattern | Effect timing |
|---|---|---|---|
| `FilterSelfTraffic` | `FlowEventPipeline.OnFlowEventReceived` | Per flow event | Live |
| `EnableReverseDnsFallback` | `ReverseDnsFallbackCache.Resolve` | Per cache miss | Live |
| `EnablePreload` | `EtwDnsCache.PreloadFromWindowsDnsCache` | Once at `StartAsync` | Next daemon start |
| `EnableSniCapture` | `PktmonSniSource.StartAsync` | Once at `StartAsync` | Next daemon start |

The two snapshot-at-startup consumers still ultimately read from the state singleton (not from `IOptionsMonitor<T>` snapshots), so the persisted override flows through correctly — they just only consult the singleton during their own `StartAsync`. Live-toggling these would require refactoring ETW session lifecycle (tear down + recreate the PktMon session, re-run preload), which is risky for limited user-visible benefit on the first interactive Settings pass.

The UI renders a `(takes effect on next daemon start)` caption next to those two pills to make the timing honest. The setting still persists to `settings_overrides` immediately; it just doesn't reflect in behaviour until the daemon restarts.

A future sub-phase can revisit this if user demand surfaces. The pattern doesn't prevent live-toggling later — it just doesn't pay the lifecycle-refactor cost today.

### Soft-failure on persistence

The Set RPC handler's flow:

1. Validate request (`InvalidArgument` for null/empty fields).
2. Call `state.SetSettings(...)`. This is the in-memory mutation — it's already done by the time we hit the next step.
3. If `changed` is true: upsert to `settings_overrides`.
4. If upsert throws: log + return `Response { Success = false, Message = "...", Values = current_state }`. The in-memory state is *not* rolled back — the user's intent has been honoured for this daemon session; only persistence failed.
5. If upsert succeeds: append chain event. Chain-append failures are logged but don't fail the call ("rule is applied but unaudited" semantics, mirroring `ApplyFirewallRule`).
6. Return `Response { Success = true, Message = "Settings updated.", Values = current_state }`.

Mirrors the `SetLanDeviceLabel` / `TriggerScan` "soft-failure" precedent: recoverable conditions return a structured failure the UI can render inline rather than surfacing a generic `RpcException`. Hard validation errors (null `values` field) still throw `InvalidArgument`.

## Consequences

### Positive

- The pattern is now fully specified for 13.3 / 13.4 / 13.5. Each new section is ~6 hours of work: define an interface in Core, implement it in Daemon, add an entry to `SettingsKeys.cs`, add an `EventKind` ordinal + a payload encoder, add a new RPC handler, wire the consumer to read from the singleton, add UI section card + row VM + commands. No design discussion needed.
- Settings overrides survive daemon restart with the same data shape (one JSON-valued row per setting).
- The "live vs next-start" split is explicit in the UI, not hidden — users see exactly when their toggle takes effect.
- Chain-audit is full per-change: every Settings mutation appears in `event_log` with the new value and timestamp, available to `GetFirewallActivity` once that surface is generalised to a "settings activity" view.

### Negative

- Two of four toggles require a daemon restart to take effect. Honest UX, but it does mean "click toggle → see effect" needs an extra step for those two. The caption documents this explicitly. A future sub-phase can lift this if the ETW lifecycle refactor proves worth the cost.
- One SQLite write + one chain-append per Settings change. Negligible cost for human-driven changes; problematic only if Settings becomes a programmatic surface (which it isn't — the local IPC is DACL'd to the same user).
- Adding a new Settings *type* (slider, dropdown, multi-select) requires JSON-encoding logic on both sides (writer + `SettingsOverridesService`'s reader). Today there's only `ReadBoolOverride`; the next type that ships will need its own `Read{Int,String,Enum}Override` helper. Worth doing once we have a concrete case — premature genericisation today.
- The `settings_overrides` table is keyed on the dotted name string; renaming a setting (e.g., `Recording.FilterSelfTraffic` → `Recording.IncludeBeholderTraffic`) requires a one-off migration if we want existing overrides to carry forward. Acceptable — Settings keys are part of the IPC surface and should be renamed only deliberately.

## Out of scope

- **Live-toggleable `EnablePreload` / `EnableSniCapture`**. Requires ETW session lifecycle refactor. Tracked as a follow-up if user demand materialises.
- **Identity verification / authentication for `Set*` RPCs**. The local IPC channel is DACL'd to the same user already (per `ARCHITECTURE.md` "IPC Protocol" section). No new auth surface is added.
- **`DaemonEvent` stream broadcasts for Settings changes**. Settings tab is the only UI for these knobs; chain-audit is the durable record. Adding a broadcast surface is cheap if cross-instance Settings views ever materialise.
- **Other `DnsOptions` / `SniOptions` knobs** (queue capacities, buffer sizes, dedup capacity). Advanced tuning, stays JSON-only, not surfaced in the UI.
- **Versioning the chain payload schema**. Today there's one schema per `EventKind`; adding a field is a breaking change that requires `TryDecode` to be lenient. Defer until a real second version exists.
