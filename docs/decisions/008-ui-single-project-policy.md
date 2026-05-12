# 008: UI Single-Project Policy (Platform Code Inline)

## Context

Phase 6.8 (OS-native notifications, commit `25a63a9`) added `Beholder.Ui.Windows` as the first UI-side platform project, mirroring the daemon-side `Beholder.Daemon.Windows`/`Beholder.Daemon.Linux` precedent. The mirror was structural copying from the daemon's architecture without a sanity check on whether the underlying trade-off matched.

**Real LOC inventory:**

| Project | Platform-specific LOC | Files | OS subsystems touched |
|---|---|---|---|
| `Beholder.Daemon.Windows` | ~3,500 | 11 | ETW (NT Kernel Logger, DNS-Client provider, PktMon provider), WFP/INetFwPolicy2, Win32 `version.dll` P/Invoke, `WinVerifyTrust`/X509 cert chain, `dnsapi.dll` undocumented exports |
| `Beholder.Daemon.Linux` (planned) | likely ~3,000+ | TBD | netlink, nftables, libelf or `/proc` parsing, GPG/openssl-based signature verification |
| `Beholder.Ui.Windows` (before this ADR) | **60** | **1** | One notification API (`Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder`) |
| `Beholder.Ui.Linux` (hypothetical) | likely 80–150 | 1 | D-Bus `org.freedesktop.Notifications` |

The daemon's platform delta is **large and growing**: every new OS-level capture mechanism, every new firewall API, every new identity verification primitive is platform-specific. The daemon split earns its keep — it isolates an entire dependency surface and is the natural unit of "swap this for another OS."

The UI's platform delta is **small and bounded**: Avalonia abstracts almost all OS-specific UI concerns at the framework level (window chrome, clipboard, fonts, dispatcher, theming, input). What remains is a thin sliver of OS-specific behavior — currently just the notification surface. The split's overhead (a csproj, a bin/obj tree, an `InternalsVisibleTo` boundary, an `.slnx` entry, a namespace prefix in the composition root, two NuGet vuln suppressions mirrored across project files because the audit walks the project graph) outweighs the isolation benefit at 60 LOC.

The mirror produced false symmetry: a future contributor reading the project graph saw "OS-side projects exist on both daemon and UI sides" and would naturally split `Beholder.Ui.Linux` off when the Linux port lands — repeating the same overhead-for-tiny-LOC mistake.

## Decision

**The UI stays as a single project: `Beholder.Ui`.** Platform-specific UI code lives inline behind source-level conditional compilation. The daemon-side split (`Beholder.Daemon.Windows`, `Beholder.Daemon.Linux`) stays mandatory.

**Code shape:**

- Windows-specific UI code lives at `Beholder.Ui/Services/*.cs` (or other folders as needed), with the platform-specific file wrapped in `#if PLATFORM_WINDOWS` / `#endif`. Linux-specific code will mirror with `#if PLATFORM_LINUX`.
- The `PLATFORM_WINDOWS` define is set on the Windows `<PropertyGroup>` in `Beholder.Ui.csproj`. A future `PLATFORM_LINUX` define will mirror.
- Platform-specific NuGet packages are declared in `Beholder.Ui.csproj` under `<ItemGroup Condition="'$(OS)' == 'Windows_NT'">` (or `'$(OS)' != 'Windows_NT'` for Linux). The package only resolves and the audit graph only walks it on the matching OS.
- NuGet vulnerability suppressions for those packages live in the same conditional `ItemGroup` so they're only active when the vuln graph actually contains the affected dep.
- Composition-root selection uses `OperatingSystem.IsWindows()` (and analogous for Linux) inside an `#if PLATFORM_*` block. Defensive runtime check covers WSL/Mono edge cases inside the Windows-built binary.
- `INotificationService` (the platform abstraction interface) stays in `Beholder.Core`. The interface boundary is the same as the daemon-side abstractions; only the project boundary differs.

**Daemon-side stays split.** No change to `Beholder.Daemon.Windows` or `Beholder.Daemon.Linux`. New daemon-side platform implementations land in those projects.

## Trigger for revisiting

Re-split UI into a `Beholder.Ui.<OS>` project if **any** of the following lands:

1. **>500 LOC of platform-specific UI code** for a single OS. The current Windows delta is 60 LOC; the projected Linux delta is 80–150 LOC. Both well below the threshold. A platform that needs custom shell integration, OS-specific accessibility, or a custom system-tray surface might push past 500 LOC.
2. **Divergent platform-specific UX** that needs separate views, viewmodels, or styles — not just service-layer differences. If a future macOS port needs a different menu-bar structure that can't be expressed as a one-VM swap, that justifies a separate UI project.
3. **A platform-specific dependency that can't be expressed as a single conditional `PackageReference`.** If a future OS-toast equivalent requires its own SDK tooling (e.g., a Win App SDK MSIX packaging step, or a macOS code-signing flow that ships its own csproj-level config), the build-system pressure may push toward a separate project.

Until any of those land, the inline shape stays.

## Consequences

**Pro:**

- One fewer project, csproj, `bin/obj` tree, `InternalsVisibleTo` boundary, and `.slnx` entry. Solution structure reads cleaner; composition root reads cleaner (`new WindowsNotificationService(...)` rather than `new Beholder.Ui.Windows.WindowsNotificationService(...)`).
- Source-level `#if PLATFORM_WINDOWS` guards are immediately visible to anyone reading the file — no jump to a separate project required to find the Windows-only branch.
- Conditional `PackageReference` keeps the platform package off Linux build/audit graphs entirely.
- A future Linux notification impl drops into `Beholder.Ui/Services/LinuxNotificationService.cs` next to its Windows sibling, in the same folder, same project — the symmetry is at the file level instead of the project level.

**Con:**

- Deviates from the daemon-side precedent. Future contributors reading the project graph might re-split without checking the threshold. This ADR is the speed-bump.
- `Beholder.Ui.csproj` is slightly noisier: one extra `<ItemGroup Condition>` per platform-specific package, one extra audit suppression block. Worth it because the alternative is a whole project.

**Neutral:**

- Conditional TFM on `Beholder.Ui.csproj` and `Beholder.Tests.csproj` stays. The Windows toast package only resolves on `net10.0-windows10.0.17763.0`; the TFM-swap shape is independent of whether the package lives in a separate project or inline.
- `Beholder.Tests.csproj` doesn't change structure — it still depends on `Beholder.Ui` (which now hosts the Windows code conditionally). The vuln-suppression comment updates to reference `Beholder.Ui.csproj` directly instead of the deleted `Beholder.Ui.Windows.csproj`.
- All 848 tests pass unchanged. The composition root's runtime behavior is identical: `INotificationService` boxing, `Notify` call, `AlertActivated` event, `Dispose` lifecycle — all the same.

## Cross-references

- **`CLAUDE.md` Architectural Rule #2** — updated in the same commit as this ADR to add the UI exception clause.
- **`docs/ARCHITECTURE.md`** — project dependency graph commentary updated with "daemon-mandatory, UI-inline" paragraph.
- **`docs/PRINCIPLES.md` §DIP** — footnote about the UI's smaller-scale application of the same DIP principle.
- **`docs/phases.md`** — §1 status summary patched, §2 Phase 6.8 entry gets a checkpoint addendum, §3 Phase 6.8 lessons gets the "mirror-the-precedent needs LOC sanity check" bullet, §5 known-gaps entry on Linux refreshed to point to the new shape.
- **`README.md`** — project structure list updated.
