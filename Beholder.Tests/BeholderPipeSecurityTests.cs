#if PLATFORM_WINDOWS
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Beholder.Daemon.Windows;

namespace Beholder.Tests;

/// <summary>
/// Covers the pure DACL builder for the daemon's control pipe (ADR 014). The
/// live group resolution + Kestrel wiring are exercised by the manual smoke
/// test (elevated, real machine), like the Administrator-only ETW tests.
/// </summary>
public class BeholderPipeSecurityTests {
    // An arbitrary local-group-shaped SID standing in for a resolved beholder-users.
    private static readonly SecurityIdentifier GroupSid = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    private static List<PipeAccessRule> Rules(PipeSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>().ToList();

    private static bool HasFullControl(IEnumerable<PipeAccessRule> rules, WellKnownSidType sid) {
        var target = new SecurityIdentifier(sid, null);
        return rules.Any(r => r.IdentityReference.Equals(target)
            && r.AccessControlType == AccessControlType.Allow
            && r.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
    }

    private static bool Mentions(IEnumerable<PipeAccessRule> rules, WellKnownSidType sid) {
        var target = new SecurityIdentifier(sid, null);
        return rules.Any(r => r.IdentityReference.Equals(target));
    }

    [Fact]
    public void Build_WithGroup_GrantsGroupConnectAndSystemAdminsFull_AndNobodyElse() {
        var rules = Rules(BeholderPipeSecurity.Build(GroupSid));

        Assert.True(HasFullControl(rules, WellKnownSidType.LocalSystemSid));
        Assert.True(HasFullControl(rules, WellKnownSidType.BuiltinAdministratorsSid));

        var groupRule = Assert.Single(rules, r => r.IdentityReference.Equals(GroupSid));
        Assert.Equal(AccessControlType.Allow, groupRule.AccessControlType);
        Assert.True(groupRule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));

        // The whole point: no broad principals can reach the control surface.
        Assert.False(Mentions(rules, WellKnownSidType.WorldSid));              // Everyone (S-1-1-0)
        Assert.False(Mentions(rules, WellKnownSidType.BuiltinUsersSid));       // Users (S-1-5-32-545)
        Assert.False(Mentions(rules, WellKnownSidType.AuthenticatedUserSid));  // Authenticated Users (S-1-5-11)
        Assert.False(Mentions(rules, WellKnownSidType.InteractiveSid));        // not the fallback path
    }

    [Fact]
    public void Build_NullGroup_FallsBackToInteractiveConnect() {
        var rules = Rules(BeholderPipeSecurity.Build(beholderUsersSid: null));

        Assert.True(HasFullControl(rules, WellKnownSidType.LocalSystemSid));
        Assert.True(HasFullControl(rules, WellKnownSidType.BuiltinAdministratorsSid));

        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        var rule = Assert.Single(rules, r => r.IdentityReference.Equals(interactive));
        Assert.True(rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));

        // Even the dev fallback never opens it to Everyone / Users.
        Assert.False(Mentions(rules, WellKnownSidType.WorldSid));
        Assert.False(Mentions(rules, WellKnownSidType.AuthenticatedUserSid));
    }
}
#endif
