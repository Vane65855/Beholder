namespace Beholder.Ui.ViewModels;

/// <summary>
/// One destination row in the chart-selection bar: the host name (or the IP when
/// no name is known), the IP shown as a secondary line when a host name is the
/// primary, the average download and upload speeds over the selected window
/// (rendered with the app-wide ▼ teal / ▲ purple traffic colors), and the total
/// bytes. Rows arrive ordered fastest-first (the daemon sorts by combined total
/// bytes, which for a fixed window equals combined average speed).
/// </summary>
internal readonly record struct SelectionDestinationRow(
    string DisplayName,
    string RemoteAddress,
    bool ShowAddress,
    string DownSpeedLabel,
    string UpSpeedLabel,
    string BytesLabel);
