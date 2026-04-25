# Beholder.Tools.DnsCacheProbe

Containment-safe trial-and-error harness for finding a working prototype of
the undocumented `DnsGetCacheDataTableEx` export in `dnsapi.dll`.

## Why this exists

`Beholder.Daemon.Windows.EtwDnsCache` preloads the Windows DNS resolver cache
at startup via `DnsGetCacheDataTable`, an undocumented `dnsapi.dll` export.
On Win11 22H2+ that export still resolves but returns
`ERROR_INVALID_FUNCTION` (status `1`); the working implementation is
believed to have moved under the sibling export `DnsGetCacheDataTableEx`,
whose prototype is **also undocumented**. A previous guess (single
`out IntPtr` parameter) caused a `0xC0000005` access violation that
crashed the daemon at startup. This tool exists so we never repeat that
mistake against production code.

See `docs/decisions/004-dns-cache-preload-undocumented-api.md` for the full
context.

## How it works

Each "candidate" is a separate hypothesis about the function's signature.
Running the probe with `--candidate <name>` invokes that hypothesis and
writes a `ProbeResult` JSON to `--out <path>`. If the hypothesis is wrong,
the call corrupts the stack and the process exits with the AV code — the
output file is never written, the parent infers a crash from the missing
file, and other candidates remain unaffected.

**Admin is not required.** The probe only reads from `dnsapi.dll`; no ETW,
no firewall, no kernel.

## Usage

```powershell
# List the candidate names this probe knows about.
dotnet run --project Beholder.Tools.DnsCacheProbe -- --list

# Run a single candidate.
dotnet run --project Beholder.Tools.DnsCacheProbe -- `
    --candidate ex-flags-first `
    --out probe-ex-flags-first.json

# Run all candidates back to back. Each gets its own process so an AV
# in one doesn't affect the next.
foreach ($c in @(dotnet run --project Beholder.Tools.DnsCacheProbe -- --list)) {
    dotnet run --project Beholder.Tools.DnsCacheProbe -- `
        --candidate $c --out "probe-$c.json"
}
```

## Output

`ProbeResult` JSON, written to the path given by `--out`:

```json
{
  "Candidate": "ex-flags-first",
  "Outcome": "ok",
  "Status": 0,
  "TablePtrHex": "0x000001A4D2E80F90",
  "EntriesWalked": 47,
  "SampleNames": ["github.com", "fonts.gstatic.com", "..."],
  "ErrorDetails": null
}
```

`Outcome` is one of:

- `ok` — call returned `ERROR_SUCCESS`, table walked, sample names look like
  real DNS query names. **This is the goal.**
- `non_zero_status` — call returned without crashing but the status code
  is not zero. The candidate has the right shape (no AV) but the wrong
  flags / context.
- `null_table` — call succeeded but returned a null table pointer. Equally
  valid: empty cache.
- `invalid_strings` — call succeeded and returned a non-null table, but
  walking the linked list either failed at `Marshal.PtrToStructure`/
  `PtrToStringUni`, or the names don't look like plausible DNS names.
  Likely a wrong-signature false positive.
- `managed_exception` — a managed exception was caught (rare; included for
  completeness).
- *(file missing)* — process crashed (almost certainly an AV from the
  wrong native call signature). Parent infers from absent file.

## What lives where

- `Program.cs` — CLI parsing, dispatch, JSON output.
- `Candidates.cs` — `LibraryImport` declarations for each prototype
  hypothesis, the linked-list walker, and the validation heuristic.

## Safety rails

- Read-only. The probe never calls `DnsQuery_W`, `DnsFlushResolverCache`,
  or any mutating API.
- Not in the daemon's runtime call graph. Even with this project committed,
  zero changes ship to `Beholder.Daemon.Windows`. A *separate* follow-up
  commit translates whichever signature passes here into the daemon, with
  no further trial-and-error.
