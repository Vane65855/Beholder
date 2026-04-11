#if PLATFORM_WINDOWS
using Beholder.Core;
using Beholder.Daemon.Windows;

namespace Beholder.Tests;

public class FirewallRuleNameEncoderTests {
    [Fact]
    public void Encode_ThenDecode_RoundTrips() {
        const string path = @"C:\Program Files\Example\foo.exe";

        var name = FirewallRuleNameEncoder.Encode(path, Direction.Outbound);

        Assert.True(FirewallRuleNameEncoder.TryDecode(name, out var decoded, out var direction));
        Assert.Equal(path, decoded);
        Assert.Equal(Direction.Outbound, direction);
    }

    [Fact]
    public void TryDecode_NonBeholderPrefix_ReturnsFalse() {
        var ok = FirewallRuleNameEncoder.TryDecode(
            "Allow Chrome outbound", out var path, out _);

        Assert.False(ok);
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void TryDecode_MalformedBase64_ReturnsFalse() {
        var ok = FirewallRuleNameEncoder.TryDecode(
            "Beholder: out|@@@not-base64@@@", out var path, out _);

        Assert.False(ok);
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void TryDecode_MissingDirection_ReturnsFalse() {
        var ok = FirewallRuleNameEncoder.TryDecode(
            "Beholder: |Zm9v", out var path, out _);

        Assert.False(ok);
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void Encode_InboundAndOutbound_ProduceDifferentNames() {
        const string path = @"C:\Windows\System32\svchost.exe";

        var inbound = FirewallRuleNameEncoder.Encode(path, Direction.Inbound);
        var outbound = FirewallRuleNameEncoder.Encode(path, Direction.Outbound);

        Assert.NotEqual(inbound, outbound);
    }

    [Fact]
    public void Encode_ThenDecode_UnicodePath_RoundTrips() {
        const string path = @"C:\Users\Üser\日本.exe";

        var name = FirewallRuleNameEncoder.Encode(path, Direction.Outbound);

        Assert.True(FirewallRuleNameEncoder.TryDecode(name, out var decoded, out var direction));
        Assert.Equal(path, decoded);
        Assert.Equal(Direction.Outbound, direction);
    }

    [Fact]
    public void Encode_ThenDecode_PathWithPipe_RoundTrips() {
        const string path = @"C:\weird|path\foo.exe";

        var name = FirewallRuleNameEncoder.Encode(path, Direction.Inbound);

        Assert.True(FirewallRuleNameEncoder.TryDecode(name, out var decoded, out var direction));
        Assert.Equal(path, decoded);
        Assert.Equal(Direction.Inbound, direction);
    }

    [Fact]
    public void Encode_ThenDecode_PathWithSpaces_RoundTrips() {
        const string path = @"C:\Program Files\Example\foo.exe";

        var name = FirewallRuleNameEncoder.Encode(path, Direction.Outbound);

        Assert.True(FirewallRuleNameEncoder.TryDecode(name, out var decoded, out var direction));
        Assert.Equal(path, decoded);
        Assert.Equal(Direction.Outbound, direction);
    }
}
#endif
