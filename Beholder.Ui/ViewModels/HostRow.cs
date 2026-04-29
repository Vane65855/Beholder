namespace Beholder.Ui.ViewModels;

/// <summary>One row in the HOSTS column of the COLS view.</summary>
/// <param name="Display">Hostname when known, else the remote IP.</param>
/// <param name="Country">Alpha-2 country code or sentinel ("??", "--").</param>
/// <param name="TotalBytes">Summed bytes in + out for this host.</param>
/// <param name="BytesLabel">Pre-formatted bytes label for the row.</param>
/// <param name="BarRatio">
/// 0..1 ratio of this row's total to the biggest host in the column. Used by
/// the XAML to size the horizontal bar with no per-row converter.
/// </param>
internal sealed record HostRow(
    string Display,
    string Country,
    long TotalBytes,
    string BytesLabel,
    double BarRatio
) {
    /// <summary>Grid-sizing complement so the row's bar grid can be built
    /// from <c>"BarRatio*,EmptyRatio*"</c> columns without a second converter.</summary>
    public double EmptyRatio => 1.0 - BarRatio;
}
