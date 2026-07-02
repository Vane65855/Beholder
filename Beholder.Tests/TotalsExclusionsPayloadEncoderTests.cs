using System.Text;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

/// <summary>
/// Covers the TotalsExclusionsChanged chain payload encoder: round-trip
/// fidelity and null-on-malformed decoding, mirroring the sibling settings
/// payload encoder contracts.
/// </summary>
public class TotalsExclusionsPayloadEncoderTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Encode_TryDecode_RoundTrips() {
        var paths = new[] { @"C:\vpn\wireguard.exe", @"C:\docker\backend.exe" };

        var payload = TotalsExclusionsPayloadEncoder.Encode(paths, FixedTimestamp);
        var decoded = TotalsExclusionsPayloadEncoder.TryDecode(payload);

        Assert.NotNull(decoded);
        Assert.Equal(paths, decoded!.Value.ExcludedProcessPaths);
        Assert.Equal(FixedTimestamp, decoded.Value.ChangedAt);
    }

    [Fact]
    public void Encode_EmptyList_RoundTrips() {
        var payload = TotalsExclusionsPayloadEncoder.Encode([], FixedTimestamp);
        var decoded = TotalsExclusionsPayloadEncoder.TryDecode(payload);

        Assert.NotNull(decoded);
        Assert.Empty(decoded!.Value.ExcludedProcessPaths);
    }

    [Fact]
    public void TryDecode_MalformedJson_ReturnsNull() {
        Assert.Null(TotalsExclusionsPayloadEncoder.TryDecode(Encoding.UTF8.GetBytes("{not json")));
    }

    [Fact]
    public void TryDecode_MissingFields_ReturnsNull() {
        Assert.Null(TotalsExclusionsPayloadEncoder.TryDecode(
            Encoding.UTF8.GetBytes("""{"changedAtUnixNs":1}""")));
        Assert.Null(TotalsExclusionsPayloadEncoder.TryDecode(
            Encoding.UTF8.GetBytes("""{"excludedProcessPaths":["a"]}""")));
    }

    [Fact]
    public void TryDecode_NonStringArrayEntry_ReturnsNull() {
        Assert.Null(TotalsExclusionsPayloadEncoder.TryDecode(
            Encoding.UTF8.GetBytes("""{"excludedProcessPaths":[1],"changedAtUnixNs":1}""")));
    }
}
