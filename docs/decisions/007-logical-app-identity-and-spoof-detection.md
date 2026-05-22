# 007: Logical App Identity for NewProcess Dedup + Authenticode Spoof Detection

## Context

Phase 7's `NewProcessDetector` shipped with the spec from ARCHITECTURE.md: `NewProcess` fires "once per unique path, ever." Real-world manual smoke testing surfaced a UX problem this spec doesn't capture.

**Squirrel-style auto-updaters** (Discord, Slack-old, GitHub Desktop, many Electron apps) install each version into a per-version subfolder:

```
C:\Users\Vane\AppData\Local\Discord\app-1.0.9225\Discord.exe
C:\Users\Vane\AppData\Local\Discord\app-1.0.9235\Discord.exe   ← after auto-update
```

Each new version is a "new path" by the original spec, so each Discord update fires another `NEW PROCESS` alert. The user sees Discord alert twice within minutes of a silent background update — noise, not signal.

This is industry-wide. Multiple shipping Windows network-monitoring and firewall products have the identical bug — acknowledged in public bug trackers, unfixed in all of them. The handful of products that "fixed" it did so only for Microsoft Store / MSIX apps (where the OS provides a stable identity); Squirrel-deployed unpackaged executables still produce noise everywhere. macOS-only tools sidestep the problem via bundle identifiers, an OS primitive that has no Windows equivalent.

Windows lacks an OS-provided "logical app identity" primitive, so every Windows network monitor inherits this problem. We can do better.

A naive fix — using PE VersionInfo (`CompanyName` + `ProductName`) as the dedup key — has two problems:
1. **Spoofable.** VersionInfo is unsigned PE metadata; any C# project sets it via `<Company>` and `<Product>`. Malware could claim to be `(Discord, Inc., Discord)` and silently dedup against the legitimate Discord registration.
2. **Doesn't distinguish dual installs.** A user running both per-user (`AppData\Local\Discord`) and system-wide (`Program Files\Discord`) installs would have them collapse into a single "Discord" entry.

## Decision

**Logical identity:**

```
identity = (CompanyName, ProductName, InstallRoot)
```

Where:
- `CompanyName` and `ProductName` come from the binary's PE VersionInfo (Win32 `GetFileVersionInfo`).
- `InstallRoot` is computed dynamically: walk the binary's path ancestors and return the first ancestor folder whose name matches `ProductName` (case-insensitive, exact segment match). Returns null if no ancestor matches.

Discord 9225 and 9235 both report `(Discord, Inc., Discord)` and both resolve their install root to `C:\Users\Vane\AppData\Local\Discord` → same logical identity → silent. A second Discord at `C:\Program Files\Discord` resolves to a different install root → fires its own alert correctly.

**Spoof resistance: Tier 2 — Authenticode Subject + Issuer chain match.**

When a known logical identity is reused with a different signing publisher, fire a `HashChanged` alert with elevated severity carrying a "Publisher mismatch" summary. We compare:
- `SubjectCn` (e.g., `CN=Discord Inc.`) — the publisher's claimed identity
- `IssuerCn` (e.g., `CN=DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1`) — the CA chain anchor

Both validated via `WinVerifyTrust` before being trusted (catches expired/revoked/untrusted-root certs). This means a spoof requires:
- Compromising a trusted CA OR
- Convincing a trusted CA to issue a code-signing cert under a chain that matches the spoofed Subject — which is the same trust assumption every Windows security tool already makes.

**Why Tier 2 specifically (vs Tier 1 Subject-only or Tier 3 thumbprint pinning):**

| Tier | What it pins | False-positive risk |
|---|---|---|
| 1: Subject CN only | `CN=Discord Inc.` string | ~zero |
| **2: Subject + Issuer chain (chosen)** | + Issuer chain anchor | ~zero (Issuer CA stable per publisher) |
| 3: Thumbprint pinning | exact cert SHA-256 | **bad** — Discord rotates certs every ~2 years; every rotation false-alerts |
| 4: Public key pinning of CA | Issuer CA root key | ~zero, parsing complexity not justified for v1 |

Tier 2 strikes the right balance: meaningfully harder to spoof than Tier 1 (must also match the entire CA chain), but tolerates the legitimate case of a publisher renewing their cert under the same CA.

**Fallback chain:**

When VersionInfo or the Authenticode signature is unavailable (unsigned binaries, malformed PE, or no ProductName-matching ancestor folder), the detector falls back to the existing path-based dedup. Pre-7.5 `process_registry` rows have NULL identity columns and behave identically to today.

**Spoof alert via existing `HashChanged` AlertKind.**

We deliberately reuse the existing `HashChanged` enum value rather than introducing a new `PublisherChanged` kind. Rationale:
- The user-perceived severity is the same: "a binary you trusted has changed in a way that warrants attention."
- No proto enum addition; no new UI styling work; no `KindLabel`/`KindBadgeClass` changes.
- The summary text carries the publisher-mismatch context (e.g., "Publisher mismatch for Discord: signed by CN=Fake Discord Inc. instead of trusted CN=Discord Inc.") so users can distinguish hash-drift from spoof-detection scenarios when they read the row.

## Consequences

**Schema:** 6 new nullable columns on `process_registry` (`company_name`, `product_name`, `install_root`, `cert_subject_cn`, `cert_issuer_cn`, `signature_status`) plus a partial index on `(company_name, product_name, install_root) WHERE company_name IS NOT NULL`. Migration is idempotent ALTER TABLE ADD COLUMN. Pre-7.5 rows keep NULL identity columns and continue to work via path-based fallback.

**ARCHITECTURE.md alert taxonomy update:**
- `NewProcess` is now "once per logical identity, ever" (was "once per unique path, ever").
- `HashChanged` gains the publisher-mismatch trigger.

Both reference this ADR.

**Performance:** ~30–50 ms per first-network-flow event for the identity resolution (one `GetFileVersionInfo` + one `WinVerifyTrust` call). Only fires on first sighting of a path; subsequent observations short-circuit on the path-based lookup. Negligible amortized cost.

**Cross-platform:** `IBinaryIdentityProvider` is a Core interface. `WindowsBinaryIdentityProvider` lives in `Beholder.Daemon.Windows`. Linux daemon registers no implementation; `NewProcessDetector` accepts the dependency as nullable and falls through to path-based dedup unchanged. When Linux/macOS providers eventually ship, they implement equivalent identity extraction (ELF NEEDED libraries, macOS bundle identifiers).

**Trust model:** Trusts the Windows certificate store. We validate Authenticode chains the same way `WinVerifyTrust` does — same trust roots, same revocation behavior, same OCSP/CRL handling. Beholder's spoof detection inherits Windows's trust posture; we don't try to be more paranoid than the OS itself.

**Competitive position:** Beholder becomes (to our knowledge) the first Windows network monitor that handles Squirrel-style auto-updaters silently AND detects publisher-spoofing on top. No current Windows network-monitor / personal-firewall product ships both capabilities together.

**Out of scope (deferred):**
- "Verified publisher" badge in the Alerts UI — plumbing supports it, but UI work is separate.
- Identity backfill for pre-7.5 `process_registry` rows — they remain NULL; only newly-seen paths get identity resolved.
- Public-key pinning of CA roots (Tier 4) — over-engineered for v1; reconsider if a publisher's CA legitimately changes between releases.
- Linux/macOS identity providers — different metadata model per platform; ship when those daemons stabilize.
