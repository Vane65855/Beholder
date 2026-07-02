using System.Text.Json;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for the <c>TotalsExclusionsChanged</c> chain
/// payload. Same byte-stable contract as
/// <see cref="RecordingSettingsPayloadEncoder"/>: explicit field order, no
/// indentation, Unix-ns timestamps. The payload carries the full post-change
/// list so each chain entry is self-contained — consecutive entries let an
/// auditor reconstruct every add/remove without replaying state.
/// </summary>
internal static class TotalsExclusionsPayloadEncoder {
    public static byte[] Encode(IReadOnlyList<string> excludedProcessPaths, DateTimeOffset changedAt) {
        ArgumentNullException.ThrowIfNull(excludedProcessPaths);
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteStartArray("excludedProcessPaths");
        foreach (var path in excludedProcessPaths) writer.WriteStringValue(path);
        writer.WriteEndArray();
        writer.WriteNumber("changedAtUnixNs", changedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    public static (IReadOnlyList<string> ExcludedProcessPaths, DateTimeOffset ChangedAt)? TryDecode(
        ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("excludedProcessPaths", out var pathsElement)
                || pathsElement.ValueKind != JsonValueKind.Array) return null;
            if (!root.TryGetProperty("changedAtUnixNs", out var changedElement)
                || changedElement.ValueKind != JsonValueKind.Number) return null;

            var paths = new List<string>(pathsElement.GetArrayLength());
            foreach (var entry in pathsElement.EnumerateArray()) {
                if (entry.ValueKind != JsonValueKind.String) return null;
                paths.Add(entry.GetString()!);
            }

            return (
                paths,
                DateTimeOffset.FromUnixTimeMilliseconds(changedElement.GetInt64() / 1_000_000L));
        } catch (JsonException) {
            return null;
        }
    }
}
