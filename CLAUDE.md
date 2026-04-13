# Beholder NMT — Claude Code Instructions

You are working on **Beholder NMT** (Network Monitoring Tool), an AGPL-3.0 licensed cross-platform network monitoring and firewall management application written in C# targeting .NET 10.

## Mandatory Pre-Work

Before planning, writing, or modifying ANY code you MUST read and internalize:

1. `docs/ARCHITECTURE.md` — solution structure, project responsibilities, data flow, IPC contracts
2. `docs/CODING_STANDARDS.md` — formatting, naming, file organization, banned patterns
3. `docs/PRINCIPLES.md` — SOLID, DRY, CLEAN code, and design rules
4. This file in its entirety

For any phase that involves UI work, also read:

5. `docs/UI_QUALITY_STANDARDS.md` — responsive sizing, required states, banned patterns, verification requirements

If you skip these documents you WILL produce code that contradicts established patterns and creates unnecessary refactoring work. Read them. Every time.

## Project Identity

- **Name**: Beholder NMT (Network Monitoring Tool)
- **License**: AGPL-3.0-or-later (daemon + UI). Future proprietary components (aggregator, enterprise management) are OUT OF SCOPE.
- **Platforms**: Windows (primary, service-based), Linux (secondary, systemd-based)
- **Framework**: .NET 10, Avalonia 12 (UI), gRPC (IPC + uplink protocol)
- **Database**: SQLite via Microsoft.Data.Sqlite
- **GeoIP**: DB-IP Lite (CC BY 4.0) via MaxMind.Db reader, country-level only (8 MB MMDB)

## Solution Layout

```
Beholder.Core              — Models, interfaces, shared logic. Zero OS dependencies.
Beholder.Protocol          — gRPC .proto files and generated code. Both client and server stubs.
Beholder.Daemon            — Host process: DI, config, scheduling, IPC server, SQLite storage.
Beholder.Daemon.Windows    — ETW network provider, WFP/INetFwPolicy2 firewall controller.
Beholder.Daemon.Linux      — Netlink/proc provider, nftables firewall controller.
Beholder.Daemon.GeoIp      — IP-to-country resolution using DB-IP Lite MMDB.
Beholder.Daemon.Uplink     — Outbound gRPC client, connection state machine, retry logic.
Beholder.Ui                — Avalonia desktop app, MVVM, all views and controls.
Beholder.Tests             — Unit and integration tests.
Beholder.Tests.UplinkStub  — Reference gRPC server stub for uplink testing.
```

## Architectural Rules — Never Violate These

1. **The daemon is the sole authority.** All network data collection, firewall enforcement, GeoIP resolution, and chain hashing happen in the daemon. The UI is a thin display client. Never put business logic in the UI.

2. **Platform code is isolated.** `Beholder.Daemon.Windows` and `Beholder.Daemon.Linux` implement interfaces defined in `Beholder.Core`. They are loaded conditionally at runtime. Never reference Windows-only or Linux-only APIs from any other project.

3. **The UI connects to the local daemon only.** Communication is via gRPC over named pipe (Windows) or Unix domain socket (Linux). The UI never touches the network directly, never reads the MMDB, never writes to SQLite.

4. **The uplink is outbound-only and off by default.** The daemon dials out to a configured aggregator. It never opens inbound ports for remote connections. The uplink is disabled unless the user explicitly enables it in config.

5. **The hash chain covers all mutable events.** Every event that changes system state (firewall rules, process first-seen, binary hash changes, chain integrity alerts) is appended to the chain-hashed event log. The chain is append-only. Rows are never deleted or updated.

6. **Interfaces live in Core, implementations live in their owning project.** `IFlowSource` is in Core. `EtwFlowSource` is in Daemon.Windows. `NetlinkFlowSource` is in Daemon.Linux. No exceptions.

7. **The protocol is the contract.** `.proto` files in `Beholder.Protocol` define the IPC surface between daemon and UI, and the uplink surface between daemon and aggregator. Changes to `.proto` files are breaking changes — treat them as such.

## Code Quality Directives

These are not guidelines. They are requirements. Every line of code must satisfy ALL of them.

### Clarity Over Cleverness
- Write code that a mid-level developer can understand on first read without documentation
- If you need a comment to explain WHAT the code does, the code is too clever — rewrite it
- Comments explain WHY, never WHAT or HOW
- No abbreviations in names except universally understood ones (IP, TCP, UDP, HTTP, UI, DB, OS, ID, DNS, ASN, GeoIP, MMDB, gRPC)

### Minimal Surface Area
- Every public type and member must justify its visibility — default to `internal` or `private`
- Every method parameter must be used
- Every class must have exactly one reason to change
- If a class has more than ~200 lines, it is almost certainly doing too much — split it
- If a method has more than ~30 lines, extract sub-methods with descriptive names

### No Dead Weight
- No commented-out code. Ever. That is what version control is for.
- No TODO/HACK/FIXME without an associated GitHub issue number
- No unused `using` directives
- No empty catch blocks
- No methods that only call another method with the same signature (pointless wrappers)

### Defensive by Default
- Validate all public method inputs with guard clauses at the top
- Use `ArgumentNullException.ThrowIfNull()` and `ArgumentException.ThrowIfNullOrWhiteSpace()` (.NET 10 has these)
- Nullable reference types are enabled solution-wide. No `#nullable disable`. No suppress with `!` without a comment explaining why.
- All async methods must accept and honor `CancellationToken`

### Testing
- Every public interface in Core must have corresponding unit tests in Beholder.Tests
- Test names follow: `MethodName_Condition_ExpectedResult`
- No test should depend on another test's execution order or state
- Use `Arrange / Act / Assert` structure — no comments needed if the sections are obvious
- Mock external dependencies (file system, network, OS APIs) — never hit real resources in unit tests

## Commit Hygiene

- Atomic commits: one logical change per commit
- Commit message format: `area: short description` (e.g., `daemon/etw: implement per-process byte counting`)
- Never commit generated files, build artifacts, or IDE-specific config (check .gitignore)

## When In Doubt

- Favor explicitness over convention
- Favor composition over inheritance
- Favor immutability over mutability
- Favor small focused types over large monolithic ones
- Favor failing fast and loudly over silently swallowing errors
- Ask yourself: "Would I understand this code after not looking at it for six months?" If no, rewrite.
