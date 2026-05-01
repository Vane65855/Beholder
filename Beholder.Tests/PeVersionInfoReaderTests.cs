using System.Runtime.Versioning;
using Beholder.Daemon.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

[SupportedOSPlatform("windows")]
public sealed class PeVersionInfoReaderTests {
    [Fact]
    public void Read_KnownWindowsBinary_ReturnsCompanyAndProductFromVersionInfo() {
        // notepad.exe ships on every Windows install we'd run tests on.
        // Microsoft signs it and stamps VersionInfo with predictable strings.
        var path = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(path)) {
            // CI image without notepad — skip rather than fail.
            return;
        }

        var (company, product) = PeVersionInfoReader.Read(path, NullLogger.Instance);

        Assert.NotNull(company);
        Assert.Contains("Microsoft", company);
        Assert.NotNull(product);
        // Product name varies between Win10 ("Microsoft® Windows® Operating System")
        // and Win11 ("Microsoft Windows"), but both contain "Windows".
        Assert.Contains("Windows", product);
    }

    [Fact]
    public void Read_NonExistentFile_ReturnsBothNull() {
        var (company, product) = PeVersionInfoReader.Read(
            @"C:\does\not\exist.exe", NullLogger.Instance);

        Assert.Null(company);
        Assert.Null(product);
    }

    [Fact]
    public void Read_NonPeFile_ReturnsBothNull() {
        // Use a known text file from System32 — drivers.etl, hosts, etc.
        // Pick hosts because it's tiny and present on every Windows.
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        if (!File.Exists(path)) return;  // CI variation

        var (company, product) = PeVersionInfoReader.Read(path, NullLogger.Instance);

        // hosts is plain text, no VersionInfo block. Both should be null.
        Assert.Null(company);
        Assert.Null(product);
    }

    [Fact]
    public void Read_NullPath_Throws() {
        Assert.Throws<ArgumentException>(
            () => PeVersionInfoReader.Read("", NullLogger.Instance));
    }
}
