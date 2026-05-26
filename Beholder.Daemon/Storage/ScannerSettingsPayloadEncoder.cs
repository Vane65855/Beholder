using System.Text.Json;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for the <c>ScannerSettingsChanged</c> chain
/// payload. Same byte-stable contract as the other settings encoders.
/// </summary>
internal static class ScannerSettingsPayloadEncoder {
    public static byte[] Encode(bool enableHostnameResolution, DateTimeOffset changedAt) {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteBoolean("enableHostnameResolution", enableHostnameResolution);
        writer.WriteNumber("changedAtUnixNs", changedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    public static (bool EnableHostnameResolution, DateTimeOffset ChangedAt)? TryDecode(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("enableHostnameResolution", out var flagElement)) return null;
            if (flagElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            if (!root.TryGetProperty("changedAtUnixNs", out var changedElement)
                || changedElement.ValueKind != JsonValueKind.Number) return null;

            return (
                flagElement.GetBoolean(),
                DateTimeOffset.FromUnixTimeMilliseconds(changedElement.GetInt64() / 1_000_000L));
        } catch (JsonException) {
            return null;
        }
    }
}
