# 015: MSI Packaging — A Self-Contained WiX Installer for the Daemon + UI

## Context

Through Phase 12.6 Beholder shipped only as a dev tree: build the solution, run `Beholder.Daemon.exe --install` from an elevated prompt, and launch the UI by hand. [ADR 013](013-windows-service-model.md) deferred "WiX/MSI + code-signing" as "a later packaging pass," and deliberately built the daemon's `--install` / `--uninstall` verbs to be **invokable by an installer**. This ADR records that pass (Phase 12.7): a single `.msi` that installs the daemon + UI into `C:\Program Files\Beholder`, registers the auto-start service, creates the `beholder-users` group, and drops shortcuts — turnkey, no prerequisites.

## Decision

### WiX v5, self-contained, two subfolders

The installer is a WiX project (`Beholder.Installer/Beholder.Installer.wixproj`, `WixToolset.Sdk/5.0.2`) built into a single MSI via `dotnet build`. **v5, not v6/v7:** WiX v7 hard-requires accepting the paid Open Source Maintenance Fee EULA (build error WIX7015); v5 predates that enforcement, is free, and still has the `<Files>` directory-harvest element. The toolchain is pinned via a local tool manifest (`.config/dotnet-tools.json`).

Both apps publish **self-contained win-x64** (`dotnet publish -r win-x64 --self-contained`), so the MSI carries .NET 10 and needs no runtime on the target machine. Because two self-contained apps would collide on framework DLLs in one folder, they install to **separate subfolders**: `C:\Program Files\Beholder\Daemon\` and `…\UI\`. Not trimmed — ETW/WFP/Avalonia rely on reflection. The ~93 MB MSI is the cost of bundling the runtime twice; accepted for a zero-prerequisite install.

### The installer reuses the daemon's verbs, not WiX-native service config

WiX has no primitive for a local group or `icacls` ACL hardening, and `WindowsServiceInstaller.Install()` already does service registration + group creation + ProgramData ACL in one tested path (ADR 013/014). So the MSI runs `Beholder.Daemon.exe --install` / `--uninstall` as **deferred, non-impersonated custom actions** (executing as LocalSystem — already elevated) rather than re-encoding the service config in WiX XML. `InstallService` (`Return=check`) rolls back via `RollbackInstallService` on failure; `UninstallService` runs before file removal. One source of truth for the service/group/ACL setup. (Deferred actions can't read `[INSTALLFOLDER]`, so an immediate "Set…" action resolves the daemon path into a property whose name matches the deferred action's Id.)

### Shortcuts + login auto-start; a version single-source

Start Menu + Desktop shortcuts open the UI normally; a per-user **Startup** shortcut launches `Beholder.Ui.exe --tray` so it comes up minimized to the tray at login — a new `--tray` flag, since Avalonia otherwise auto-shows the window. A new root `Directory.Build.props` sets `<Version>0.1.0</Version>` as the single source for both the assembly stamp (fixing the UI About's "0.0.0.0 / (dev build)") and the MSI ProductVersion. The license is the AGPL text wrapped as `License.rtf` for the WiX license dialog. `build-installer.ps1` orchestrates publish → strip PDBs (the native Skia/HarfBuzz PDBs are ~100 MB) → harvest.

### Not in the solution build

`Beholder.Installer.wixproj` is deliberately **not** added to `Beholder.slnx`: a `dotnet build Beholder.slnx` (the test loop) would otherwise try to build a ~93 MB MSI from whatever staging exists, and WiX builds MSIs only on Windows. The installer builds solely via `build-installer.ps1` (or an explicit `dotnet build` of the wixproj).

## Consequences

### Positive
- One double-click MSI installs everything and "just works" on a clean Windows 10/11 x64 box — no .NET, no manual `sc` / `net` / `icacls`.
- The service/group/ACL logic stays in `WindowsServiceInstaller` (DRY); the MSI is a thin packaging layer the daemon verbs already anticipated.
- Upgrades are handled by `MajorUpgrade`; uninstall deregisters the service and removes the files.

### Negative
- **Unsigned.** No code-signing certificate, so SmartScreen ("Windows protected your PC") and UAC show "unknown publisher." Unavoidable without a cert (self-signed doesn't clear SmartScreen); deferred.
- **Re-login required.** `--install` creates `beholder-users` and the daemon immediately restricts the control pipe to it (ADR 014), but the installing user's logon token only joins the group at next sign-in — so the UI can't connect until then. The finish dialog says so.
- The elevated install behavior (service registration, group, ACL) is verified by **manual smoke test**, like every other elevated path in the project — not CI. The WiX build validates only the package structure.
- ~93 MB installer (two runtime copies); uninstall leaves `%ProgramData%\Beholder` + the group behind (matches `--uninstall`).

## Out of scope (deferred / flagged)
- **Code-signing** (a cert + `signtool` over the two exes and the MSI) — the top follow-up; clears SmartScreen/UAC.
- **A Burn bootstrapper** that downloads the .NET runtime (smaller framework-dependent MSI) — rejected in favor of self-contained for a single, offline, no-prerequisite `.msi`.
- **Linux packaging** (`.deb` / systemd unit) — lands with the Linux daemon.
- **Service-upgrade timing** — `sc delete` → `sc create` across a major upgrade can race on a marked-for-deletion service; acceptable for now, revisit if smoke tests hit it.
