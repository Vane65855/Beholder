# 013: Windows Service Model — Self-Installing LocalSystem Daemon with a ProgramData Data Directory

## Context

Through Phase 11 the daemon ran only as a **console process**: `Program.cs` built a `WebApplication` and called `app.Run()`, and the operator had to launch `Beholder.Daemon.exe` manually from an elevated prompt. That is fine for development but not for a product that must capture network telemetry continuously, survive logoff/reboot, and start before the user signs in. The `Microsoft.Extensions.Hosting.WindowsServices` package was already referenced in the daemon `.csproj` but never wired up — dead weight.

Two facts from earlier phases shaped this decision:

- **The daemon needs elevation** for ETW network capture (the NT Kernel Logger is a per-machine singleton) and for WFP / `INetFwPolicy2` firewall control. Today it relies on a manual "Run as Administrator" and fails at runtime with a clear message if unelevated (`EtwFlowSource`, `WfpFirewallController`).
- **[ADR 012](012-signed-checkpoints-and-chain-export.md) already declared the intended data location.** The `FileCheckpointKeyProvider` doc states the daemon's data folder is `ProgramData\Beholder` "when installed as a service," relying on its ACLs to restrict the Ed25519 signing key to SYSTEM + Administrators. The code never implemented this — every path hung off `AppContext.BaseDirectory\data`.

Phase 12.1 turns the daemon into an installable, auto-starting Windows service and closes that documented-but-unimplemented gap. The roadmap offered "sc.exe or WiX installer"; this ADR records the choices made.

## Decision

### Host runs as a LocalSystem service; no application manifest

`Program.cs` calls `builder.Host.UseWindowsService(options => options.ServiceName = "Beholder")`. This is a no-op when the process is not SCM-hosted, so a developer `dotnet run` is unaffected; under the Service Control Manager it installs the `WindowsServiceLifetime` (cooperating with SCM start/stop, no console) and routes logging to the **Windows Event Log**.

The service runs as **LocalSystem**, which already holds every privilege ETW and WFP require. We therefore do **not** add an `app.manifest` with `requestedExecutionLevel=requireAdministrator`: a manifest matters for interactive double-click elevation, but a LocalSystem service is elevated by definition. (The `--install` / `--uninstall` verbs *do* require an elevated caller, checked explicitly — see below.)

### Data directory: `%ProgramData%\Beholder` when service, exe-relative in dev

A new `DaemonPaths` resolver splits two roots:

- **`WritableDataRoot`** — holds `beholder.db` (+ WAL/SHM) and `keys/`. Resolves to `%ProgramData%\Beholder` when `WindowsServiceHelpers.IsWindowsService()` is true, else `AppContext.BaseDirectory\data`. So an installed service writes to a stable, upgrade-surviving, ACL-controlled location, while `dotnet run` / debugging keeps its data local and needs no elevation.
- **`ReadOnlyAssetRoot`** — always `AppContext.BaseDirectory\data`. The GeoIP MMDB and OUI registry ship beside the binary and are never written, so they stay where the build copies them (no installer step to relocate read-only assets).

`%ProgramData%\Beholder` (not `…\Beholder\data`) is the data root, matching ADR 012's wording and the ProgramData convention of namespacing by app.

### The installer hardens the key directory's ACLs — inheritance is not enough

ADR 012 claims the key folder is restricted to SYSTEM + Administrators. **ProgramData's default ACL grants the `Users` group read**, and a child folder created by the LocalSystem daemon would *inherit* that — leaving the private signing key world-readable by every local user, which would let any user forge checkpoints and defeat Phase 11's tamper-evidence. So `--install` (running elevated) creates `%ProgramData%\Beholder` and runs `icacls <dir> /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F` — strip inherited ACEs, grant only LocalSystem + Administrators. Well-known SIDs keep this locale-independent. This makes ADR 012's security claim actually true.

### Install mechanism: in-daemon `--install` / `--uninstall` / `--status`, shelling `sc.exe`

The daemon installs itself. `ServiceCommandLine.Parse(args)` (pure, unit-tested) maps the verb; `Program.cs` dispatches to `WindowsServiceInstaller` (in `Beholder.Daemon.Windows` per ADR 008, since the work is Windows-only) **before** building the host, and returns the verb's exit code. The installer shells the in-box `sc.exe` and `icacls.exe` resolved explicitly from `System32` (never via `PATH`, since they run with the caller's elevated token), with `binPath` = `Environment.ProcessPath`, `start= auto`, a description, and failure-recovery (`sc failure … restart`). The sc/icacls **argument lists** are built by pure functions and unit-tested; the actual elevated registration is a **manual smoke test**, mirroring the Administrator-only ETW tests.

WiX/MSI was rejected for now: it adds a separate toolchain, `.wxs` authoring, and code-signing for no benefit at the pre-release stage. `sc.exe` self-install is the lightest thing that delivers auto-start + recovery; a real MSI is a later packaging pass.

### The per-second `Worker` heartbeat is removed

The Worker-template heartbeat logged one line/second. Under the Event Log that is ~86,400 entries/day of noise, and the SCM already reports running state — so it's deleted (no dead weight).

## Consequences

### Positive
- The daemon is a first-class auto-start service: survives reboot, starts before sign-in, restarts on crash, runs with exactly the privileges it needs and no manifest gymnastics.
- The Ed25519 signing key is now genuinely restricted to SYSTEM + Administrators — ADR 012's claim is realized rather than aspirational.
- Dev ergonomics are untouched: `dotnet run` stays console + exe-relative; only an installed service relocates to ProgramData.
- The previously-unused `WindowsServices` package now earns its reference.

### Negative
- A console install built into the daemon `.exe` means the install path lives in product code (not a separate installer artifact). Acceptable for self-install; an MSI would supersede it.
- Data created by a prior *console* run (exe-relative) is not migrated to ProgramData — a fresh service install starts with an empty chain. Documented; acceptable for a deployment-model change.
- The elevated registration and ACL hardening are verified by manual smoke test, not CI (they need admin + a real machine).

## Out of scope (deferred / flagged)

- **The control RPC is unauthenticated TCP `localhost:50051`**, not the named-pipe-DACL'd-to-a-group transport [ARCHITECTURE.md](../ARCHITECTURE.md) and [CLAUDE.md](../../CLAUDE.md) describe. Any local process — any user — can currently connect and issue firewall commands, and making the daemon an always-on service **widens that exposure window**. Phase 12.1 does not change the transport. **Recommended follow-up (its own pass): switch to a named pipe DACL'd to the local user / a `beholder-users` group to match the documented design, or add local-caller authentication; and reconcile the docs to whatever ships.** This is the most important security item the service work surfaces.
- **WiX/MSI installer and code-signing** — later packaging pass.
- **Phase 12.5 startup OS↔SQLite firewall reconciliation** — separate sub-phase.
- **Linux**: the equivalent systemd unit + `/var/lib/beholder` data dir lands with the Linux daemon; `DaemonPaths` already degrades to exe-relative off-Windows.
