using System.Text;
using Beholder.Core;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Encodes and decodes the <c>Name</c> field of a Beholder-managed Windows Firewall
/// rule so that <c>(ProcessPath, Direction)</c> — the Beholder primary key — round-trips
/// through a single string the native firewall API is happy to store and look up.
///
/// Format: <c>"Beholder: {in|out}|{base64(utf8(processPath))}"</c>. The literal
/// <c>"Beholder: "</c> prefix is how <c>WfpFirewallController</c> distinguishes its own
/// rules from rules created by other software. The path is base64-encoded because
/// real process paths legitimately contain spaces, pipes, colons, and Unicode, and
/// base64 gives the parser exactly one unambiguous split point.
/// </summary>
internal static class FirewallRuleNameEncoder {
    internal const string Prefix = "Beholder: ";
    private const string InboundToken = "in";
    private const string OutboundToken = "out";

    internal static string Encode(string processPath, Direction direction) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        var directionToken = direction == Direction.Inbound ? InboundToken : OutboundToken;
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(processPath));
        return $"{Prefix}{directionToken}|{encodedPath}";
    }

    internal static bool TryDecode(string? ruleName, out string processPath, out Direction direction) {
        processPath = string.Empty;
        direction = default;

        if (ruleName is null) return false;
        if (!ruleName.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var body = ruleName.AsSpan(Prefix.Length);
        var pipeIndex = body.IndexOf('|');
        if (pipeIndex <= 0 || pipeIndex == body.Length - 1) return false;

        var directionSpan = body[..pipeIndex];
        var pathSpan = body[(pipeIndex + 1)..];

        if (directionSpan.SequenceEqual(InboundToken)) {
            direction = Direction.Inbound;
        } else if (directionSpan.SequenceEqual(OutboundToken)) {
            direction = Direction.Outbound;
        } else {
            return false;
        }

        byte[] pathBytes;
        try {
            pathBytes = Convert.FromBase64String(pathSpan.ToString());
        } catch (FormatException) {
            return false;
        }

        processPath = Encoding.UTF8.GetString(pathBytes);
        return true;
    }
}
