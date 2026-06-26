using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="INotificationService"/> for VM tests. Records every
/// <see cref="Notify"/> call for assertion. <see cref="RaiseAlertActivated"/>
/// lets tests simulate the user clicking a toast.
/// </summary>
internal sealed class FakeNotificationService : INotificationService {
    public List<NotifyCall> Calls { get; } = new();

    public void Notify(long seq, AlertKind kind, string title, string body) =>
        Calls.Add(new NotifyCall(seq, kind, title, body));

    public List<InfoCall> InfoCalls { get; } = new();

    public void NotifyInfo(string title, string body) =>
        InfoCalls.Add(new InfoCall(title, body));

    public event Action<long>? AlertActivated;

    public void RaiseAlertActivated(long seq) => AlertActivated?.Invoke(seq);

    public sealed record NotifyCall(long Seq, AlertKind Kind, string Title, string Body);

    public sealed record InfoCall(string Title, string Body);
}
