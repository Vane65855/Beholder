using System.Text.Json;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public class ScannerSettingsPayloadEncoderTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Encode_ProducesExpectedJson() {
        var bytes = ScannerSettingsPayloadEncoder.Encode(
            enableHostnameResolution: false, FixedTimestamp);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("enableHostnameResolution").GetBoolean());
        Assert.Equal(
            FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            root.GetProperty("changedAtUnixNs").GetInt64());
    }

    [Fact]
    public void Encode_DeterministicOutput() {
        var first = ScannerSettingsPayloadEncoder.Encode(true, FixedTimestamp);
        var second = ScannerSettingsPayloadEncoder.Encode(true, FixedTimestamp);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryDecode_ValidPayload_RoundTrips() {
        var bytes = ScannerSettingsPayloadEncoder.Encode(
            enableHostnameResolution: true, FixedTimestamp);

        var decoded = ScannerSettingsPayloadEncoder.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.True(decoded.Value.EnableHostnameResolution);
        Assert.Equal(FixedTimestamp, decoded.Value.ChangedAt);
    }

    [Fact]
    public void TryDecode_MalformedJson_ReturnsNull() {
        var result = ScannerSettingsPayloadEncoder.TryDecode(new byte[] { 0xFF, 0xFF });

        Assert.Null(result);
    }
}
