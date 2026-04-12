using System.Text.Json;
using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public class FirewallRulePayloadEncoderTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Encode_NullRule_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => FirewallRulePayloadEncoder.Encode(null!));
    }

    [Fact]
    public void Encode_ValidRule_ProducesExpectedJson() {
        var rule = MakeRule();

        var bytes = FirewallRulePayloadEncoder.Encode(rule);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.Equal(@"C:\bin\foo.exe", root.GetProperty("processPath").GetString());
        Assert.Equal("Outbound", root.GetProperty("direction").GetString());
        Assert.Equal("Block", root.GetProperty("action").GetString());
        Assert.Equal("Manual", root.GetProperty("source").GetString());
        Assert.Equal(
            FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            root.GetProperty("createdAtUnixNs").GetInt64());
        Assert.Equal(
            FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            root.GetProperty("updatedAtUnixNs").GetInt64());
    }

    [Fact]
    public void Encode_DeterministicOutput() {
        var rule = MakeRule();

        var first = FirewallRulePayloadEncoder.Encode(rule);
        var second = FirewallRulePayloadEncoder.Encode(rule);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Encode_DifferentRules_ProduceDifferentBytes() {
        var ruleA = MakeRule(processPath: @"C:\bin\a.exe");
        var ruleB = MakeRule(processPath: @"C:\bin\b.exe");

        var bytesA = FirewallRulePayloadEncoder.Encode(ruleA);
        var bytesB = FirewallRulePayloadEncoder.Encode(ruleB);

        Assert.NotEqual(bytesA, bytesB);
    }

    [Fact]
    public void TryDecode_ValidPayload_RoundTrips() {
        var original = MakeRule();

        var bytes = FirewallRulePayloadEncoder.Encode(original);
        var decoded = FirewallRulePayloadEncoder.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.Equal(original.Id, decoded.Id);
        Assert.Equal(original.ProcessPath, decoded.ProcessPath);
        Assert.Equal(original.Direction, decoded.Direction);
        Assert.Equal(original.Action, decoded.Action);
        Assert.Equal(original.Source, decoded.Source);
        Assert.Equal(original.CreatedAt, decoded.CreatedAt);
        Assert.Equal(original.UpdatedAt, decoded.UpdatedAt);
    }

    [Fact]
    public void TryDecode_MalformedJson_ReturnsNull() {
        var result = FirewallRulePayloadEncoder.TryDecode(new byte[] { 0xFF, 0xFF });

        Assert.Null(result);
    }

    [Fact]
    public void TryDecode_MissingField_ReturnsNull() {
        var json = """{"id":1,"processPath":"foo.exe","action":"Block","source":"Manual","createdAtUnixNs":0,"updatedAtUnixNs":0}"""u8;

        var result = FirewallRulePayloadEncoder.TryDecode(json);

        Assert.Null(result);
    }

    [Fact]
    public void TryDecode_InvalidEnumValue_ReturnsNull() {
        var json = """{"id":1,"processPath":"foo.exe","direction":"Sideways","action":"Block","source":"Manual","createdAtUnixNs":0,"updatedAtUnixNs":0}"""u8;

        var result = FirewallRulePayloadEncoder.TryDecode(json);

        Assert.Null(result);
    }

    private static FirewallRule MakeRule(
        int id = 42,
        string processPath = @"C:\bin\foo.exe",
        Direction direction = Direction.Outbound,
        FirewallAction action = FirewallAction.Block,
        RuleSource source = RuleSource.Manual
    ) => new(
        id: id,
        processPath: processPath,
        direction: direction,
        action: action,
        source: source,
        createdAt: FixedTimestamp,
        updatedAt: FixedTimestamp);
}
