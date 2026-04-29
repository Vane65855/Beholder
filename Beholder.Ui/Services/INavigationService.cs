namespace Beholder.Ui.Services;

/// <summary>
/// Cross-tab navigation hook so child view-models can drive the
/// <c>MainWindowViewModel</c>'s <c>ActiveTab</c> + post-switch focus
/// without a circular reference back to the parent. Implemented by
/// <c>MainWindowViewModel</c>; consumed by VMs that need to deep-link
/// (currently <c>AlertsTabViewModel.AddRule</c>).
/// </summary>
internal interface INavigationService {
    /// <summary>
    /// Switch the active tab to Firewall and bring the rule row matching
    /// <paramref name="processPath"/> into view, briefly highlighted with
    /// an accent border so the user can locate it. No-op if no rule row
    /// exists for that path (e.g., the alert's process has never had a
    /// firewall rule and no live traffic has populated a row for it yet).
    /// </summary>
    void NavigateToFirewallRule(string processPath);
}
