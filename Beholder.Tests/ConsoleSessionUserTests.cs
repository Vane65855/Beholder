#if PLATFORM_WINDOWS
using Beholder.Daemon.Windows;

namespace Beholder.Tests;

/// <summary>
/// Validates the WTS console-user P/Invoke against the live session. The test
/// host runs in the developer's interactive console, so it resolves to the
/// current user — the same path the MSI's LocalSystem <c>--install</c> relies on
/// to add the real user (not SYSTEM) to <c>beholder-users</c>. See ADR 015.
/// </summary>
public class ConsoleSessionUserTests {
    [Fact]
    public void TryResolve_FromInteractiveSession_ReturnsCurrentUser() {
        var resolved = ConsoleSessionUser.TryResolve();

        Assert.NotNull(resolved);
        Assert.Contains(Environment.UserName, resolved, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
