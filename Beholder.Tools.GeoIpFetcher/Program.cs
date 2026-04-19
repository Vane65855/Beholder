using System.IO.Compression;

// Beholder.Tools.GeoIpFetcher — downloads the DB-IP Lite IP-to-country MMDB
// from the public CC BY 4.0 distribution and places it in the output folder
// (default: <repo-root>/data/) where the daemon's csproj conditionally picks
// it up. See this project's README.md for context.

var outputDir = ParseOutputArg(args) ?? "data";
outputDir = Path.GetFullPath(outputDir);
Directory.CreateDirectory(outputDir);

var mmdbPath = Path.Combine(outputDir, "dbip-country-lite.mmdb");
var attributionPath = Path.Combine(outputDir, "ATTRIBUTION.md");

using var http = new HttpClient {
    Timeout = TimeSpan.FromMinutes(5),
};

// Try current calendar month; fall back to previous month if 404 — DB-IP
// typically publishes early in the month but there's a few-day gap on
// rollover.
var now = DateTimeOffset.UtcNow;
foreach (var candidate in new[] { now, now.AddMonths(-1) }) {
    var month = $"{candidate:yyyy-MM}";
    var url = $"https://download.db-ip.com/free/dbip-country-lite-{month}.mmdb.gz";
    Console.WriteLine($"Fetching {url}");

    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
        .ConfigureAwait(false);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
        Console.WriteLine($"  {month} not yet published, trying previous month...");
        continue;
    }
    response.EnsureSuccessStatusCode();

    await using var gzStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
    await using var gunzip = new GZipStream(gzStream, CompressionMode.Decompress);
    await using var output = File.Create(mmdbPath);
    await gunzip.CopyToAsync(output).ConfigureAwait(false);

    var info = new FileInfo(mmdbPath);
    Console.WriteLine($"  Wrote {mmdbPath} ({info.Length:N0} bytes)");
    WriteAttribution(attributionPath, month);
    Console.WriteLine($"  Wrote {attributionPath}");
    return 0;
}

Console.Error.WriteLine("DB-IP Lite MMDB not available for current or previous month.");
Console.Error.WriteLine("Check https://db-ip.com/db/lite.php for status and re-run.");
return 1;

static string? ParseOutputArg(string[] args) {
    for (var i = 0; i < args.Length - 1; i++) {
        if (args[i] is "--output" or "-o") return args[i + 1];
    }
    return null;
}

static void WriteAttribution(string path, string month) {
    // CC BY 4.0 attribution — ships alongside the data whenever the fetcher
    // runs so the license obligation travels with any redistribution.
    File.WriteAllText(path, $"""
        # GeoIP Data Attribution

        The `dbip-country-lite.mmdb` file in this directory is the DB-IP
        IP-to-Country Lite database, edition {month}.

        - Source: https://db-ip.com/db/lite.php
        - License: Creative Commons Attribution 4.0 International (CC BY 4.0)
        - License text: https://creativecommons.org/licenses/by/4.0/

        © DB-IP, used with attribution.
        """);
}
