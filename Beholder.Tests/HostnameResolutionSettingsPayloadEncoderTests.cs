using System.Text.Json;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public class HostnameResolutionSettingsPayloadEncoderTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Encode_ProducesExpectedJson() {
        var bytes = HostnameResolutionSettingsPayloadEncoder.Encode(
            enablePreload: true,
            enableReverseDnsFallback: false,
            enableSniCapture: true,
            changedAt: FixedTimestamp);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("enablePreload").GetBoolean());
        Assert.False(root.GetProperty("enableReverseDnsFallback").GetBoolean());
        Assert.True(root.GetProperty("enableSniCapture").GetBoolean());
        Assert.Equal(
            FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            root.GetProperty("changedAtUnixNs").GetInt64());
    }

    [Fact]
    public void Encode_DeterministicOutput() {
        var first = HostnameResolutionSettingsPayloadEncoder.Encode(true, true, true, FixedTimestamp);
        var second = HostnameResolutionSettingsPayloadEncoder.Encode(true, true, true, FixedTimestamp);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryDecode_ValidPayload_RoundTrips() {
        var bytes = HostnameResolutionSettingsPayloadEncoder.Encode(
            enablePreload: false,
            enableReverseDnsFallback: true,
            enableSniCapture: false,
            changedAt: FixedTimestamp);

        var decoded = HostnameResolutionSettingsPayloadEncoder.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.False(decoded.Value.EnablePreload);
        Assert.True(decoded.Value.EnableReverseDnsFallback);
        Assert.False(decoded.Value.EnableSniCapture);
        Assert.Equal(FixedTimestamp, decoded.Value.ChangedAt);
    }

    [Fact]
    public void TryDecode_MissingField_ReturnsNull() {
        // Lacks `enableSniCapture`.
        var json = """{"enablePreload":true,"enableReverseDnsFallback":true,"changedAtUnixNs":0}"""u8;

        var result = HostnameResolutionSettingsPayloadEncoder.TryDecode(json);

        Assert.Null(result);
    }
}
