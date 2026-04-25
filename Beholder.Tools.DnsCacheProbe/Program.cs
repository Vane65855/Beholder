using System.Text.Json;
using Beholder.Tools.DnsCacheProbe;

// Beholder.Tools.DnsCacheProbe — containment-safe trial-and-error harness for
// finding a working prototype of the undocumented `DnsGetCacheDataTableEx`
// export in `dnsapi.dll`. Each candidate signature runs in this process; if
// a wrong signature corrupts the stack and produces a 0xC0000005 access
// violation, only this process dies — the daemon is untouched. The user (or
// a parent script) re-invokes the probe with a different `--candidate` to
// try the next hypothesis.
//
// Per docs/decisions/004-dns-cache-preload-undocumented-api.md and the plan
// at C:\Users\Vane\.claude\plans\luminous-wishing-map.md.

if (TryParseArg(args, "--candidate", out var candidate)
    && TryParseArg(args, "--out", out var outPath)) {
    return RunCandidate(candidate, outPath);
}

if (args.Length == 1 && args[0] == "--list") {
    foreach (var name in Candidates.AllNames)
        Console.WriteLine(name);
    return 0;
}

Console.Error.WriteLine("Usage: Beholder.Tools.DnsCacheProbe --candidate <name> --out <path.json>");
Console.Error.WriteLine("       Beholder.Tools.DnsCacheProbe --list");
Console.Error.WriteLine();
Console.Error.WriteLine("Candidates:");
foreach (var name in Candidates.AllNames)
    Console.Error.WriteLine($"  {name}");
return 2;

static int RunCandidate(string candidate, string outPath) {
    Console.Error.WriteLine($"[{candidate}] starting probe…");
    ProbeResult result;
    try {
        result = Candidates.Run(candidate);
    } catch (Exception ex) {
        // Managed exceptions (ArgumentException for unknown candidate, etc.).
        // Native AVs cannot be caught here and exit the process directly —
        // the parent infers crash from the missing output file.
        result = new ProbeResult(
            candidate, "managed_exception", null, "0x0", null, null,
            ex.GetType().Name + ": " + ex.Message);
    }

    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions {
        WriteIndented = true,
    });
    File.WriteAllText(outPath, json);
    Console.Error.WriteLine($"[{candidate}] wrote result to {outPath} (outcome={result.Outcome})");
    return result.Outcome == "ok" ? 0 : 1;
}

static bool TryParseArg(string[] args, string name, out string value) {
    for (var i = 0; i < args.Length - 1; i++) {
        if (args[i] == name) {
            value = args[i + 1];
            return true;
        }
    }
    value = string.Empty;
    return false;
}
