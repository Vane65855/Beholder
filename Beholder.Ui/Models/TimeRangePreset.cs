namespace Beholder.Ui.Models;

/// <summary>
/// Named time-range presets for the Traffic tab's range selector dropdown.
/// <see cref="Last5Minutes"/> is the only live-streaming preset; all others
/// trigger a historical query against the daemon's tiered storage.
/// </summary>
public enum TimeRangePreset {
    Last5Minutes,
    Last1Hour,
    Last24Hours,
    Last7Days,
    Last30Days,
    AllTime,
    Custom,
}
