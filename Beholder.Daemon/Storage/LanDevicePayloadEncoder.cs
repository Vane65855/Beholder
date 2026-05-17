using System.Text.Json;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Deterministic JSON encoder for the two LAN-discovery chain payloads
/// (<c>LanDeviceFirstSeen</c>, <c>LanDeviceMacChanged</c>). Uses
/// <see cref="Utf8JsonWriter"/> with explicit field order so the chain hash
/// stays byte-stable across runs. Mirrors <see cref="AlertPayloadEncoder"/>
/// and <see cref="FirewallRulePayloadEncoder"/>.
/// </summary>
internal static class LanDevicePayloadEncoder {
    public static byte[] EncodeFirstSeen(string mac, string ip, string? vendor, string? hostname) {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);

        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("mac", mac);
        writer.WriteString("ip", ip);
        if (vendor is null) writer.WriteNull("vendor"); else writer.WriteString("vendor", vendor);
        if (hostname is null) writer.WriteNull("hostname"); else writer.WriteString("hostname", hostname);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes a payload previously produced by <see cref="EncodeFirstSeen"/>.
    /// Returns null on malformed JSON or missing required fields (<c>mac</c>,
    /// <c>ip</c>). Null vendor / hostname round-trip as null.
    /// </summary>
    public static LanDeviceFirstSeenPayload? TryDecodeFirstSeen(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("mac", out var macElement) || macElement.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("ip", out var ipElement) || ipElement.ValueKind != JsonValueKind.String) return null;

            var mac = macElement.GetString();
            var ip = ipElement.GetString();
            if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(ip)) return null;

            var vendor = root.TryGetProperty("vendor", out var vEl) && vEl.ValueKind == JsonValueKind.String
                ? vEl.GetString() : null;
            var hostname = root.TryGetProperty("hostname", out var hEl) && hEl.ValueKind == JsonValueKind.String
                ? hEl.GetString() : null;

            return new LanDeviceFirstSeenPayload(mac, ip, vendor, hostname);
        } catch (JsonException) {
            return null;
        }
    }

    public static byte[] EncodeMacChanged(string ip, string oldMac, string newMac, DateTimeOffset oldMacFirstSeen) {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldMac);
        ArgumentException.ThrowIfNullOrWhiteSpace(newMac);

        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("ip", ip);
        writer.WriteString("oldMac", oldMac);
        writer.WriteString("newMac", newMac);
        writer.WriteNumber("oldMacFirstSeenUnixNs", oldMacFirstSeen.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes a payload previously produced by <see cref="EncodeMacChanged"/>.
    /// Returns null on malformed JSON or any missing field.
    /// </summary>
    public static LanDeviceMacChangedPayload? TryDecodeMacChanged(ReadOnlySpan<byte> payload) {
        try {
            var reader = new Utf8JsonReader(payload);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("ip", out var ipEl) || ipEl.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("oldMac", out var oldEl) || oldEl.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("newMac", out var newEl) || newEl.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("oldMacFirstSeenUnixNs", out var tsEl) || tsEl.ValueKind != JsonValueKind.Number) return null;

            var ip = ipEl.GetString();
            var oldMac = oldEl.GetString();
            var newMac = newEl.GetString();
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(oldMac) || string.IsNullOrWhiteSpace(newMac)) return null;

            return new LanDeviceMacChangedPayload(
                Ip: ip,
                OldMac: oldMac,
                NewMac: newMac,
                OldMacFirstSeen: DateTimeOffset.FromUnixTimeMilliseconds(tsEl.GetInt64() / 1_000_000L));
        } catch (JsonException) {
            return null;
        }
    }
}

internal sealed record LanDeviceFirstSeenPayload(string Mac, string Ip, string? Vendor, string? Hostname);

internal sealed record LanDeviceMacChangedPayload(string Ip, string OldMac, string NewMac, DateTimeOffset OldMacFirstSeen);
