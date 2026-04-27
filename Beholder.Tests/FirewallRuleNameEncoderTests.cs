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
        // '@' is not in the URL-safe base64 alphabet, so decoding fails.
        var ok = FirewallRuleNameEncoder.TryDecode(
            "Beholder-out-@@@notbase64@@@", out var path, out _);

        Assert.False(ok);
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void TryDecode_MissingDirection_ReturnsFalse() {
        // Empty direction token (separator immediately after prefix) — invalid.
        var ok = FirewallRuleNameEncoder.TryDecode(
            "Beholder--Zm9v", out var path, out _);

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

    [Fact]
    public void Encode_ContainsOnlyFirewallSafeCharacters() {
        // Empirical finding from probe (see commit body): INetFwPolicy2.Rules.Add()
        // rejects names containing reserved characters ':', ' ', '|', '=' with
        // E_INVALIDARG. URL-safe base64 + dash separators keeps every character in
        // [A-Za-z0-9-_], all of which Windows Firewall accepts. Lock that in.
        var paths = new[] {
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            @"C:\Users\Üser\日本.exe",
            @"C:\weird path with|pipe\foo.exe",
            @"C:\Program Files\WindowsApps\Claude_1.0_x64__pzs8sxrjxfjjc\app\Claude.exe",
        };
        foreach (var path in paths) {
            foreach (var direction in new[] { Direction.Inbound, Direction.Outbound }) {
                var name = FirewallRuleNameEncoder.Encode(path, direction);
                Assert.DoesNotContain(' ', name);
                Assert.DoesNotContain(':', name);
                Assert.DoesNotContain('|', name);
                Assert.DoesNotContain('=', name);
                Assert.DoesNotContain('+', name);
                Assert.DoesNotContain('/', name);
                Assert.All(name, c => Assert.True(
                    char.IsLetterOrDigit(c) || c == '-' || c == '_',
                    $"character '{c}' in '{name}' is not in [A-Za-z0-9-_]"));
            }
        }
    }

    [Fact]
    public void Encode_ProducesExpectedPrefixAndStructure() {
        var name = FirewallRuleNameEncoder.Encode(@"C:\bin\app.exe", Direction.Outbound);

        Assert.StartsWith("Beholder-out-", name);
    }

    [Fact]
    public void Encode_ThenDecode_PathWithBackslashes_RoundTripsThroughDashesInBase64() {
        // URL-safe base64 substitutes '-' for '+' from the standard alphabet.
        // Some encoded paths will contain '-' inside the path portion; ensure
        // the decoder splits on the FIRST '-' after the prefix (which is always
        // the direction-token boundary) rather than greedy-matching into the
        // path portion.
        const string path = @"C:\app\some-binary.exe";  // 'some-binary' contains '-' in the source path too

        var name = FirewallRuleNameEncoder.Encode(path, Direction.Inbound);

        Assert.True(FirewallRuleNameEncoder.TryDecode(name, out var decoded, out var direction));
        Assert.Equal(path, decoded);
        Assert.Equal(Direction.Inbound, direction);
    }
}
#endif
