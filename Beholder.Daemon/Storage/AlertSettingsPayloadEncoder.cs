using System.Text.Json;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for the <c>AlertSettingsChanged</c> chain
/// payload. Same byte-stable contract as
/// <see cref="HostnameResolutionSettingsPayloadEncoder"/>.
/// </summary>
internal static class AlertSettingsPayloadEncoder {
    public static byte[] Encode(
        bool enableNewProcessDetection,
        bool enableHashChangeDetection,
        bool enableChainIntegrityMonitor,
        DateTimeOffset changedAt
    ) {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteBoolean("enableNewProcessDetection", enableNewProcessDetection);
        writer.WriteBoolean("enableHashChangeDetection", enableHashChangeDetection);
        writer.WriteBoolean("enableChainIntegrityMonitor", enableChainIntegrityMonitor);
        writer.WriteNumber("changedAtUnixNs", changedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    public static (bool EnableNewProcessDetection,
                   bool EnableHashChangeDetection,
                   bool EnableChainIntegrityMonitor,
                   DateTimeOffset ChangedAt)? TryDecode(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!TryReadBool(root, "enableNewProcessDetection", out var newProc)) return null;
            if (!TryReadBool(root, "enableHashChangeDetection", out var hash)) return null;
            if (!TryReadBool(root, "enableChainIntegrityMonitor", out var chain)) return null;
            if (!root.TryGetProperty("changedAtUnixNs", out var changedElement)
                || changedElement.ValueKind != JsonValueKind.Number) return null;

            return (
                newProc,
                hash,
                chain,
                DateTimeOffset.FromUnixTimeMilliseconds(changedElement.GetInt64() / 1_000_000L));
        } catch (JsonException) {
            return null;
        }
    }

    private static bool TryReadBool(JsonElement root, string propertyName, out bool value) {
        value = false;
        if (!root.TryGetProperty(propertyName, out var element)) return false;
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
        return true;
    }
}
