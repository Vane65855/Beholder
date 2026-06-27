#if PLATFORM_WINDOWS
using Beholder.Daemon.Windows;

namespace Beholder.Tests;

/// <summary>
/// Covers the pure sc.exe / icacls.exe argument builders in
/// <see cref="WindowsServiceInstaller"/>. The actual service registration is
/// elevation-gated and exercised by the manual smoke test (see ADR 013), like
/// the Administrator-only ETW tests.
/// </summary>
public class WindowsServiceInstallerTests {
    private const string ExePath = @"C:\Program Files\Beholder\Beholder.Daemon.exe";

    [Fact]
    public void BuildCreateArguments_RegistersAutoStartServiceAtExePath() {
        var args = WindowsServiceInstaller.BuildCreateArguments(ExePath);
        var list = args.ToList();

        Assert.Equal("create", args[0]);
        Assert.Equal("Beholder", args[1]);
        // sc reads the token immediately after `binPath=` (and `start=`) as the value.
        Assert.Equal(ExePath, args[list.IndexOf("binPath=") + 1]);
        Assert.Equal("auto", args[list.IndexOf("start=") + 1]);
    }

    [Fact]
    public void BuildFailureArguments_ConfiguresRestartRecovery() {
        var args = WindowsServiceInstaller.BuildFailureArguments();
        Assert.Equal("failure", args[0]);
        Assert.Equal("Beholder", args[1]);
        Assert.Contains("reset=", args);
        Assert.Contains(args, a => a.StartsWith("restart/", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildHardenAclArguments_StripsInheritanceAndGrantsSystemAndAdminsOnly() {
        var args = WindowsServiceInstaller.BuildHardenAclArguments(@"C:\ProgramData\Beholder");
        Assert.Equal(@"C:\ProgramData\Beholder", args[0]);
        Assert.Contains("/inheritance:r", args);
        Assert.Contains("*S-1-5-18:(OI)(CI)F", args);       // LocalSystem
        Assert.Contains("*S-1-5-32-544:(OI)(CI)F", args);   // Administrators
        // Crucially, nothing grants Users (S-1-5-32-545) or Everyone (S-1-1-0) —
        // that's the whole point of hardening the private-key directory.
        Assert.DoesNotContain(args, a => a.Contains("S-1-5-32-545") || a.Contains("S-1-1-0"));
    }

    [Fact]
    public void BuildQueryArguments_IsQueryThenServiceName() =>
        Assert.Equal(new[] { "query", "Beholder" }, WindowsServiceInstaller.BuildQueryArguments());

    [Fact]
    public void BuildDeleteArguments_IsDeleteThenServiceName() =>
        Assert.Equal(new[] { "delete", "Beholder" }, WindowsServiceInstaller.BuildDeleteArguments());

    [Fact]
    public void BuildStopArguments_IsStopThenServiceName() =>
        Assert.Equal(new[] { "stop", "Beholder" }, WindowsServiceInstaller.BuildStopArguments());

    [Fact]
    public void BuildStartArguments_IsStartThenServiceName() =>
        Assert.Equal(new[] { "start", "Beholder" }, WindowsServiceInstaller.BuildStartArguments());

    [Fact]
    public void BuildCreateGroupArguments_CreatesTheBeholderUsersLocalGroup() {
        var args = WindowsServiceInstaller.BuildCreateGroupArguments();
        Assert.Equal("localgroup", args[0]);
        Assert.Equal("beholder-users", args[1]);
        Assert.Contains("/add", args);
        Assert.Contains(args, a => a.StartsWith("/comment:", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildAddMemberArguments_AddsTheUserToTheGroup() =>
        Assert.Equal(new[] { "localgroup", "beholder-users", @"MACHINE\alice", "/add" },
            WindowsServiceInstaller.BuildAddMemberArguments(@"MACHINE\alice"));
}
#endif
