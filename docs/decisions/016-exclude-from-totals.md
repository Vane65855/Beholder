# 016: "Exclude from totals" — Aggregate-Read-Time Process Exclusion

## Context

VPN and tunnel clients double-count traffic in a per-process monitor: every
app's bytes are re-counted through the tunnel binary, so a machine running
WireGuard shows roughly 2× its real WAN traffic in the All-processes totals,
the aggregate chart is dominated by the tunnel echo, and the Map gets a giant
blob in the VPN endpoint's country. Users need a way to remove such
"infrastructure noise" processes from aggregate views.

The dangerous shape for this feature is the obvious one: reuse the
`Recording.FilterSelfTraffic` capture-time drop. That would stop *recording*
the excluded process entirely — and the one binary all traffic transits
through would become invisible to `NewProcess` / `HashChanged` alerting and
absent from the chain-audited record. For a security monitor, a user-facing
"stop watching this process" switch is a self-inflicted blind spot.

## Decision

### Exclusion is aggregate-READ-time, never capture-time

Excluded processes stay fully captured, recorded to every rollup tier,
alertable, chain-audited, and inspectable per-process. Exclusion is applied
only where *aggregates across processes* are computed:

- **Daemon SQL** (the authority): `GetAggregateTimeline` (threaded through
  `TimelineStitcher`, including its data-extent scan), and
  `GetProcessDestinations` / `GetCountryBreakdown` / `GetProtocolBreakdown`
  when no explicit process is selected. One shared
  `ProcessExclusionSqlFilter` builds the parameterized
  `process_path COLLATE NOCASE NOT IN (...)` fragment (empty list → empty
  clause, zero overhead when unconfigured).
- **UI live aggregation** (client-side by design): status-strip totals /
  rates / WAN, the All-processes chart, and the process list's all-row skip
  excluded states.

**Explicit selection always wins.** A view scoped to one process
(per-process timeline, destinations, COLS-for-process) includes it even when
excluded — the user asked to see that process. `GetProcessSummaries` keeps
excluded rows for the same reason (the UI decides how to render them).

### The list is a daemon setting (ADR 010 pattern), chain-audited per change

`ITotalsExclusionState` + `TotalsExclusionState` follow the Phase 13.2
section-singleton shape. Persistence is one `settings_overrides` row
(`Traffic.ExcludedProcessPaths`, a JSON string array — the value_json column
was designed for exactly this; `SettingsOverridesService` gained
`ReadStringListOverride`). The RPC is a whole-list `SetTotalsSettings` (the
client's add/remove UI recomputes the full list; the daemon normalizes —
trim, drop empties, dedupe case-insensitively). Each real change appends
`EventKind.TotalsExclusionsChanged = 17` whose payload carries the **full
post-change list**, so hiding a process from totals is itself always
auditable and each chain entry is self-contained.

The list-shaped 13.6 precedent (dedicated table + Add/Remove/List RPCs) was
considered and rejected: app-identity rules are structured entities (ids,
two-part match, display names); this is a flat set of strings.

### Matching is by path, case-insensitive

Exclusion entries come from a file picker (filesystem casing) while recorded
paths come from ETW; Windows paths are case-insensitive. The state singleton
compares `OrdinalIgnoreCase`; SQL uses `COLLATE NOCASE` (ASCII-only folding —
acceptable for Windows executable paths, which are ASCII in practice).

### Hidden by default; "show excluded" is a UI-local display preference

Excluded processes disappear from the Traffic tab's process list by default.
A single "Show excluded processes" toggle re-lists them with a ⊘ marker.
Either way their bytes never count toward aggregates. The toggle is
**display-only**, so it lives in `JsonUiPreferencesStore`
(`ShowExcludedProcesses`) per the close-to-tray precedent — never in the
daemon's chain-audited settings.

### The UI mirrors the list instead of receiving per-process proto flags

The original design stamped an `excluded_from_totals` bool onto every
`CounterSnapshot` / process summary at the IPC boundary. Dropped in favor of
a small `TotalsExclusionUiState` mirror in the UI, because the flag bought
consistency the system already has:

- The UI must hold the list anyway (the Settings management card).
- This single-instance UI (SingleInstanceGuard) is the list's **only
  writer** — it seeds from `GetSettings` on every daemon connect and updates
  the mirror with each echoed `SetTotalsSettings` response.
- ADR 010 already accepts settings changes without a broadcast surface.

The flag would have cost a proto field plus ~43 constructor call-site
updates (`BroadcastService`) for no behavioral difference. Trade-off: an
out-of-band writer (another RPC client on the DACL'd pipe) could make the
mirror stale until the next connect — the same accepted posture as every
other settings section.

## Consequences

### Positive

- Totals, charts, and breakdowns become truthful on VPN machines with one
  Settings edit, while the security posture is completely unchanged —
  recording, alerts, chain, and firewall never see the exclusion list.
- Every list change is chain-audited with the full new list; an auditor can
  reconstruct the exclusion state at any point in time.
- Zero overhead when the feature is unused (empty list short-circuits both
  the SQL clause builder and the UI set lookups).

### Negative

- Two enforcement points (daemon SQL for historical, UI for live) that must
  agree; both consult the same daemon-owned list, and the coordinator/chart/
  strip tests plus store tests pin the shared semantics.
- The COLS/Map "all processes" views silently omit excluded traffic; the
  Settings card's caption and the per-row ⊘ marker are the visibility story.
- SQLite `NOCASE` folds ASCII only, so a non-ASCII path differing only by
  case would match in the UI but not in SQL. Not a practical Windows
  scenario; noted in `ProcessExclusionSqlFilter`'s doc comment.
