namespace Beholder.Protocol;

/// <summary>
/// The local IPC endpoint the daemon serves and the UI dials — a single source
/// of truth for the named-pipe name so the daemon's Kestrel listener and the
/// UI's gRPC channel can't drift. The pipe is DACL-restricted by the daemon to
/// the <c>beholder-users</c> group (ADR 014).
/// </summary>
public static class IpcEndpoint {
    /// <summary>
    /// The named-pipe name (full path <c>\\.\pipe\beholder</c>). Used by
    /// Kestrel's <c>ListenNamedPipe</c> on the daemon and
    /// <c>NamedPipeClientStream</c> on the UI.
    /// </summary>
    public const string PipeName = "beholder";
}
