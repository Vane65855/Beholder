# 011: Manual App Identity Rules — Tier 2.5 Fallback for Squirrel-Style Updaters Without Signed VersionInfo

## Context

[ADR 007](007-logical-app-identity-and-spoof-detection.md) shipped automatic logical-app identity dedup via PE VersionInfo (`CompanyName`, `ProductName`) + Authenticode signature. For signed binaries with usable VersionInfo, `(CompanyName, ProductName, install-root)` keys a NewProcess alert to a logical app instead of a file path, so Squirrel auto-updaters like Discord, GitHub Desktop, and Slack stay silent across version bumps.

ADR 007 explicitly named the trade-off it made: **unsigned binaries** (and any signed binary without VersionInfo) fall back to path-based dedup. Every Squirrel-style update — new `app-1.0.NNNN` subfolder, same exe filename — fires a fresh `NewProcess` alert. The class of affected binaries is real and recurring:

- Custom-built or sideloaded developer tools.
- Internal IT-deployed line-of-business apps that aren't code-signed.
- VS Code Insiders / nightly / canary channels where signing state varies by build pipeline.
- Older versions of apps that haven't been re-signed.
- Open-source Squirrel-deployed apps that don't bother with a code-signing cert.

On a typical developer machine that's ~6–10 spurious "first-seen" alerts per month — small in absolute terms, but cumulative noise that erodes the "every alert is signal" trust contract the Alerts tab depends on.

ADR 007 documented the gap explicitly: *"unsigned binaries fall back to path-based dedup."* Phase 13.6 closes that gap with a manual fallback tier — the user explicitly tells Beholder "this binary is the same app across version subfolders" via a Settings rule, and the detector silences subsequent first-seen events on matching paths.

The design space had three obvious shapes:

| Shape | Description | Why we didn't pick it |
|---|---|---|
| **Heuristic auto-detection** | Detect Squirrel patterns automatically (look for `app-X.Y.Z` sibling folders + same exe filename + recent-creation timestamps). | False positives are hard to bound. The detector would need to distinguish "legitimate Squirrel update" from "user manually copied app to a versioned folder" with no reliable signal. Auto-silencing the wrong process is exactly the failure mode the chain exists to prevent. |
| **VersionInfo-only fallback (no signature requirement)** | Use `(CompanyName, ProductName)` even when the binary is unsigned. | Trivially spoofable. Any C# project sets `<Company>` and `<Product>`; an unsigned malware binary that claims to be `(Discord, Inc., Discord)` would silently dedup against the legitimate Discord registration. ADR 007's explicit reason for requiring Authenticode validation. |
| **Manual user-confirmed rules** | User picks the binary in Settings, confirms the anchor folder. Strict depth-1 match semantics + explicit user intent. | Chosen — see below. |

The third option is what every comparable security product ships for the same class of problem:
- **1Password**'s "Don't ask again for this device" / browser extension trust prompts.
- **Windows Defender**'s "Excluded items" UX.
- **Little Snitch**'s per-process rules with bundle identifier fallback to path.

The user explicitly opts an entity into a special-case list. No magic detection. The trust boundary is the user's deliberate UI action, not a heuristic the daemon makes on its own.

## Decision

### Strict depth-1 anchor + filename match semantics

A binary at path `P` matches rule `(anchor_path, filename)` iff:

```
Path.GetFileName(P).Equals(filename, OrdinalIgnoreCase on Windows)
  AND
Path.GetDirectoryName(Path.GetDirectoryName(P)).Equals(anchor_path, OrdinalIgnoreCase on Windows)
```

The double `GetDirectoryName` is the depth-1 enforcement: `P` must sit exactly one variable folder below the anchor. Both checks are case-insensitive on Windows (NTFS), case-sensitive elsewhere.

Example matches given rule `anchor_path = C:\Users\Vane\AppData\Local\Discord, filename = Discord.exe`:

| Candidate path | Matches? | Why |
|---|---|---|
| `…\Discord\app-1.0.9235\Discord.exe` | ✓ | grandparent matches; filename matches |
| `…\Discord\app-1.0.9236\Discord.exe` | ✓ | next version — same pattern |
| `…\Discord\Discord.exe` | ✗ | zero variable segments — too shallow |
| `…\Discord\v2\dist\Discord.exe` | ✗ | two segments below anchor — too deep |
| `…\Discord\app-1.0.9235\Setup.exe` | ✗ | filename mismatch |
| `…\OtherDir\Discord\v2\Discord.exe` | ✗ | grandparent is `…\OtherDir\Discord`, not the configured anchor |

The strictness is the safety guarantee: the rule cannot accidentally match a deeper file structure (e.g., a future installer that nests bin/ folders). Users with that pattern hit "no match" and either edit their rule or wait for a v2 multi-depth extension. Loud failure beats silent over-match.

A "filename"-only secondary index on the rules table keeps the lookup hot: `SELECT * FROM app_identity_rule WHERE filename = ?` returns ≤handful of rows; iterate in C# for the grandparent equality check. No path-trie data structure; no regex compilation. This is by design — the surface area is ~tens of rules per user, not thousands.

### Tier 2.5 placement in `NewProcessDetector.ProcessAsync`

The existing tier walk is:
- **Tier 1**: path-exact lookup (`process_registry` has the path → seen before, silent).
- **Tier 2**: signed logical identity (PE VersionInfo + Authenticode → same `(CompanyName, ProductName, install-root)` → silent).
- **Tier 3**: genuinely new path with no matching identity → fire `NewProcess`.

Phase 13.6 inserts **Tier 2.5** between Tier 2 and Tier 3:

```
Tier 1 (path exact)
  → Tier 2 (signed logical identity)
    → Tier 2.5 (manual rule match) ← NEW
      → Tier 3 (fire NewProcess)
```

On Tier 2.5 match: register the path silently in `process_registry` with `displayName = matchedRule.DisplayName ?? filename`, no signature/identity columns populated. Return without alerting. Mirrors Tier 2's same-publisher silent path.

The ordering is deliberate: **automatic signed-identity (Tier 2) wins over manual rules; manual rules win over path-based genuinely-new (Tier 3)**. The user's manual override defers to "the system has cryptographic proof these are the same publisher" but overrides "the path is new." This means:
- A signed Discord that updates and gains a new path still routes through Tier 2's `(CompanyName, ProductName, install-root)` dedup, even if the user also has a manual rule that would match. The signed path is the authoritative one.
- An unsigned dev-build of an internal tool routes through Tier 2.5 because Tier 2 returned null. The rule is the authority.

### Stupid in, stupid out (the one hard guard rail)

The system trusts the user's explicit rule. If the user configures an absurdly broad anchor (e.g., `C:\Users\Vane`), the daemon shrugs and silences everything one level below it. That's the user's call.

The single hard guard rail is a **validation check at ADD time** in the UI: the file the user picked must actually live exactly one level below the configured anchor. This catches typos (anchor edited to a sibling folder) and depth mismatches (file is at the anchor with no variable segment, or two+ segments deep). Inline error → SAVE disabled. This is a usability guard, not a security one — the daemon would still trust whatever you sent it over the RPC.

The validation runs continuously in the VM as the user edits the anchor field — instant feedback before SAVE is pressed.

### Three new RPCs, soft-failure on duplicate

| RPC | Shape |
|---|---|
| `AddAppIdentityRule(anchor_path, filename, display_name)` | Returns `{ success: bool, message: string, rule: AppIdentityRule }`. `success=false` on duplicate `(anchor_path, filename)` — the UI surfaces inline. |
| `RemoveAppIdentityRule(id)` | Returns `{ removed: bool }`. Idempotent: unknown id returns `removed=false`. |
| `ListAppIdentityRules()` | Returns `{ rules: AppIdentityRule[] }` in id order. |

RPC surface: 24 → 27.

The soft-failure shape on `AddAppIdentityRule` mirrors `SetLanDeviceLabel` / `TriggerScan`: duplicate-add returns `success=false` with a structured message rather than throwing `RpcException`. Hard validation errors (null `anchor_path` or `filename`) still throw `InvalidArgument`.

### Storage

```sql
CREATE TABLE app_identity_rule (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    anchor_path     TEXT NOT NULL,    -- absolute path to the stable parent folder
    filename        TEXT NOT NULL,    -- e.g. Discord.exe
    display_name    TEXT,             -- optional UI label (null when unset)
    created_at      INTEGER NOT NULL, -- Unix ns
    UNIQUE(anchor_path, filename)
);
CREATE INDEX idx_app_identity_filename ON app_identity_rule(filename);
```

`anchor_path` is stored verbatim (caller-trimmed of trailing separator). `filename` is stored verbatim (case preserved for display, matched case-insensitive on Windows at query time). Idempotent migration via `CREATE TABLE IF NOT EXISTS` + `CREATE INDEX IF NOT EXISTS` — applied automatically on next daemon start for existing installs.

### Chain audit

Two new `EventKind` ordinals append to `Beholder.Core/EventKind.cs`:
- `AppIdentityRuleCreated = 15`
- `AppIdentityRuleRemoved = 16`

One encoder file `AppIdentityRulePayloadEncoder.cs` handles both kinds with the same payload shape: `{ id, anchorPath, filename, displayName, createdAtUnixNs }`. Deterministic JSON identical in shape contract to `FirewallRulePayloadEncoder`'s — no indentation, fixed field order, symmetric `TryDecode` that returns null on malformed input.

Every Add → chain entry. Every Remove → chain entry. Failures to write the chain are logged but don't fail the RPC (mirrors `ApplyFirewallRule` — "rule is applied but unaudited" semantics; user sees success, chain has the gap, durable record is the SQLite row itself).

### UI: inline expand-to-edit pattern (no modal dialog)

The ADD RULE flow uses an inline expanded card matching the Phase 9.5 RENAME pattern from the Scanner tab — `IsAdding` boolean flips a section between read mode (rule list + ADD RULE button) and add mode (input form). Cancel returns to read mode; Save validates → RPC → on success appends the rule + returns to read mode.

The ADD card auto-detects three segments from the picked file path:
- **FILE** (read-only): `Path.GetFileName(pickedFile)`.
- **VARIABLE** (info-only display): `Path.GetFileName(Path.GetDirectoryName(pickedFile))`. The user sees this so they can sanity-check what's being treated as variable.
- **ANCHOR** (editable): `Path.GetDirectoryName(Path.GetDirectoryName(pickedFile))`. The user can move it up (less common — would weaken the match) or fix the auto-detection.

A **"Will match: …\Anchor\\<any single subfolder>\Filename"** live preview line shows exactly what behaviour the rule encodes before saving.

### File picker abstraction

New `IFilePicker` interface in `Beholder.Ui/Services/`. Prod impl `AvaloniaFilePicker` uses `TopLevel.GetTopLevel(window).StorageProvider.OpenFilePickerAsync`. Wired through `App.axaml.cs` using the existing `Func<MainWindow?>` capture-by-reference pattern from `AvaloniaClipboardWriter` (solves "TopLevel needed but VM doesn't have window reference" the same way the clipboard service already does). Test fake `FakeFilePicker` exposes a settable `PickedPath` property + an exception-throw hook, matching the `FakeShellOpener` / `FakeClipboardWriter` precedent.

The abstraction is now available for future Settings features that need user-picked paths (e.g., a future "exclude binary from monitoring" surface or a hash-pin override).

## Consequences

### Positive

- **Closes ADR 007's explicit gap** for unsigned/no-VersionInfo binaries. The "every alert is signal" trust contract holds for the long tail of dev tools, internal apps, and unsigned Squirrel deploys.
- **User intent is explicit and auditable.** Every rule add/remove appears in the chain-hashed event log with the full payload; nothing happens automatically without the user clicking SAVE.
- **Strict depth-1 prevents silent over-match.** A user who configures an anchor too high up gets "no match" rather than accidentally silencing siblings two levels deep.
- **No surface area on the Alerts tab** — no new alert kinds, no new UI styling, no new dispatch logic. The detector tier is invisible to users not in Settings.
- **File picker abstraction is reusable** for any future feature that needs user-picked paths.

### Negative

- **Manual onboarding cost.** Users with unsigned auto-updaters must explicitly add a rule per app. The first NewProcess alert on Discord (if unsigned) is still noisy; only subsequent updates are silent. Acceptable: it's a one-time action per app, and the user only adds rules for apps that have actually annoyed them.
- **No retroactive dedup.** When a rule is saved, alerts already fired for prior `app-1.0.NNNN` paths stay in the chain. Future enhancement: a "merge prior alerts under this rule" action.
- **Stupid in / stupid out.** A user who configures `C:\Users\Vane` as the anchor will silence everything one level below it. The validation guard catches typos but not user judgment errors.
- **No spoof detection on manual rules.** ADR 007's publisher-mismatch fires when a signed app's publisher changes. Manual rules typically apply to unsigned binaries (no publisher to compare). The existing `BinaryHashMonitor` SHA-256 check still runs orthogonally — that's the right tier for tamper detection on these binaries.

## Out of scope

- **Retroactive dedup of existing duplicate alerts.** When a rule is saved, alerts already fired for prior `app-1.0.NNNN` paths stay in the chain. Future enhancement.
- **Right-click "Group with similar versions" on alerts.** Bootstrap-from-alert UX — would auto-fill the file path. v2.
- **Pre-flight scan warning** ("This rule will cover N existing binaries"). Nice-to-have safety nudge. v2.
- **Spoof detection on manual rules.** Out of scope per the trade-off above.
- **`display_name` override across UI surfaces** (Alerts tab, Firewall tab, Traffic tab). `display_name` is shown ONLY in the Settings rule list for v1. Cross-cutting "label apps everywhere" is its own feature.
- **Multi-level variable paths.** Apps where the exe is two or more levels below the stable parent (e.g., `…\App\release-1\bin\App.exe`) need a v2 schema that records the variable depth. Deferred until a real user with that pattern surfaces.
- **Wildcard syntax beyond anchor+filename.** No regex, no globs, no multiple-filename-per-rule. One rule = one anchor + one filename + exactly one variable segment.
- **Linux path handling.** Rules use the OS-native separator; the feature ships Windows-first like the daemon.
