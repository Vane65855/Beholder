namespace Beholder.Daemon.Scanner;

/// <summary>
/// Parses the IEEE Organizationally Unique Identifier (OUI) registry CSV into a
/// dictionary keyed on uppercase 6-hex-character OUI prefixes. Isolated as a
/// pure function for unit-test ease — the file-reading and logging concerns
/// live in <see cref="OuiVendorLookup"/>.
/// </summary>
internal static class OuiCsvParser {
    private const string MaLRegistry = "MA-L";
    private const int RegistryColumn = 0;
    private const int AssignmentColumn = 1;
    private const int OrganizationColumn = 2;

    /// <summary>
    /// Reads the CSV from <paramref name="reader"/>, skipping the header row,
    /// keeping only <c>MA-L</c> assignments (24-bit OUI prefixes; <c>MA-M</c> and
    /// <c>MA-S</c> are sub-OUI assignments we don't use), and returning a
    /// case-insensitive dictionary of OUI prefix → organization name. Malformed
    /// rows are skipped silently — the IEEE file occasionally contains rows with
    /// embedded quotes or unexpected column counts that aren't worth aborting
    /// the load over.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(TextReader reader) {
        ArgumentNullException.ThrowIfNull(reader);

        var result = new Dictionary<string, string>(capacity: 32_000);
        var headerSkipped = false;
        string? line;
        while ((line = reader.ReadLine()) is not null) {
            if (!headerSkipped) {
                headerSkipped = true;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;

            try {
                var fields = SplitCsvLine(line);
                if (fields.Length <= OrganizationColumn) continue;
                if (!string.Equals(fields[RegistryColumn], MaLRegistry, StringComparison.OrdinalIgnoreCase)) continue;

                var prefix = fields[AssignmentColumn].Trim().ToUpperInvariant();
                var vendor = fields[OrganizationColumn].Trim();
                if (prefix.Length != 6 || string.IsNullOrEmpty(vendor)) continue;

                result[prefix] = vendor;
            } catch (FormatException) {
                // Skip rows whose CSV escaping the splitter doesn't accept.
            } catch (IndexOutOfRangeException) {
                // Skip rows shorter than expected.
            }
        }
        return result;
    }

    /// <summary>
    /// Minimal CSV splitter: respects double-quoted fields (so commas inside
    /// vendor names like "Apple, Inc." don't break the split) but does not
    /// handle every RFC 4180 edge case (embedded CRLF, escaped quotes inside
    /// quoted fields). The IEEE OUI file's vendor names occasionally contain
    /// commas but no CRLFs or embedded quotes, so this is sufficient.
    /// </summary>
    private static string[] SplitCsvLine(string line) {
        var fields = new List<string>(capacity: 4);
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in line) {
            if (ch == '"') {
                inQuotes = !inQuotes;
            } else if (ch == ',' && !inQuotes) {
                fields.Add(current.ToString());
                current.Clear();
            } else {
                current.Append(ch);
            }
        }
        fields.Add(current.ToString());
        return [.. fields];
    }
}
