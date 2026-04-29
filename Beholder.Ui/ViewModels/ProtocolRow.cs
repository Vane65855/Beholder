namespace Beholder.Ui.ViewModels;

/// <summary>One row in the TRAFFIC TYPE column of the COLS view.</summary>
internal sealed record ProtocolRow(
    string Name,
    string Transport,
    long TotalBytes,
    string BytesLabel,
    double BarRatio
) {
    public double EmptyRatio => 1.0 - BarRatio;
}
