using Microsoft.Data.Sqlite;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Builds the parameterized <c>NOT IN</c> clause that removes
/// totals-excluded processes from aggregate traffic queries. One shared
/// implementation so <see cref="SqliteTrafficStore"/> and
/// <see cref="TimelineStitcher"/> can't drift on the SQL shape.
/// </summary>
/// <remarks>
/// <c>COLLATE NOCASE</c> mirrors the state singleton's
/// <c>OrdinalIgnoreCase</c> matching (exclusion entries come from a file
/// picker, recorded paths from ETW; Windows paths are case-insensitive).
/// SQLite's NOCASE folds ASCII only — acceptable for Windows executable
/// paths, which are ASCII in practice.
/// </remarks>
internal static class ProcessExclusionSqlFilter {
    /// <summary>
    /// Binds one parameter per excluded path onto <paramref name="command"/>
    /// and returns the matching <c>AND process_path COLLATE NOCASE NOT IN
    /// (...)</c> fragment, or an empty string when there is nothing to
    /// exclude (zero overhead for the common unconfigured case).
    /// </summary>
    public static string BindNotInClause(SqliteCommand command, IReadOnlyList<string>? excludedPaths) {
        ArgumentNullException.ThrowIfNull(command);
        if (excludedPaths is null || excludedPaths.Count == 0) return string.Empty;

        var placeholders = new string[excludedPaths.Count];
        for (var i = 0; i < excludedPaths.Count; i++) {
            var name = $"$excluded{i}";
            placeholders[i] = name;
            command.Parameters.AddWithValue(name, excludedPaths[i]);
        }
        return $"AND process_path COLLATE NOCASE NOT IN ({string.Join(", ", placeholders)})";
    }
}
