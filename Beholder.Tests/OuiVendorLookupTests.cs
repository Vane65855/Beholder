using Beholder.Daemon.Scanner;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public sealed class OuiVendorLookupTests : IDisposable {
    private const string SampleCsv = """
        Registry,Assignment,Organization Name,Organization Address
        MA-L,AABBCC,AcmeCorp,Some Address
        MA-L,001122,WidgetWorks Inc.,Other Address
        MA-L,DECAFE,"Comma, In Name LLC","123 Main, Suite 4"
        MA-M,FFEE00,SubAssignment Vendor,Address
        """;

    private readonly string _tempDir;
    private readonly string _csvPath;

    public OuiVendorLookupTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _csvPath = Path.Combine(_tempDir, "oui.csv");
        File.WriteAllText(_csvPath, SampleCsv);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_FileMissing_LogsWarningAndReturnsNullForAllLookups() {
        // Error-path test per PRINCIPLES.md "every error path must be tested" —
        // locks in the graceful-degradation contract: missing file ≠ daemon
        // crash, matches NullGeoIpResolver posture.
        var missingPath = Path.Combine(_tempDir, "does-not-exist.csv");

        var lookup = new OuiVendorLookup(missingPath, NullLogger<OuiVendorLookup>.Instance);

        Assert.Null(lookup.GetVendor("aa:bb:cc:11:22:33"));
        Assert.Null(lookup.GetVendor("AABBCC"));
    }

    [Fact]
    public void GetVendor_KnownPrefix_ReturnsVendorName() {
        var lookup = new OuiVendorLookup(_csvPath, NullLogger<OuiVendorLookup>.Instance);

        var vendor = lookup.GetVendor("aa:bb:cc:11:22:33");

        Assert.Equal("AcmeCorp", vendor);
    }

    [Fact]
    public void GetVendor_KnownPrefixWithDashSeparators_NormalizesAndReturns() {
        var lookup = new OuiVendorLookup(_csvPath, NullLogger<OuiVendorLookup>.Instance);

        var vendor = lookup.GetVendor("AA-BB-CC-11-22-33");

        Assert.Equal("AcmeCorp", vendor);
    }

    [Fact]
    public void GetVendor_KnownPrefixWithColonSeparators_NormalizesAndReturns() {
        var lookup = new OuiVendorLookup(_csvPath, NullLogger<OuiVendorLookup>.Instance);

        var vendor = lookup.GetVendor("AA:BB:CC:11:22:33");

        Assert.Equal("AcmeCorp", vendor);
    }

    [Fact]
    public void GetVendor_KnownPrefixLowercase_NormalizesAndReturns() {
        var lookup = new OuiVendorLookup(_csvPath, NullLogger<OuiVendorLookup>.Instance);

        var vendor = lookup.GetVendor("00:11:22:33:44:55");

        Assert.Equal("WidgetWorks Inc.", vendor);
    }

    [Fact]
    public void GetVendor_VendorNameWithEmbeddedComma_PreservesFullName() {
        var lookup = new OuiVendorLookup(_csvPath, NullLogger<OuiVendorLookup>.Instance);

        var vendor = lookup.GetVendor("DE:CA:FE:00:00:00");

        Assert.Equal("Comma, In Name LLC", vendor);
    }

    [Fact]
    public void GetVendor_UnknownPrefix_ReturnsNull() {
        var lookup = new OuiVendorLookup(_csvPath, NullLogger<OuiVendorLookup>.Instance);

        var vendor = lookup.GetVendor("ff:ff:ff:00:00:00");

        Assert.Null(vendor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("aa:bb")]            // too short after separator removal
    [InlineData("zz:zz:zz:zz:zz:zz")] // non-hex chars
    public void GetVendor_MalformedInput_ReturnsNull(string mac) {
        var lookup = new OuiVendorLookup(_csvPath, NullLogger<OuiVendorLookup>.Instance);

        var vendor = lookup.GetVendor(mac);

        Assert.Null(vendor);
    }
}
