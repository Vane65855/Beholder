using System.Text.Json;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for the <c>RecordingSettingsChanged</c> chain
/// payload. Same byte-stable contract as
/// <see cref="FirewallEnforcementTogglePayloadEncoder"/>: explicit field
/// order, no indentation, Unix-ns timestamps.
/// </summary>
internal static class RecordingSettingsPayloadEncoder {
    public static byte[] Encode(bool filterSelfTraffic, DateTimeOffset changedAt) {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteBoolean("filterSelfTraffic", filterSelfTraffic);
        writer.WriteNumber("changedAtUnixNs", changedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    public static (bool FilterSelfTraffic, DateTimeOffset ChangedAt)? TryDecode(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("filterSelfTraffic", out var filterElement)) return null;
            if (filterElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            if (!root.TryGetProperty("changedAtUnixNs", out var changedElement)
                || changedElement.ValueKind != JsonValueKind.Number) return null;

            return (
                filterElement.GetBoolean(),
                DateTimeOffset.FromUnixTimeMilliseconds(changedElement.GetInt64() / 1_000_000L));
        } catch (JsonException) {
            return null;
        }
    }
}
