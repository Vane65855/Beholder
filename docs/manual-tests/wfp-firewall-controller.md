# Manual Test: WfpFirewallController

`WfpFirewallController` cannot be unit-tested against COM — `INetFwPolicy2`
requires the real Windows Firewall service, Administrator privileges, and
touches system-wide state. This runbook is the end-to-end verification.

The `FirewallRuleNameEncoder` half of the implementation is covered by
`FirewallRuleNameEncoderTests` in `Beholder.Tests`; this runbook covers
the COM interop half.

## Requirements

- Windows 10/11 or Server 2019+
- .NET 10 SDK
- Administrator terminal (Run as administrator — NOT a non-elevated
  terminal)
- Windows Firewall service running (default on every supported Windows
  build)
- `PING.EXE` available at `C:\Windows\System32\PING.EXE` (it is, on every
  supported Windows build)

## Steps

1. Build the solution from a normal terminal:
   ```
   dotnet build
   ```
   Expect zero errors, zero warnings.

2. Open an **elevated** terminal and verify elevation:
   ```
   whoami /groups | findstr "S-1-16-12288"
   ```
   A non-empty match means you are running as High Integrity (admin). If
   the match is empty, any `AddRuleAsync` call will throw `COMException`
   HRESULT `0x80070005` — the runbook's unelevated-failure step below
   deliberately exercises that path.

3. **Baseline**: from a **non-elevated** terminal, run:
   ```
   ping -n 1 8.8.8.8
   ```
   Expect a successful reply ("Reply from 8.8.8.8: bytes=32 time=…"). If
   ping already fails here, some pre-existing firewall rule or network
   problem is in the way and this runbook cannot distinguish it from the
   rule Beholder is about to add — resolve that first.

4. From the elevated terminal, run a one-shot host that resolves
   `IFirewallController` from DI and adds a block-outbound rule for
   `PING.EXE`. The easiest form is a `dotnet-script` snippet or a small
   `Program.cs` pointing at `Beholder.Daemon`:

   ```csharp
   using Beholder.Core;
   using Beholder.Daemon.Windows;
   using Microsoft.Extensions.DependencyInjection;
   using Microsoft.Extensions.Logging;

   var services = new ServiceCollection();
   services.AddLogging(b => b.AddConsole());
   services.AddSingleton<IFirewallController, WfpFirewallController>();
   await using var sp = services.BuildServiceProvider();

   var fw = sp.GetRequiredService<IFirewallController>();
   var now = DateTimeOffset.UtcNow;
   await fw.AddRuleAsync(new FirewallRule(
       id: 0,
       processPath: @"C:\Windows\System32\PING.EXE",
       direction: Direction.Outbound,
       action: FirewallAction.Block,
       source: RuleSource.Manual,
       createdAt: now,
       updatedAt: now), CancellationToken.None);
   ```

   Expect one Information log line:
   ```
   info: Beholder.Daemon.Windows.WfpFirewallController[0]
         Added firewall rule Outbound Block for C:\Windows\System32\PING.EXE
   ```

5. Open `wf.msc` → **Outbound Rules** and confirm a new rule exists with:
   - **Name**: begins with `Beholder: out|` followed by a base64 blob
   - **Action**: Block
   - **Program**: `C:\Windows\System32\PING.EXE`
   - **Enabled**: Yes
   - **Profile**: All

6. From the non-elevated terminal, run:
   ```
   ping -n 1 8.8.8.8
   ```
   Expect "General failure" or "Request timed out" — the block is
   effective. (Resolve the rule immediately if this causes trouble; it
   only affects `PING.EXE`, not your general network.)

7. From the elevated terminal, run the same one-shot host but calling
   `RemoveRuleAsync` instead:
   ```csharp
   await fw.RemoveRuleAsync(
       @"C:\Windows\System32\PING.EXE", Direction.Outbound, CancellationToken.None);
   ```
   Expect one Information log line:
   ```
   info: Beholder.Daemon.Windows.WfpFirewallController[0]
         Removed firewall rule Outbound for C:\Windows\System32\PING.EXE
   ```
   Confirm in `wf.msc` that the rule is gone.

8. Repeat step 6. Ping should succeed again.

9. **Idempotency**: from the elevated terminal, call `RemoveRuleAsync` a
   second time on the same rule. Expect a single Information log line:
   ```
   info: Beholder.Daemon.Windows.WfpFirewallController[0]
         RemoveRuleAsync: no existing rule to remove for Outbound C:\Windows\System32\PING.EXE
   ```
   No exception should be thrown.

10. **Unelevated failure path**: from a **non-elevated** terminal, run
    the `AddRuleAsync` snippet from step 4. Expect one Error log line
    mentioning "must run as Administrator" and a propagated
    `COMException` with HRESULT `0x80070005`:
    ```
    fail: Beholder.Daemon.Windows.WfpFirewallController[0]
          Access denied during AddRule for C:\Windows\System32\PING.EXE — the daemon must run as Administrator to modify Windows Firewall
    ```

## What success looks like

- The Beholder rule appears in `wf.msc` with the correct Action, Program,
  and `Beholder: out|…` Name prefix.
- Ping is blocked while the rule is present and unblocked once removed.
- Re-removing an already-gone rule succeeds silently (idempotent).
- Running unelevated produces a clear "must run as Administrator" Error
  log line and a propagated `COMException`.

## Known non-issues

- **`ListRulesAsync` returns `Id = 0` and `CreatedAt = UtcNow` for every
  rule**: expected. The controller reflects the live OS view, not the
  persistence layer's row IDs or wall-clock history. The SQLite store
  owns those columns and will join them in when it comes online.
- **Rule name looks like a garbled base64 blob**: expected. The `|` after
  the direction token is the split marker; everything after it is
  `base64(UTF-8(processPath))`. This is deliberate so process paths
  containing spaces, pipes, colons, or Unicode round-trip without
  escaping ambiguity.
- **`ListRulesAsync` only returns rules whose name starts with
  `Beholder: ` and parses cleanly**: expected. Rules created by other
  software and rules whose name has been hand-edited in `wf.msc` to
  something malformed are silently skipped — Beholder only claims
  ownership of rules it can itself parse.

## Related

- `Beholder.Daemon.Windows/FirewallRuleNameEncoder.cs` — the pure
  string-format side of the controller; unit-tested in
  `Beholder.Tests/FirewallRuleNameEncoderTests.cs`.
