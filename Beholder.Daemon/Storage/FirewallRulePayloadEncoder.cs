using System.Text.Json;
using Beholder.Core;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for firewall rule chain payloads. Uses
/// <see cref="Utf8JsonWriter"/> with explicit field order to guarantee
/// byte-identical output for identical input — critical because the chain
/// hash covers the exact payload bytes.
/// </summary>
internal static class FirewallRulePayloadEncoder {
    public static byte[] Encode(FirewallRule rule) {
        ArgumentNullException.ThrowIfNull(rule);

        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteNumber("id", rule.Id);
        writer.WriteString("processPath", rule.ProcessPath);
        writer.WriteString("direction", rule.Direction.ToString());
        writer.WriteString("action", rule.Action.ToString());
        writer.WriteString("source", rule.Source.ToString());
        writer.WriteNumber("createdAtUnixNs", rule.CreatedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteNumber("updatedAtUnixNs", rule.UpdatedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    public static FirewallRule? TryDecode(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number) return null;
            if (!root.TryGetProperty("processPath", out var pathElement) || pathElement.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("direction", out var dirElement) || dirElement.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("action", out var actElement) || actElement.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("source", out var srcElement) || srcElement.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("createdAtUnixNs", out var createdElement) || createdElement.ValueKind != JsonValueKind.Number) return null;
            if (!root.TryGetProperty("updatedAtUnixNs", out var updatedElement) || updatedElement.ValueKind != JsonValueKind.Number) return null;

            var processPath = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(processPath)) return null;

            if (!Enum.TryParse<Direction>(dirElement.GetString(), out var direction)) return null;
            if (!Enum.TryParse<FirewallAction>(actElement.GetString(), out var action)) return null;
            if (!Enum.TryParse<RuleSource>(srcElement.GetString(), out var source)) return null;

            var createdAtNs = createdElement.GetInt64();
            var updatedAtNs = updatedElement.GetInt64();

            return new FirewallRule(
                id: idElement.GetInt32(),
                processPath: processPath,
                direction: direction,
                action: action,
                source: source,
                createdAt: DateTimeOffset.FromUnixTimeMilliseconds(createdAtNs / 1_000_000L),
                updatedAt: DateTimeOffset.FromUnixTimeMilliseconds(updatedAtNs / 1_000_000L));
        } catch (JsonException) {
            return null;
        }
    }
}
