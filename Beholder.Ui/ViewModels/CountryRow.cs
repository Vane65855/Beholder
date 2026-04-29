namespace Beholder.Ui.ViewModels;

/// <summary>One row in the COUNTRIES column of the COLS view.</summary>
/// <param name="Alpha2">Raw alpha-2 country code or sentinel (--/??).</param>
/// <param name="Display">Human-readable label ("Local", "Unknown", or the alpha-2 itself).</param>
internal sealed record CountryRow(
    string Alpha2,
    string Display,
    long TotalBytes,
    string BytesLabel,
    double BarRatio
) {
    public double EmptyRatio => 1.0 - BarRatio;
}
