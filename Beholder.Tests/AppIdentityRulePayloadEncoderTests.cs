using System.Text.Json;
using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public class AppIdentityRulePayloadEncoderTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static AppIdentityRule MakeRule(
        int id = 42,
        string anchorPath = @"C:\Users\Vane\AppData\Local\Discord",
        string filename = "Discord.exe",
        string? displayName = "Discord"
    ) => new(
        Id: id,
        AnchorPath: anchorPath,
        Filename: filename,
        DisplayName: displayName,
        CreatedAt: FixedTimestamp);

    [Fact]
    public void Encode_ProducesExpectedJson() {
        var rule = MakeRule();

        var bytes = AppIdentityRulePayloadEncoder.Encode(rule);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.Equal(@"C:\Users\Vane\AppData\Local\Discord",
            root.GetProperty("anchorPath").GetString());
        Assert.Equal("Discord.exe", root.GetProperty("filename").GetString());
        Assert.Equal("Discord", root.GetProperty("displayName").GetString());
        Assert.Equal(FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            root.GetProperty("createdAtUnixNs").GetInt64());
    }

    [Fact]
    public void Encode_NullDisplayName_SerialisesAsJsonNull() {
        var rule = MakeRule(displayName: null);

        var bytes = AppIdentityRulePayloadEncoder.Encode(rule);

        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("displayName").ValueKind);
    }

    [Fact]
    public void Encode_DeterministicOutput() {
        var rule = MakeRule();

        var first = AppIdentityRulePayloadEncoder.Encode(rule);
        var second = AppIdentityRulePayloadEncoder.Encode(rule);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryDecode_ValidPayload_RoundTrips() {
        var original = MakeRule();

        var bytes = AppIdentityRulePayloadEncoder.Encode(original);
        var decoded = AppIdentityRulePayloadEncoder.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.Equal(original.Id, decoded.Id);
        Assert.Equal(original.AnchorPath, decoded.AnchorPath);
        Assert.Equal(original.Filename, decoded.Filename);
        Assert.Equal(original.DisplayName, decoded.DisplayName);
        Assert.Equal(original.CreatedAt, decoded.CreatedAt);
    }

    [Fact]
    public void TryDecode_NullDisplayName_RoundTrips() {
        var original = MakeRule(displayName: null);

        var bytes = AppIdentityRulePayloadEncoder.Encode(original);
        var decoded = AppIdentityRulePayloadEncoder.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.Null(decoded.DisplayName);
    }

    [Fact]
    public void TryDecode_MalformedJson_ReturnsNull() {
        var result = AppIdentityRulePayloadEncoder.TryDecode(new byte[] { 0xFF, 0xFF });

        Assert.Null(result);
    }

    [Fact]
    public void TryDecode_MissingField_ReturnsNull() {
        var json = """{"id":1,"filename":"App.exe","displayName":null,"createdAtUnixNs":0}"""u8;

        var result = AppIdentityRulePayloadEncoder.TryDecode(json);

        Assert.Null(result);
    }
}
