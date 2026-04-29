namespace Beholder.Ui.Services;

internal record DaemonStatusInfo(ConnectionState State, string Label) {
    public static DaemonStatusInfo FromState(ConnectionState state) => state switch {
        ConnectionState.Disconnected => new(state, "offline"),
        ConnectionState.Connecting => new(state, "connecting…"),
        ConnectionState.Connected => new(state, "online"),
        ConnectionState.Reconnecting => new(state, "reconnecting…"),
        _ => new(ConnectionState.Disconnected, "offline"),
    };
}
