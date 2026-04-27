using System.Text.Json;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for the <c>FirewallEnforcementToggled</c> chain
/// payload. Same byte-stable contract as
/// <see cref="FirewallRulePayloadEncoder"/>: explicit field order, no
/// indentation. Decode is exposed for the activity-strip RPC in Phase 6.5.
/// </summary>
internal static class FirewallEnforcementTogglePayloadEncoder {
    public static byte[] Encode(bool enabled, DateTimeOffset toggledAt) {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", enabled);
        writer.WriteNumber("toggledAtUnixNs", toggledAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    public static (bool Enabled, DateTimeOffset ToggledAt)? TryDecode(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("enabled", out var enabledElement)) return null;
            if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            if (!root.TryGetProperty("toggledAtUnixNs", out var toggledElement)
                || toggledElement.ValueKind != JsonValueKind.Number) return null;

            var toggledAtNs = toggledElement.GetInt64();
            return (
                enabledElement.GetBoolean(),
                DateTimeOffset.FromUnixTimeMilliseconds(toggledAtNs / 1_000_000L));
        } catch (JsonException) {
            return null;
        }
    }
}
