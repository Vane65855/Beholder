namespace Beholder.Core;

/// <summary>
/// String constants for the dotted setting names used as primary keys in the
/// <c>settings_overrides</c> SQLite table. Centralising these here avoids
/// production code and tests drifting on the literal string values.
/// </summary>
/// <remarks>
/// The dotted-section convention mirrors the <c>appsettings.json</c> top-level
/// section names + the property name — e.g.
/// <c>"Recording.FilterSelfTraffic"</c> matches
/// <c>appsettings.json["Recording"]["FilterSelfTraffic"]</c> and
/// <c>RecordingOptions.FilterSelfTraffic</c>. Future settings sections add
/// their own constants here.
/// </remarks>
public static class SettingsKeys {
    public const string RecordingFilterSelfTraffic = "Recording.FilterSelfTraffic";

    public const string DnsEnablePreload = "Dns.EnablePreload";
    public const string DnsEnableReverseDnsFallback = "Dns.EnableReverseDnsFallback";

    public const string SniEnableSniCapture = "Sni.EnableSniCapture";

    public const string AlertEnableNewProcessDetection = "Alert.EnableNewProcessDetection";
    public const string AlertEnableHashChangeDetection = "Alert.EnableHashChangeDetection";
    public const string AlertEnableChainIntegrityMonitor = "Alert.EnableChainIntegrityMonitor";

    public const string ScannerEnableHostnameResolution = "Scanner.EnableHostnameResolution";

    /// <summary>
    /// JSON string-array value (not a bool like the keys above): the ordered
    /// list of process paths excluded from aggregate traffic views.
    /// </summary>
    public const string TrafficExcludedProcessPaths = "Traffic.ExcludedProcessPaths";
}
