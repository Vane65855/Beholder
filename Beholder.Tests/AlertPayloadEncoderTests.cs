using System.Text;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public sealed class AlertPayloadEncoderTests {
    [Fact]
    public void Encode_ProducesDeterministicBytes() {
        var first = AlertPayloadEncoder.Encode(@"C:\bin\app.exe", "first network connection");
        var second = AlertPayloadEncoder.Encode(@"C:\bin\app.exe", "first network connection");

        Assert.Equal(first, second);  // byte-identical for identical input
    }

    [Fact]
    public void Encode_ExpectedShape_ReadableJson() {
        var bytes = AlertPayloadEncoder.Encode(@"C:\bin\app.exe", "first network connection");
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal(
            "{\"processPath\":\"C:\\\\bin\\\\app.exe\",\"summary\":\"first network connection\"}",
            json);
    }

    [Fact]
    public void RoundTrip_PreservesProcessPathAndSummary() {
        var bytes = AlertPayloadEncoder.Encode(@"C:\bin\firefox.exe", "binary changed");

        var decoded = AlertPayloadEncoder.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.Equal(@"C:\bin\firefox.exe", decoded.Value.ProcessPath);
        Assert.Equal("binary changed", decoded.Value.Summary);
    }

    [Fact]
    public void TryDecode_MalformedJson_ReturnsNull() {
        var garbage = new byte[] { 0xFF, 0xFE, 0xFD };

        var decoded = AlertPayloadEncoder.TryDecode(garbage);

        Assert.Null(decoded);
    }

    [Fact]
    public void TryDecode_MissingSummary_ReturnsNull() {
        var json = Encoding.UTF8.GetBytes("{\"processPath\":\"C:\\\\bin\\\\app.exe\"}");

        var decoded = AlertPayloadEncoder.TryDecode(json);

        Assert.Null(decoded);
    }

    [Fact]
    public void Encode_EmptyProcessPath_AllowedForChainError() {
        // ChainError alerts have no associated process — the encoder must
        // accept empty processPath but the decoder should still see it.
        var bytes = AlertPayloadEncoder.Encode("", "chain failed at seq 42");

        var decoded = AlertPayloadEncoder.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.Equal("", decoded.Value.ProcessPath);
        Assert.Equal("chain failed at seq 42", decoded.Value.Summary);
    }
}
