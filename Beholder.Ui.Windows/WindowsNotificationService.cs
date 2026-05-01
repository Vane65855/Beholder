using Beholder.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Beholder.Ui.Windows;

/// <summary>
/// Windows implementation of <see cref="INotificationService"/>. Uses
/// <see cref="ToastContentBuilder"/> from the legacy UWP-Toolkit package,
/// which auto-registers a Start Menu shortcut + AppUserModelID on first
/// <see cref="ToastContentBuilder.Show"/> — that is what makes Action Center
/// persistence work for unpackaged .exe (the alternative is MSIX packaging
/// or hand-rolled WinRT projection, both heavier than this v1 needs).
/// </summary>
public sealed class WindowsNotificationService : INotificationService, IDisposable {
    private readonly ILogger<WindowsNotificationService> _logger;

    public event Action<long>? AlertActivated;

    public WindowsNotificationService(ILogger<WindowsNotificationService> logger) {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    public void Notify(long seq, AlertKind kind, string title, string body) {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        try {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .AddArgument("seq", seq)
                .Show();
        } catch (Exception ex) {
            // Best-effort: chain row + in-app list still surface the alert.
            // Logged at Warning so a noisy environment (Action Center
            // disabled, missing shortcut, etc.) is visible to the operator.
            _logger.LogWarning(ex,
                "Failed to post toast notification for alert {Seq}", seq);
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat args) {
        try {
            var parsed = ToastArguments.Parse(args.Argument);
            if (parsed.TryGetValue("seq", out var seqString)
                && long.TryParse(seqString, out var seq)) {
                AlertActivated?.Invoke(seq);
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex,
                "Failed to parse toast activation arguments: {Argument}", args.Argument);
        }
    }

    public void Dispose() {
        ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
    }
}
