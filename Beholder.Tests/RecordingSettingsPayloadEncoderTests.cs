using System.Text.Json;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public class RecordingSettingsPayloadEncoderTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Encode_ProducesExpectedJson() {
        var bytes = RecordingSettingsPayloadEncoder.Encode(filterSelfTraffic: true, FixedTimestamp);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("filterSelfTraffic").GetBoolean());
        Assert.Equal(
            FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            root.GetProperty("changedAtUnixNs").GetInt64());
    }

    [Fact]
    public void Encode_DeterministicOutput() {
        var first = RecordingSettingsPayloadEncoder.Encode(true, FixedTimestamp);
        var second = RecordingSettingsPayloadEncoder.Encode(true, FixedTimestamp);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryDecode_ValidPayload_RoundTrips() {
        var bytes = RecordingSettingsPayloadEncoder.Encode(filterSelfTraffic: false, FixedTimestamp);

        var decoded = RecordingSettingsPayloadEncoder.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.False(decoded.Value.FilterSelfTraffic);
        Assert.Equal(FixedTimestamp, decoded.Value.ChangedAt);
    }

    [Fact]
    public void TryDecode_MalformedJson_ReturnsNull() {
        var result = RecordingSettingsPayloadEncoder.TryDecode(new byte[] { 0xFF, 0xFF });

        Assert.Null(result);
    }
}
