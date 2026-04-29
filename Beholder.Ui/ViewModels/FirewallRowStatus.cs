namespace Beholder.Ui.ViewModels;

/// <summary>
/// Coarse row-level summary status. Drives header counts (BLOCKED / PARTIAL)
/// and any future row-level visual cue.
/// </summary>
internal enum FirewallRowStatus {
    /// <summary>
    /// Initial pre-data sentinel. <see cref="FirewallRuleRow.OverallStatus"/>
    /// never returns this once the row has been populated — Default and Allow
    /// data states both fold into <see cref="Allowed"/>.
    /// </summary>
    Default = 0,
    Allowed = 1,
    Blocked = 2,
    Partial = 3,
}
