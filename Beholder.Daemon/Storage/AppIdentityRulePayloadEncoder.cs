using System.Text.Json;
using Beholder.Core;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for the Phase 13.6 application-identity-rule
/// chain payloads (<see cref="EventKind.AppIdentityRuleCreated"/> and
/// <see cref="EventKind.AppIdentityRuleRemoved"/>). Both kinds share the
/// same payload shape — the encoder writes the full rule snapshot at the
/// moment of the audited action. Mirrors
/// <see cref="FirewallRulePayloadEncoder"/>'s deterministic-output contract:
/// <see cref="Utf8JsonWriter"/> with explicit field order, no indentation,
/// byte-identical output for identical input.
/// </summary>
internal static class AppIdentityRulePayloadEncoder {
    public static byte[] Encode(AppIdentityRule rule) {
        ArgumentNullException.ThrowIfNull(rule);

        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteNumber("id", rule.Id);
        writer.WriteString("anchorPath", rule.AnchorPath);
        writer.WriteString("filename", rule.Filename);
        // Always write the displayName field for byte-stability across rules
        // with vs without one. Null serialises as JSON null.
        if (rule.DisplayName is null) {
            writer.WriteNull("displayName");
        } else {
            writer.WriteString("displayName", rule.DisplayName);
        }
        writer.WriteNumber("createdAtUnixNs", rule.CreatedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    public static AppIdentityRule? TryDecode(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number) return null;
            if (!root.TryGetProperty("anchorPath", out var anchorElement) || anchorElement.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("filename", out var fileElement) || fileElement.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("displayName", out var dispElement)) return null;
            if (!root.TryGetProperty("createdAtUnixNs", out var createdElement) || createdElement.ValueKind != JsonValueKind.Number) return null;

            var anchorPath = anchorElement.GetString();
            var filename = fileElement.GetString();
            if (string.IsNullOrWhiteSpace(anchorPath) || string.IsNullOrWhiteSpace(filename)) return null;

            string? displayName = dispElement.ValueKind switch {
                JsonValueKind.Null => null,
                JsonValueKind.String => dispElement.GetString(),
                _ => null,
            };
            // If displayName was a non-null non-string, payload is malformed.
            if (displayName is null && dispElement.ValueKind != JsonValueKind.Null) return null;

            return new AppIdentityRule(
                Id: idElement.GetInt32(),
                AnchorPath: anchorPath,
                Filename: filename,
                DisplayName: displayName,
                CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(createdElement.GetInt64() / 1_000_000L));
        } catch (JsonException) {
            return null;
        }
    }
}
