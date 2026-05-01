namespace Beholder.Core;

/// <summary>
/// Posts OS-native notifications for incoming alerts. Implementations on each
/// platform call into the OS notification surface (Windows toast + Action
/// Center; future Linux libnotify via D-Bus). Failures are best-effort —
/// the alert's chain row and in-app list display remain authoritative.
/// </summary>
public interface INotificationService {
    /// <summary>
    /// Post a notification for the alert identified by <paramref name="seq"/>.
    /// Best-effort — implementations log and swallow failures rather than
    /// throw. Returns immediately; the OS handles render + dismissal.
    /// </summary>
    void Notify(long seq, AlertKind kind, string title, string body);

    /// <summary>
    /// Fires when the user clicks a notification. Carries the alert's chain
    /// seq so subscribers can deep-link the user to it. Raised on whatever
    /// thread the OS callback runs on; subscribers must marshal to the UI
    /// thread themselves.
    /// </summary>
    event Action<long>? AlertActivated;
}
