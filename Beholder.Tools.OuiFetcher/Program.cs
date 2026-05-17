// Beholder.Tools.OuiFetcher — downloads the IEEE Organizationally Unique
// Identifier (OUI) registry CSV from standards-oui.ieee.org and places it in
// the output folder (default: <repo-root>/data/) where the daemon's csproj
// conditionally picks it up. See this project's README.md for context.

const string OuiCsvUrl = "https://standards-oui.ieee.org/oui/oui.csv";

var outputDir = ParseOutputArg(args) ?? "data";
outputDir = Path.GetFullPath(outputDir);
Directory.CreateDirectory(outputDir);

var csvPath = Path.Combine(outputDir, "oui.csv");
var attributionPath = Path.Combine(outputDir, "ATTRIBUTION.md");

using var http = new HttpClient {
    Timeout = TimeSpan.FromMinutes(5),
};

Console.WriteLine($"Fetching {OuiCsvUrl}");

try {
    using var response = await http
        .GetAsync(OuiCsvUrl, HttpCompletionOption.ResponseHeadersRead)
        .ConfigureAwait(false);
    response.EnsureSuccessStatusCode();

    await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
    await using var output = File.Create(csvPath);
    await input.CopyToAsync(output).ConfigureAwait(false);
} catch (HttpRequestException ex) {
    Console.Error.WriteLine($"Failed to fetch OUI CSV: {ex.Message}");
    // Don't leave a partial file on disk — the daemon would happily load it.
    if (File.Exists(csvPath)) File.Delete(csvPath);
    return 1;
}

var info = new FileInfo(csvPath);
Console.WriteLine($"  Wrote {csvPath} ({info.Length:N0} bytes)");

EnsureAttribution(attributionPath);
Console.WriteLine($"  Updated {attributionPath}");
return 0;

static string? ParseOutputArg(string[] args) {
    for (var i = 0; i < args.Length - 1; i++) {
        if (args[i] is "--output" or "-o") return args[i + 1];
    }
    return null;
}

static void EnsureAttribution(string path) {
    // IEEE publishes OUI assignments as public information with no attribution
    // requirement. We record the source for transparency. If the file already
    // exists (e.g., from GeoIpFetcher), append our section idempotently.
    const string Section = """

        ## IEEE OUI Registry

        The `oui.csv` file in this directory is the IEEE Organizationally
        Unique Identifier (OUI) registry — the canonical mapping from MAC
        address prefix (first 24 bits) to the organization that registered it.

        - Source: https://standards-oui.ieee.org/oui/oui.csv
        - License: Public information (no attribution required, recorded
          for transparency)
        """;

    var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    if (existing.Contains("IEEE OUI Registry", StringComparison.Ordinal)) return;
    // Ensure a blank line separates our section from anything written
    // earlier (e.g., the GeoIpFetcher's CC BY 4.0 block).
    var separator = existing.Length == 0
        ? string.Empty
        : existing.EndsWith("\n\n", StringComparison.Ordinal)
            ? string.Empty
            : existing.EndsWith('\n') ? "\n" : "\n\n";
    File.WriteAllText(path, existing + separator + Section);
}
