namespace Beholder.Ui.ViewModels;

/// <summary>
/// Which of the Traffic tab's sub-views is currently showing in the chart area.
/// GRAPH is the default — the time-series line chart. COLS is the GlassWire-
/// style 3-column destination/protocol/country breakdown. MAP is deferred
/// until Phase 8.
/// </summary>
internal enum TrafficViewMode {
    Graph,
    Cols,
    Map,
}
