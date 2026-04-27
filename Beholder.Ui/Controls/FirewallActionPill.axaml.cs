using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Beholder.Ui.ViewModels;

namespace Beholder.Ui.Controls;

/// <summary>
/// Status-indicator pill used in the Firewall tab's rule table. Renders
/// the effective connectivity state of one direction for one process:
/// <list type="bullet">
/// <item><c>Block</c> rule exists → BLOCK (red).</item>
/// <item>everything else (no rule, or an explicit Allow rule) → ALLOW (green).</item>
/// </list>
/// Click invokes <see cref="Command"/> with <see cref="CommandParameter"/>;
/// the view-model translates the click into either
/// <c>ApplyFirewallRule(Block)</c> or <c>RemoveFirewallRule</c> as
/// appropriate. This control is purely presentational.
/// </summary>
public partial class FirewallActionPill : UserControl {
    public static readonly StyledProperty<object?> StateProperty =
        AvaloniaProperty.Register<FirewallActionPill, object?>(nameof(State));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<FirewallActionPill, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<FirewallActionPill, object?>(nameof(CommandParameter));

    /// <summary>
    /// Backing value for the visual state. Bound to
    /// <see cref="FirewallRuleRow.InAction"/> / <see cref="FirewallRuleRow.OutAction"/>;
    /// boxed as <see cref="object"/> to keep this control's public surface
    /// independent of the internal <see cref="FirewallActionState"/> enum.
    /// </summary>
    public object? State {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public ICommand? Command {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public FirewallActionPill() {
        InitializeComponent();
        UpdateVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty) {
            UpdateVisuals();
        }
    }

    private void UpdateVisuals() {
        // Reset and re-apply class — Avalonia's Classes is a set so we can
        // toggle membership rather than reassigning the entire collection.
        PillButton.Classes.Remove("allow");
        PillButton.Classes.Remove("block");

        // The bound value can come in as a boxed enum (live binding),
        // an int (literal in xaml), or null (unset). Coerce to the enum;
        // any unrecognized value collapses into the Default→Allow visual
        // because "we don't know" is safest rendered as "allowed" (the
        // pill is a status indicator and the OS default is allow).
        var state = State switch {
            FirewallActionState s => s,
            int i when Enum.IsDefined(typeof(FirewallActionState), i) => (FirewallActionState)i,
            _ => FirewallActionState.Default,
        };

        switch (state) {
            case FirewallActionState.Block:
                PillButton.Classes.Add("block");
                PillButton.Content = "BLOCK";
                break;
            // Default and Allow share the visual: both mean "this app can
            // connect right now." Default = no rule, OS default allows.
            // Allow = an explicit allow rule exists. The user sees the same
            // outcome, so we render the same pill.
            case FirewallActionState.Allow:
            default:
                PillButton.Classes.Add("allow");
                PillButton.Content = "ALLOW";
                break;
        }
    }

    private void OnClick(object? sender, RoutedEventArgs e) {
        if (Command is { } command && command.CanExecute(CommandParameter)) {
            command.Execute(CommandParameter);
        }
    }
}
