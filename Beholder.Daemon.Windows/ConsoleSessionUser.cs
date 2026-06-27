using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Resolves the interactive console session's user (<c>DOMAIN\user</c>) so code
/// running as LocalSystem — notably the MSI's <c>--install</c> custom action —
/// can name the person who actually launched setup, not "NT AUTHORITY\SYSTEM".
/// Returns <c>null</c> when nobody is signed in at the console (e.g. a headless
/// or login-screen session). See ADR 015.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ConsoleSessionUser {
    private const int WtsUserName = 5;     // WTS_INFO_CLASS.WTSUserName
    private const int WtsDomainName = 7;   // WTS_INFO_CLASS.WTSDomainName
    private const uint NoActiveSession = 0xFFFFFFFF;
    private static readonly IntPtr CurrentServer = IntPtr.Zero;   // WTS_CURRENT_SERVER_HANDLE

    /// <summary>The console user as <c>DOMAIN\user</c>, or null if none is signed in.</summary>
    public static string? TryResolve() {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NoActiveSession) return null;

        var user = QuerySessionString(sessionId, WtsUserName);
        if (string.IsNullOrEmpty(user)) return null;   // console at the login screen

        var domain = QuerySessionString(sessionId, WtsDomainName);
        return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
    }

    private static string? QuerySessionString(uint sessionId, int infoClass) {
        if (!WTSQuerySessionInformationW(CurrentServer, sessionId, infoClass, out var buffer, out _))
            return null;
        try {
            return Marshal.PtrToStringUni(buffer);
        } finally {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(
        IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr memory);
}
