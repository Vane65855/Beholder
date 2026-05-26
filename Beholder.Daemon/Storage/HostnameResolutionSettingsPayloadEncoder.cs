using System.Text.Json;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for the <c>HostnameResolutionSettingsChanged</c>
/// chain payload. Same byte-stable contract as the other settings encoders.
/// </summary>
internal static class HostnameResolutionSettingsPayloadEncoder {
    public static byte[] Encode(
        bool enablePreload,
        bool enableReverseDnsFallback,
        bool enableSniCapture,
        DateTimeOffset changedAt
    ) {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteBoolean("enablePreload", enablePreload);
        writer.WriteBoolean("enableReverseDnsFallback", enableReverseDnsFallback);
        writer.WriteBoolean("enableSniCapture", enableSniCapture);
        writer.WriteNumber("changedAtUnixNs", changedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    public static (bool EnablePreload, bool EnableReverseDnsFallback, bool EnableSniCapture, DateTimeOffset ChangedAt)?
        TryDecode(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!TryReadBool(root, "enablePreload", out var enablePreload)) return null;
            if (!TryReadBool(root, "enableReverseDnsFallback", out var enableReverseDnsFallback)) return null;
            if (!TryReadBool(root, "enableSniCapture", out var enableSniCapture)) return null;
            if (!root.TryGetProperty("changedAtUnixNs", out var changedElement)
                || changedElement.ValueKind != JsonValueKind.Number) return null;

            return (
                enablePreload,
                enableReverseDnsFallback,
                enableSniCapture,
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
