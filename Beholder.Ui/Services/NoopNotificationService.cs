using System;
using Beholder.Core;

namespace Beholder.Ui.Services;

/// <summary>
/// Fallback for non-Windows platforms until a real Linux/macOS impl ships.
/// Swallows every <see cref="Notify"/> call and never raises
/// <see cref="AlertActivated"/>. The Alerts tab still works as before — the
/// user just doesn't get OS toasts.
/// </summary>
internal sealed class NoopNotificationService : INotificationService {
    public void Notify(long seq, AlertKind kind, string title, string body) { }

    // Event is declared by the interface but never raised — no OS callback
    // exists to wire to on non-Windows platforms. Pragma silences CS0067
    // (unused event) without obscuring real "unused" warnings elsewhere.
#pragma warning disable CS0067
    public event Action<long>? AlertActivated;
#pragma warning restore CS0067
}
