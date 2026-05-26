using System.Text.Json;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public class AlertSettingsPayloadEncoderTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Encode_ProducesExpectedJson() {
        var bytes = AlertSettingsPayloadEncoder.Encode(
            enableNewProcessDetection: true,
            enableHashChangeDetection: false,
            enableChainIntegrityMonitor: true,
            changedAt: FixedTimestamp);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("enableNewProcessDetection").GetBoolean());
        Assert.False(root.GetProperty("enableHashChangeDetection").GetBoolean());
        Assert.True(root.GetProperty("enableChainIntegrityMonitor").GetBoolean());
        Assert.Equal(
            FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            root.GetProperty("changedAtUnixNs").GetInt64());
    }

    [Fact]
    public void Encode_DeterministicOutput() {
        var first = AlertSettingsPayloadEncoder.Encode(true, true, true, FixedTimestamp);
        var second = AlertSettingsPayloadEncoder.Encode(true, true, true, FixedTimestamp);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryDecode_ValidPayload_RoundTrips() {
        var bytes = AlertSettingsPayloadEncoder.Encode(
            enableNewProcessDetection: false,
            enableHashChangeDetection: true,
            enableChainIntegrityMonitor: false,
            changedAt: FixedTimestamp);

        var decoded = AlertSettingsPayloadEncoder.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.False(decoded.Value.EnableNewProcessDetection);
        Assert.True(decoded.Value.EnableHashChangeDetection);
        Assert.False(decoded.Value.EnableChainIntegrityMonitor);
        Assert.Equal(FixedTimestamp, decoded.Value.ChangedAt);
    }

    [Fact]
    public void TryDecode_MissingField_ReturnsNull() {
        // Lacks `enableChainIntegrityMonitor`.
        var json = """{"enableNewProcessDetection":true,"enableHashChangeDetection":true,"changedAtUnixNs":0}"""u8;

        var result = AlertSettingsPayloadEncoder.TryDecode(json);

        Assert.Null(result);
    }
}
