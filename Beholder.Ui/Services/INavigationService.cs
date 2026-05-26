using System.Threading.Tasks;

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
    /// Awaits the Firewall tab's <c>ActivateAsync</c> before scrolling so
    /// a cold-start deep-link (user has not yet visited the tab) doesn't
    /// race against rule-list population.
    /// </summary>
    Task NavigateToFirewallRuleAsync(string processPath);

    /// <summary>
    /// Phase 9.6: switch the active tab to Traffic and filter its per-process
    /// list to only processes that exchanged data with
    /// <paramref name="remoteAddress"/> in the current time range. Backs the
    /// Scanner-tab "VIEW IN TRAFFIC" button — answers "which processes on my
    /// box talked to this LAN device's IP?". Awaits the Traffic tab's
    /// <c>ActivateAsync</c> before applying the filter to avoid the
    /// construction-vs-load race that bit the Firewall deep-link's first
    /// implementation.
    /// </summary>
    Task NavigateToTrafficForRemoteAddressAsync(string remoteAddress);
}
