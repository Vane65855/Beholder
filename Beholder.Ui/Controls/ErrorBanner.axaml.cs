using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Beholder.Ui.Controls;

/// <summary>
/// Inline error/warning banner with optional dismiss-X button. Used at all
/// five HasError/ErrorMessage sites across the UI (Alerts, Firewall,
/// FirewallActivity, Traffic, TrafficCols). Two visual variants driven by
/// <see cref="Severity"/>: Danger (full-width red) and Warn (centered yellow
/// overlay). When <see cref="DismissCommand"/> is bound, clicking the X
/// invokes it; when unbound, the X is hidden and the banner is read-only.
/// See UI_DESIGN.md §5.10 for the design spec.
/// </summary>
public sealed partial class ErrorBanner : UserControl {
    public static readonly StyledProperty<ErrorBannerSeverity> SeverityProperty =
        AvaloniaProperty.Register<ErrorBanner, ErrorBannerSeverity>(
            nameof(Severity), defaultValue: ErrorBannerSeverity.Danger);

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<ErrorBanner, string?>(nameof(Message));

    public static readonly StyledProperty<ICommand?> DismissCommandProperty =
        AvaloniaProperty.Register<ErrorBanner, ICommand?>(nameof(DismissCommand));

    /// <summary>Visual + semantic severity of the banner.</summary>
    public ErrorBannerSeverity Severity {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    /// <summary>Text body of the banner. Bound to the VM's ErrorMessage.</summary>
    public string? Message {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>
    /// Optional dismiss command. When non-null, the X button is visible and
    /// clicking it invokes the command. When null, the X is hidden — useful
    /// for banners cleared by external state changes (e.g., daemon-reconnect
    /// auto-clears in <c>FirewallTabViewModel.OnDaemonStateChanged</c>).
    /// </summary>
    public ICommand? DismissCommand {
        get => GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    public ErrorBanner() {
        InitializeComponent();
        ApplySeverityClass();
        ApplyMessage();
        ApplyDismissVisibility();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == SeverityProperty) ApplySeverityClass();
        else if (change.Property == MessageProperty) ApplyMessage();
        else if (change.Property == DismissCommandProperty) ApplyDismissVisibility();
    }

    private void ApplySeverityClass() {
        // Avalonia Classes is a set; toggle membership rather than reassigning
        // the whole collection. Mirrors FirewallActionPill.UpdateVisuals.
        BannerBorder.Classes.Remove("danger");
        BannerBorder.Classes.Remove("warn");
        BannerBorder.Classes.Add(Severity == ErrorBannerSeverity.Warn ? "warn" : "danger");
    }

    private void ApplyMessage() {
        MessageText.Text = Message ?? string.Empty;
    }

    private void ApplyDismissVisibility() {
        DismissButton.IsVisible = DismissCommand is not null;
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e) {
        if (DismissCommand is { } cmd && cmd.CanExecute(null)) {
            cmd.Execute(null);
        }
    }
}

/// <summary>
/// Visual treatment for an <see cref="ErrorBanner"/>. Maps to the
/// <c>SeverityDanger</c> / <c>SeverityWarn</c> tokens defined in
/// UI_DESIGN.md §2.
/// </summary>
public enum ErrorBannerSeverity {
    /// <summary>Full-width red bottom-border treatment for action failures.</summary>
    Danger = 0,
    /// <summary>Centered yellow rounded-border overlay for transient warnings.</summary>
    Warn = 1,
}
