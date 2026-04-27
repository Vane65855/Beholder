using System.Text;
using Beholder.Core;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Encodes and decodes the <c>Name</c> field of a Beholder-managed Windows Firewall
/// rule so that <c>(ProcessPath, Direction)</c> — the Beholder primary key — round-trips
/// through a single string the native firewall API is happy to store and look up.
///
/// Format: <c>"Beholder-{in|out}-{base64url(utf8(processPath))}"</c>. The literal
/// <c>"Beholder-"</c> prefix is how <c>WfpFirewallController</c> distinguishes its own
/// rules from rules created by other software. The path is base64-encoded with the
/// URL-safe alphabet (RFC 4648 §5: <c>A-Z a-z 0-9 - _</c>) and no padding, because
/// <see cref="INetFwPolicy2.Rules"/>'s <c>Add</c> method validates the rule name during
/// the add and rejects names containing reserved characters with
/// <c>E_INVALIDARG</c> — empirically confirmed via probe (see commit message body).
/// Standard base64's <c>+ / =</c> are reserved; URL-safe base64's <c>- _</c> are accepted.
/// </summary>
internal static class FirewallRuleNameEncoder {
    internal const string Prefix = "Beholder-";
    private const string InboundToken = "in";
    private const string OutboundToken = "out";
    private const char Separator = '-';

    internal static string Encode(string processPath, Direction direction) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        var directionToken = direction == Direction.Inbound ? InboundToken : OutboundToken;
        var standardBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(processPath));
        // RFC 4648 §5 URL-safe base64 + no padding — keeps every character in the
        // alphabet [A-Za-z0-9-_], which the Windows Firewall name validator accepts.
        var urlSafe = standardBase64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{Prefix}{directionToken}{Separator}{urlSafe}";
    }

    internal static bool TryDecode(string? ruleName, out string processPath, out Direction direction) {
        processPath = string.Empty;
        direction = default;

        if (ruleName is null) return false;
        if (!ruleName.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var body = ruleName.AsSpan(Prefix.Length);
        // Direction token is "in" or "out" — both contain no <Separator>, so the
        // first <Separator> in the body always cleanly splits direction from path.
        // The path portion may itself contain <Separator> (URL-safe base64 uses it
        // in place of '+'); IndexOf returns the FIRST occurrence, so we don't
        // risk consuming part of the path.
        var sepIndex = body.IndexOf(Separator);
        if (sepIndex <= 0 || sepIndex == body.Length - 1) return false;

        var directionSpan = body[..sepIndex];
        var pathSpan = body[(sepIndex + 1)..];

        if (directionSpan.SequenceEqual(InboundToken)) {
            direction = Direction.Inbound;
        } else if (directionSpan.SequenceEqual(OutboundToken)) {
            direction = Direction.Outbound;
        } else {
            return false;
        }

        // Convert URL-safe back to standard base64, then re-add padding to a
        // multiple of 4 so Convert.FromBase64String accepts it.
        var urlSafeStr = pathSpan.ToString();
        var standardBase64 = urlSafeStr.Replace('-', '+').Replace('_', '/');
        var paddingNeeded = (4 - standardBase64.Length % 4) % 4;
        if (paddingNeeded > 0) standardBase64 += new string('=', paddingNeeded);

        byte[] pathBytes;
        try {
            pathBytes = Convert.FromBase64String(standardBase64);
        } catch (FormatException) {
            return false;
        }

        processPath = Encoding.UTF8.GetString(pathBytes);
        return true;
    }
}
