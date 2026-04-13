using Beholder.Ui.Helpers;

namespace Beholder.Tests;

public class ByteFormatterTests {
    [Fact]
    public void FormatBytes_Zero_ReturnsZeroB() =>
        Assert.Equal("0 B", ByteFormatter.FormatBytes(0));

    [Theory]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    public void FormatBytes_SubKilobyte_ReturnsBytesLabel(long bytes, string expected) =>
        Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));

    [Theory]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10240, "10.0 KB")]
    public void FormatBytes_Kilobytes_ReturnsOneDecimal(long bytes, string expected) =>
        Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));

    [Theory]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(1_572_864, "1.5 MB")]
    [InlineData(20_971_520, "20.0 MB")]
    public void FormatBytes_Megabytes_ReturnsOneDecimal(long bytes, string expected) =>
        Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));

    [Theory]
    [InlineData(1_073_741_824, "1.00 GB")]
    [InlineData(2_684_354_560, "2.50 GB")]
    public void FormatBytes_Gigabytes_ReturnsTwoDecimals(long bytes, string expected) =>
        Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));

    [Fact]
    public void FormatRate_ReturnsFormattedBytesPerSecond() =>
        Assert.Equal("1.0 KB/s", ByteFormatter.FormatRate(1024));
}
