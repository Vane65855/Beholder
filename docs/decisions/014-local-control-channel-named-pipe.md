# 014: Local Control Channel — gRPC over a DACL'd Named Pipe

## Context

The daemon's local control surface — the `BeholderLocal` gRPC service the UI uses to read snapshots, toggle firewall rules, change settings, and export the chain — listened on **unauthenticated TCP `127.0.0.1:50051`** from Phase 4 through 12.1. A loopback TCP socket carries no access control: any process running as any local user, in any logon session, could connect and issue privileged commands — block/allow traffic, flip firewall enforcement, mutate settings, read the audit chain.

[ARCHITECTURE.md](../ARCHITECTURE.md) and [CLAUDE.md](../../CLAUDE.md) rule #3 had *always* described the intended transport as "gRPC over a named pipe (`\\.\pipe\beholder`), DACL'd to the local user or a `beholder-users` group" — but the code never implemented it. [ADR 013](013-windows-service-model.md) flagged this as **the most important security item** the service work surfaced: making the daemon an always-on auto-start service widened the exposure window for that open port. This ADR records closing the gap (Phase 12.6): the transport moves to a named pipe whose **DACL is the access-control mechanism**.

## Decision

### Transport: Kestrel named pipe, HTTP/2 (h2c), no TCP fallback

.NET 10 ships a native named-pipe Kestrel transport. `Program.cs` replaces `ListenLocalhost(50051, … Http2)` with `UseNamedPipes(…)` + `ListenNamedPipe(IpcEndpoint.PipeName, … Http2)`. The pipe name lives in exactly one place — `Beholder.Protocol.IpcEndpoint.PipeName` (`"beholder"`) — referenced by both the daemon listener and the UI client, so there is no magic-string drift. gRPC still speaks HTTP/2 over the pipe stream (h2c, prior knowledge), exactly as it did over TCP; only the transport changes, so the RPC contract and the in-process `BeholderLocalService` tests are untouched.

**There is no TCP fallback.** Re-opening the port on any failure path would re-open the very hole this closes; if the pipe cannot be served the daemon fails loudly rather than silently downgrading.

### The DACL is the security boundary

`BeholderPipeSecurity.Build` (Daemon.Windows, `[SupportedOSPlatform("windows")]`) constructs the pipe's `PipeSecurity`:

- **SYSTEM** (`S-1-5-18`) and **Administrators** (`S-1-5-32-544`) → `FullControl`.
- The **`beholder-users`** local group → connect rights (`ReadWrite | Synchronize`).
- **Everyone else is denied** — a pipe with an explicit DACL grants nothing it does not name.

Well-known SIDs keep this locale-independent, the same approach as `WindowsServiceInstaller.BuildHardenAclArguments` (ADR 013). The builder is a pure function of the resolved group SID and is unit-tested to grant exactly those three principals and **no** broad ones — Everyone (`S-1-1-0`), Users (`S-1-5-32-545`), Authenticated Users (`S-1-5-11`).

### INTERACTIVE fallback for dev / uninstalled runs

`beholder-users` only exists after the installer creates it. When the group can't be resolved — a developer `dotnet run`, or a fresh checkout before install — `Create()` falls back to granting the **INTERACTIVE** group (`S-1-5-4`) connect rights instead, and the daemon **logs the downgrade** at startup. This keeps `dotnet run` + the dev UI working out of the box, at the cost of admitting any interactive desktop user on an *uninstalled* dev box — an acceptable, visible trade. An installed product always has the group, so it is always restricted to the group. This mirrors the graceful-degradation posture of `NullGeoIpResolver`: never fail closed in dev, but make the reduced mode obvious.

### The installer creates the group

`--install` (already elevated, ADR 013) creates the local `beholder-users` group and adds the installing user by shelling `net.exe localgroup` from System32 — pure, unit-tested argument builders (`BuildCreateGroupArguments`, `BuildAddMemberArguments`), best-effort and idempotent ("already exists" / "already a member" are not errors). Because group membership is baked into the logon token at sign-in, the install output tells the user to **sign out and back in** before their current session can connect, and documents `net localgroup beholder-users <DOMAIN\user> /add` for adding other users. `--uninstall` leaves the group intact (an admin may have customized its membership).

### The UI connects through the pipe

`DaemonClient` (Windows) builds its `GrpcChannel` over a `SocketsHttpHandler` whose `ConnectCallback` opens a `NamedPipeClientStream(".", IpcEndpoint.PipeName, …)`; the channel address is a placeholder since the callback performs the real connect. The reconnect/backoff loop, the health probe, and the shared-channel use by `DaemonStreamSubscriber` are all unchanged — one channel construction swapped behind `#if PLATFORM_WINDOWS`. The TCP path is retained behind `#else` as the placeholder for a future Linux Unix-domain-socket client.

## Consequences

### Positive
- The control surface is no longer reachable by arbitrary local processes: the pipe DACL admits only SYSTEM, Administrators, and members of `beholder-users`. The design ARCHITECTURE.md / CLAUDE.md rule #3 always described is now real rather than aspirational.
- No open localhost port — `netstat` no longer shows `50051`; there is nothing for a port scanner or another local user to discover.
- The daemon remains the **sole authority** (it still validates every inbound call); this is defense-in-depth at the transport, not a substitute for command validation.

### Negative
- **Group membership requires a re-login.** A just-installed product cannot connect from the installing user's *current* session until they sign out/in (or reboot). Documented in the install output; unavoidable with token-baked group membership.
- The dev INTERACTIVE fallback admits any interactive user on an uninstalled box. It is visible (logged), scoped to development, and never the installed posture.
- The elevated group creation and the live DACL are verified by **manual smoke test** (admin + a real machine), like the ETW and sc/icacls work; only the pure builders are unit-tested.

## Out of scope (deferred / flagged)
- **Linux** Unix-domain-socket mirror (`/run/beholder.sock`, permission-restricted) — lands with the Linux daemon; `DaemonClient` keeps the TCP `#else` branch as the placeholder.
- **Per-caller application auth** (tokens / capabilities) layered on top of the DACL — the OS DACL is the standard, sufficient control for local IPC; app-level auth is a later concern only if a least-privilege split *within* `beholder-users` is ever needed.
- **Auto-detecting the console-session user** (vs the installing user) at install time, and **removing the group** on uninstall — minor refinements.
