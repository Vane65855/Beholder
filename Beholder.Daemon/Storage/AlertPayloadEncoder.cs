using System.Text.Json;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for alert chain payloads. Uses
/// <see cref="Utf8JsonWriter"/> with explicit field order to guarantee
/// byte-identical output for identical input — critical because the chain
/// hash covers the exact payload bytes.
/// </summary>
/// <remarks>
/// Mirrors <see cref="FirewallRulePayloadEncoder"/>. The shape
/// <c>{"processPath": "...", "summary": "..."}</c> matches what
/// <see cref="SqliteAlertStore"/>'s <c>ParsePayload</c> reads back. Both
/// process-bearing alert kinds (<c>NewProcess</c>, <c>HashChanged</c>) and
/// the chain-error kind (where <paramref name="processPath"/> is empty by
/// convention) share this single shape.
/// </remarks>
internal static class AlertPayloadEncoder {
    public static byte[] Encode(string processPath, string summary) {
        ArgumentNullException.ThrowIfNull(processPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("processPath", processPath);
        writer.WriteString("summary", summary);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes a payload previously produced by <see cref="Encode"/>. Returns
    /// null on malformed JSON or missing fields. <c>processPath</c> may be
    /// empty (chain-error case); <c>summary</c> is required to be non-empty.
    /// </summary>
    public static (string ProcessPath, string Summary)? TryDecode(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("processPath", out var pathElement) || pathElement.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("summary", out var summaryElement) || summaryElement.ValueKind != JsonValueKind.String) return null;

            var processPath = pathElement.GetString() ?? string.Empty;
            var summary = summaryElement.GetString();
            if (string.IsNullOrWhiteSpace(summary)) return null;

            return (processPath, summary);
        } catch (JsonException) {
            return null;
        }
    }
}
