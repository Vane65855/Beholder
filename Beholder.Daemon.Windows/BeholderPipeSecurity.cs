using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Builds the DACL for the daemon's local control pipe (ADR 014). LocalSystem +
/// Administrators get full control; the <c>beholder-users</c> group gets connect
/// rights; everyone else is denied. When the group can't be resolved — a dev
/// <c>dotnet run</c>, or before <c>--install</c> created it — it falls back to
/// the INTERACTIVE group so development and uninstalled runs still work, at the
/// cost of admitting any interactive desktop user (a startup log makes the
/// fallback visible).
/// </summary>
[SupportedOSPlatform("windows")]
public static class BeholderPipeSecurity {
    internal const string GroupName = "beholder-users";

    // A connecting gRPC client needs duplex IO on the pipe instance.
    private const PipeAccessRights ConnectRights =
        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    /// <summary>Resolves <c>beholder-users</c> and builds the pipe DACL (INTERACTIVE fallback when absent).</summary>
    public static PipeSecurity Create() => Build(TryResolveGroupSid());

    /// <summary>True when the <c>beholder-users</c> group exists — i.e. the pipe is restricted to it, not the fallback.</summary>
    public static bool BeholderUsersGroupExists() => TryResolveGroupSid() is not null;

    /// <summary>
    /// Builds the pipe DACL: SYSTEM + Administrators full control, plus connect
    /// rights for <paramref name="beholderUsersSid"/> when non-null, otherwise
    /// the INTERACTIVE group. No access for anyone else. Pure — unit-tested.
    /// </summary>
    internal static PipeSecurity Build(SecurityIdentifier? beholderUsersSid) {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        var clientSid = beholderUsersSid
            ?? new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        security.AddAccessRule(new PipeAccessRule(clientSid, ConnectRights, AccessControlType.Allow));
        return security;
    }

    private static SecurityIdentifier? TryResolveGroupSid() {
        try {
            return (SecurityIdentifier)new NTAccount(GroupName).Translate(typeof(SecurityIdentifier));
        } catch (IdentityNotMappedException) {
            return null;   // group not created yet
        } catch (SystemException) {
            return null;   // transient SAM/translation failure — treat as absent
        }
    }
}
