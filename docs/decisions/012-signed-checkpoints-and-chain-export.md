# 012: Signed Checkpoints + Signed Chain Export — The Phase 11 Audit-Chain Trust Model

## Context

Beholder's `event_log` is a chain-hashed, append-only record of every state-changing event (firewall rule writes, process first-seen, binary-hash changes, chain-integrity alerts). Each row commits to its predecessor:

```
row_hash[n] = SHA-256( seq(8) ‖ ts_unix_ns(8) ‖ kind(4) ‖ payload ‖ row_hash[n-1] )
```

all fields big-endian, `row_hash[-1]` (the genesis previous-hash) defined as 32 zero bytes. This is the [`ChainHasher`](../../Beholder.Core/ChainHasher.cs) contract and it predates Phase 11. It gives **internal consistency**: flip any byte of any row's payload and every subsequent `row_hash` fails to recompute, so a verifier walking the chain catches the edit.

A plain hash chain has one fatal gap, though: it only resists **partial** tampering. An attacker with write access to the SQLite file can rewrite a row *and* recompute every downstream `row_hash` — a **cascaded rewrite**. The result is a chain that is internally perfect and that a hash walk accepts unconditionally. Nothing in the chain itself distinguishes the original history from a fabricated one, because the attacker controls every input the hash depends on.

Phase 11 closes that gap and adds external attestability, in three sub-phases:

- **11.1 — Signed checkpoints.** Periodically sign the chain head with an Ed25519 key the attacker doesn't hold. A signature over a past head is something a file-level rewrite cannot reproduce.
- **11.2 — Verify-from-anchor + cascaded-rewrite detection.** Make verification anchor on the latest usable signed checkpoint: skip re-walking attested rows (an O(n)→O(Δ) win on every periodic re-verify) *and* treat a signature-valid-but-live-mismatched checkpoint as proof of tampering rather than a cache miss.
- **11.3 — Signed chain export.** Let the user get the chain *out* — a signed JSON envelope any third party can verify for both authenticity (the daemon's signature) and internal consistency (recompute the embedded hash chain), with no access to Beholder's database or key material.

This ADR documents the resulting trust model end-to-end: the three primitives, the six-case verify decision tree, the export envelope contract, and the residual gaps. It is referenced by code comments in [`CheckpointSignerService`](../../Beholder.Daemon/Pipeline/CheckpointSignerService.cs), [`ChainVerifier`](../../Beholder.Daemon/Storage/ChainVerifier.cs), and [`ChainExporter`](../../Beholder.Daemon/Storage/ChainExporter.cs).

## Decision

### Ed25519 over the alternatives

The signing primitive is **Ed25519** (via NSec.Cryptography, libsodium-backed), the same choice for both checkpoints and exports. Rationale:

| Candidate | Why not |
|---|---|
| **HMAC-SHA256** | Symmetric — the verifier needs the same secret the signer used. A third party verifying an export would need Beholder's secret key, defeating the point. Cascaded-rewrite detection would also fail: anyone who can write the chain file can read the HMAC key beside it and re-MAC the head. |
| **RSA-2048/PSS** | Works, but 256-byte signatures and slow keygen for no benefit. Checkpoints are written hourly and exports embed one signature; neither needs RSA's properties. |
| **ECDSA-P256** | Comparable, but requires a per-signature nonce whose reuse leaks the private key. Ed25519 is deterministic — no nonce to mishandle. |
| **Ed25519** | 32-byte keys, 64-byte signatures, deterministic, fast, no parameter choices to get wrong. Chosen. |

The keypair lives in `data/keys/` as three files, written by [`FileCheckpointKeyProvider`](../../Beholder.Daemon/Storage/FileCheckpointKeyProvider.cs):

- `checkpoint-private.bin` — 32-byte raw Ed25519 seed (the secret; daemon-only, never leaves the host).
- `checkpoint-public.bin` — 32-byte raw public key (embedded in every export so third parties can verify offline).
- `checkpoint-key-id.txt` — `key_id`, the lowercase hex of the first 8 bytes of `SHA-256(public_key)` (16 hex chars). A short, stable, non-secret label so a verifier can tell which key signed what across rotations.

### 11.1 — Signed checkpoints

[`CheckpointSignerService`](../../Beholder.Daemon/Pipeline/CheckpointSignerService.cs) is an `IHostedService` driven by a `PeriodicTimer` at `CheckpointOptions.SigningInterval` (default **1 hour**). Each tick reads the current chain head and writes a checkpoint row signing this **fixed 48-byte payload**, defined once in [`CheckpointSignaturePayload`](../../Beholder.Core/CheckpointSignaturePayload.cs):

```
seq(8) ‖ row_hash(32) ‖ ts_unix_ns(8)        // all big-endian
```

The payload is defined in exactly one place so the signer and the verifier reconstruct byte-identical input by construction — the same "canonical representation lives once" rule `ChainHasher` follows for rows. The `ts_unix_ns` signed is the checkpoint's own stored timestamp, not the head row's; the verifier rebuilds the payload from the stored checkpoint columns, so signing any other value would make verification fail.

A checkpoint signs the head's `row_hash`, and because the chain is a hash chain, that single hash **transitively commits to every row at or below `seq`**. One signature attests an arbitrarily long prefix.

### 11.2 — The six-case verify decision tree

[`ChainVerifier.VerifyAsync`](../../Beholder.Daemon/Storage/ChainVerifier.cs) decides how to verify by examining the latest checkpoint. The branching is the heart of the trust model — each case is a distinct (signature × live-chain) state with a deliberate response:

| # | Condition | Response | Why |
|---|---|---|---|
| 1 | `forceFull` requested (mandatory startup verify, paranoid audit) | Full O(n) walk | The caller explicitly wants no shortcut. |
| 2 | No checkpoint exists (young chain, or signing disabled) | Full walk | Nothing to anchor on. |
| 3 | Checkpoint signature **doesn't** verify under the current key | Full walk, log info | Benign: the key was rotated (files deleted + regenerated), so old checkpoints are signed by a key we no longer hold. Operational, not tampering. |
| 4 | Signature valid, but the signed `seq` is **absent** from the live chain (truncation) | Full walk, log warning | Suspicious, but the full walk is authoritative and will report any real break. |
| 5 | Signature valid, signed `seq` present, but the live `row_hash[seq]` **≠** the signed `row_hash` | **FAILURE — do not fall back** | The chain head was altered *after* it was attested. This is the cascaded-rewrite catch: a full walk would accept the rewritten chain, so falling back here would erase the one piece of evidence. Reporting failure is the entire reason the signature exists. |
| 6 | Signature valid, signed `seq` present, live `row_hash[seq]` **=** signed `row_hash` | Anchor confirmed: count the attested prefix as verified, walk **forward** from `seq+1` only | The expensive prefix is already attested by the signature; only post-anchor rows need re-hashing. |

Case 5 is the security-critical branch and the reason verification is *not* a plain cache: the anchor is a tamper-evidence mechanism first and an optimization second.

**Reported row count (the 11.2 fix).** An anchored success reports `attestedPrefix + forwardRowsVerified`, where `attestedPrefix = checkpoint.Seq + 1` (rows `0..seq`). Reporting only the forward-walked delta would show `0 rows verified` whenever the checkpoint sits at the head — technically defensible (zero rows were re-hashed) but actively misleading in the UI, since the signature *does* vouch for the whole prefix. The result also carries `WithAnchor(seq, keyId)` so the UI and proto surface which checkpoint anchored the run.

### 11.3 — Signed chain export envelope

[`ChainExporter`](../../Beholder.Daemon/Storage/ChainExporter.cs) (single responsibility: assemble + sign; rows are read for it by the RPC handler via `IEventStore.ReadRangeAsync`) produces a self-verifying JSON envelope:

```jsonc
{
  "body": {
    "format_version": 1,
    "daemon_version": "…",
    "exported_at_unix_ns": 0,
    "from_seq": 0, "to_seq": 0,          // requested range; (0,0) = whole chain
    "event_count": 0,
    "events": [
      { "seq": 0, "ts_unix_ns": 0, "kind": "…",
        "payload_b64": "…", "prev_hash_b64": "…", "row_hash_b64": "…" }
    ]
  },
  "signature_alg": "Ed25519",
  "key_id": "…",
  "public_key_b64": "…",                 // the daemon's checkpoint public key
  "signature_b64": "…"
}
```

The signature is a **detached signature over a digest of the canonical body**:

```
signature = Ed25519.Sign( SHA-256( canonicalUtf8(body) ) )
```

This honors the user's "sign the entire envelope, events included" requirement **without** the fragility of signing raw JSON text. Canonical form is pinned by one `static readonly JsonSerializerOptions` (`WriteIndented = false`, fixed property order via `[JsonPropertyOrder]` on every envelope record, relaxed escaping) — the single source of truth both the signer and any verifier must use. Verification re-serializes `body` with those exact options, re-digests, and checks; the round-trip is symmetric by construction. This is the same approach JWS detached signatures and Sigstore bundles use for the identical problem. (Checkpoints sign their 48-byte payload directly; exports sign `SHA-256(body)` because the body is variable-length — both reduce to "sign a fixed-size input.")

[`ChainExporter.TryVerify`](../../Beholder.Daemon/Storage/ChainExporter.cs) is the reference verification routine, exposed for tests and documented here so a third party can reproduce it in ~30 lines. **Two independent guarantees** a receiver can check, neither requiring access to Beholder's database or private key:

1. **Authenticity** — re-serialize `body` canonically, `SHA-256`, verify `signature_b64` against `public_key_b64` (cross-check `key_id` and `public_key_b64` against a trusted `checkpoint-public.bin` out of band). Proves the daemon produced this exact body.
2. **Internal consistency** — for each event, recompute `row_hash = SHA-256(seq ‖ ts_unix_ns ‖ kind ‖ payload ‖ prev_hash)` (genesis `prev_hash` = 32 zero bytes) and confirm it matches `row_hash_b64`, and that each event's `prev_hash` equals the previous event's `row_hash`. Proves the chain is unbroken independently of the signature.

The export smoke test ships a Python verifier (mirroring the 11.1 Python checkpoint verifier) that performs both checks, proving the contract cross-implementation.

### Transport and read-only semantics

- **Message size.** A full-chain export can exceed gRPC's default 4 MB. `MaxSendMessageSize`/`MaxReceiveMessageSize` are raised to **64 MB** on both the daemon's `AddGrpc` options and the UI channel. Even ~100k events (low tens of MB of JSON) fit comfortably; streaming export is the v2 escape hatch if a chain ever exceeds the cap.
- **`ExportChain` is read-only and un-audited.** Exporting reads rows and signs them; it does not mutate the chain and deliberately appends **no** event of its own (same as `VerifyChain`). Range validation (`from_seq ≥ 0`, `to_seq ≥ 0`, `from ≤ to` when both non-zero; `to_seq = 0` means "to head") flows through the existing `ExecuteQueryAsync` helper, which maps `ArgumentException` → gRPC `InvalidArgument`.
- **The UI never touches keys or the database.** The daemon signs; the UI receives bytes over the local IPC channel and writes them to a user-chosen path through an `IFileWriter` seam. The daemon — which runs elevated — never writes to user-chosen paths.

## Consequences

### Positive

- **Cascaded rewrites are detectable.** The one attack a plain hash chain cannot survive is now caught (case 5), provided the attacker never held the private key during the window they rewrote.
- **Verification is O(Δ), not O(n), on the common path.** Hourly re-verify re-hashes only rows added since the last checkpoint; the attested prefix is free.
- **The chain is externally attestable.** A user can hand an auditor a signed file and the auditor verifies it with stock crypto libraries — no Beholder install, no DB access, no trust in the UI.
- **One signing primitive, defined once.** Ed25519 + a single canonical-payload rule for checkpoints and a single canonical-body rule for exports. No bespoke serialization in two places to drift apart.

### Negative

- **Trust roots in the private key file.** If `checkpoint-private.bin` is stolen *and* the chain rewritten before the next legitimate checkpoint, the attacker can forge a consistent signed history. The key is daemon-only and host-local, but it is not HSM-backed (see gaps). This is the irreducible trust assumption: signatures move the bar from "anyone with file write" to "someone who also holds the signing key."
- **Key rotation silently degrades old anchors to full walks (case 3).** Deleting the key files regenerates a new keypair; pre-rotation checkpoints no longer verify and fall back to O(n). Correct and safe, but a user who rotates keys loses the anchor optimization until the next checkpoint under the new key.
- **Export is a point-in-time copy.** It is not a live feed and carries no freshness proof beyond `exported_at_unix_ns`. A stale export is still authentic for the range it covers.

## Out of scope (residual gaps / deferred)

- **HSM / OS-keystore-backed keys.** The private key is a file on disk. Binding it to a TPM or DPAPI/keyring is a hardening item, not a Phase 11 deliverable.
- **Key rotation with retained trust.** No rotation that keeps old checkpoints verifiable (e.g. a signed key-succession record). Today, rotation = regenerate + degrade to full walk.
- **Streaming export** for chains beyond the 64 MB unary cap — v2 server-streaming chunked export.
- **Import / restore.** Export is one-way (forensic copy); there is no "load an exported chain back in."
- **Compression / encryption of the export.** Plain UTF-8 JSON. The signature provides authenticity, not confidentiality, and the payloads (firewall rules, process paths, LAN devices) are not secret. A `--gzip` is trivial to add later.
- **A range-picker UI.** The `ExportChain` RPC carries `from_seq`/`to_seq` (exercised by tests + grpcurl), but the v1 Settings button exports the full chain only. A date/seq range control is a deferred UI nicety.
- **A standalone verifier binary.** This ADR + `TryVerify` document the contract precisely enough to write one; Beholder ships none.
