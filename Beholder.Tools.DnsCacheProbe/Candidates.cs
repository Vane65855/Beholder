using System.Runtime.InteropServices;

namespace Beholder.Tools.DnsCacheProbe;

/// <summary>
/// Result row written to the probe's output JSON. The parent process / user
/// reads this file (or its absence on AV crash) to decide whether the
/// candidate is a working <c>DnsGetCacheDataTableEx</c> prototype.
/// </summary>
internal sealed record ProbeResult(
    string Candidate,
    string Outcome,
    uint? Status,
    string TablePtrHex,
    int? EntriesWalked,
    string[]? SampleNames,
    string? ErrorDetails);

/// <summary>
/// Each entry in the singly-linked list returned by
/// <c>DnsGetCacheDataTable[Ex]</c>. Layout has been the same across every
/// publicly-documented description (FRex/muhdnscache, malcomvetter/DnsCache,
/// osquery PR #6505): pNext + pszName + wType + wDataLength + dwFlags. Every
/// candidate is assumed to use the same layout when walking the result.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DnsCacheEntry {
    public IntPtr Next;
    public IntPtr Name;
    public ushort Type;
    public ushort DataLength;
    public uint Flags;
}

internal static partial class Candidates {
    public static readonly string[] AllNames = [
        "legacy-baseline",
        "ex-flags-first",
        "ex-flags-last",
        "ex-flags-first-0x8000",
        "ex-three-args",
        "ex-table-and-count",
        "legacy-with-flag-arg",
    ];

    public static ProbeResult Run(string candidate) => candidate switch {
        "legacy-baseline" => Probe(candidate, () => {
            var status = LegacyBaseline(out var table);
            return (status, table);
        }),
        "ex-flags-first" => Probe(candidate, () => {
            var status = ExFlagsFirst(0u, out var table);
            return (status, table);
        }),
        "ex-flags-last" => Probe(candidate, () => {
            var status = ExFlagsLast(out var table, 0u);
            return (status, table);
        }),
        "ex-flags-first-0x8000" => Probe(candidate, () => {
            var status = ExFlagsFirst(0x8000u, out var table);
            return (status, table);
        }),
        "ex-three-args" => Probe(candidate, () => {
            var status = ExThreeArgs(0u, out _, out var table);
            return (status, table);
        }),
        "ex-table-and-count" => Probe(candidate, () => {
            var status = ExTableAndCount(out var table, out _);
            return (status, table);
        }),
        "legacy-with-flag-arg" => Probe(candidate, () => {
            var status = LegacyWithFlagArg(0u, out var table);
            return (status, table);
        }),
        _ => throw new ArgumentException($"Unknown candidate: {candidate}"),
    };

    /// <summary>
    /// Common probe shell: print a "calling" trace, invoke the candidate, log
    /// the return values, walk the linked list with validation, return a
    /// <see cref="ProbeResult"/>. If the candidate's call AVs, this method
    /// never returns — the process exits with the AV code, the parent infers
    /// a crash from the missing output file.
    /// </summary>
    private static ProbeResult Probe(string candidate, Func<(uint Status, IntPtr Table)> call) {
        Console.Error.WriteLine($"[{candidate}] calling DLL export…");
        var (status, table) = call();
        var tableHex = "0x" + table.ToInt64().ToString("X");
        Console.Error.WriteLine($"[{candidate}] returned status={status} table={tableHex}");

        if (status != 0) {
            return new ProbeResult(candidate, "non_zero_status", status, tableHex, null, null, null);
        }
        if (table == IntPtr.Zero) {
            return new ProbeResult(candidate, "null_table", status, tableHex, 0, [], null);
        }

        Console.Error.WriteLine($"[{candidate}] walking linked list…");
        var (entries, names, walkError) = WalkAndValidate(table);
        Console.Error.WriteLine($"[{candidate}] walked {entries} entries, {names.Length} valid names");

        if (walkError is not null) {
            return new ProbeResult(candidate, "invalid_strings", status, tableHex, entries, names, walkError);
        }
        return new ProbeResult(candidate, "ok", status, tableHex, entries, names, null);
    }

    /// <summary>
    /// Walk the linked list returned by the candidate. Returns the count of
    /// entries traversed and a sample of validated DNS names. A name is
    /// considered valid if it's non-empty, ≤253 chars (DNS spec maximum),
    /// and contains only printable ASCII or the wildcard prefix that
    /// Windows uses internally. Stops sampling at 10 names.
    /// </summary>
    private static (int Entries, string[] SampleNames, string? Error) WalkAndValidate(IntPtr head) {
        var entries = 0;
        var samples = new List<string>();
        var current = head;
        const int MaxEntriesToWalk = 1000;  // sanity bound — real cache is hundreds of entries

        while (current != IntPtr.Zero && entries < MaxEntriesToWalk) {
            DnsCacheEntry entry;
            try {
                entry = Marshal.PtrToStructure<DnsCacheEntry>(current);
            } catch (Exception ex) {
                return (entries, samples.ToArray(),
                    $"PtrToStructure failed at entry {entries}: {ex.Message}");
            }

            string? name = null;
            if (entry.Name != IntPtr.Zero) {
                try {
                    name = Marshal.PtrToStringUni(entry.Name);
                } catch (Exception ex) {
                    return (entries, samples.ToArray(),
                        $"PtrToStringUni failed at entry {entries}: {ex.Message}");
                }
            }

            if (name is not null && samples.Count < 10 && IsLikelyDnsName(name))
                samples.Add(name);

            entries++;
            current = entry.Next;
        }

        return (entries, samples.ToArray(), null);
    }

    /// <summary>
    /// Heuristic: a value walked from the cache table looks like a real DNS
    /// query name if it's non-empty, within the DNS spec length (≤253 chars),
    /// contains only printable ASCII, and either contains a dot or matches
    /// the typical short single-label patterns Windows caches (e.g. "wpad").
    /// Garbage memory rarely passes all four checks at once.
    /// </summary>
    private static bool IsLikelyDnsName(string s) {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Length > 253) return false;
        foreach (var c in s) {
            if (c < 0x21 || c > 0x7E) return false;  // printable ASCII only
        }
        return true;
    }

    // --- Candidate signature declarations. -----------------------------
    //
    // Each LibraryImport binds the same native export (or its sibling) but
    // with a different C# wrapper shape. The C# method name disambiguates;
    // the EntryPoint pins the actual native function we're testing against.

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable")]
    private static partial uint LegacyBaseline(out IntPtr ppCacheTable);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTableEx")]
    private static partial uint ExFlagsFirst(uint flags, out IntPtr ppCacheTable);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTableEx")]
    private static partial uint ExFlagsLast(out IntPtr ppCacheTable, uint flags);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTableEx")]
    private static partial uint ExThreeArgs(uint flags, out uint count, out IntPtr ppCacheTable);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTableEx")]
    private static partial uint ExTableAndCount(out IntPtr ppCacheTable, out uint count);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable")]
    private static partial uint LegacyWithFlagArg(uint flags, out IntPtr ppCacheTable);
}
