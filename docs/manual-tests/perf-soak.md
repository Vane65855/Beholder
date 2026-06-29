# Performance soak (Phase 12.3)

A 24-hour soak confirms the daemon is leak-free and that rollup pruning keeps the
database bounded under sustained, real traffic. The daemon ships an **opt-in
diagnostic sampler** that logs a resource snapshot on an interval; this test runs
it and reads the trend.

## Run

1. **Enable the sampler.** Set the `Diagnostics` section in the daemon's
   `appsettings.json` (beside `Beholder.Daemon.exe` — `C:\Program Files\Beholder\Daemon\`
   for an installed service, or the repo copy for `dotnet run`):
   ```json
   "Diagnostics": { "Enabled": true, "IntervalSeconds": 60 }
   ```
   Then restart the daemon (`sc stop Beholder` / `sc start Beholder`, or relaunch
   `dotnet run`).

2. **Generate real load for ~24h** — browse, stream, download, run builds — so the
   traffic engine, rollup cascade, scanner, and chain monitors all stay busy.

3. **Collect the sampler lines.** Each is one structured entry:
   ```
   perf-soak: workingSet=210MB managedHeap=48MB gc=(812/140/9) db=180MB rows=2.1M lanDevices=14
   ```
   Installed service → Windows **Event Log** (Application, source `Beholder`);
   `dotnet run` → the console. Pull them with PowerShell, e.g.
   `Get-WinEvent -ProviderName Beholder | ? Message -match 'perf-soak'`.

## What to look for

| Metric | Healthy | Red flag |
|---|---|---|
| `workingSet` / `managedHeap` | rises early, then plateaus | monotonic climb over hours → a leak |
| `db` MB / `rows` | grows, then plateaus as pruning catches up | unbounded growth → a tier isn't pruning |
| `gc` (gen0/1/2) | gen-2 rare | frequent gen-2 → allocation churn worth profiling |

## If something grows

Identify the surface from the figures (a climbing `db`/`rows` points at a table;
climbing memory at a hot path) and fix the root cause — a missing eviction, an
unpruned rollup tier, or a per-event allocation. Re-run the soak to confirm the
curve flattens.
