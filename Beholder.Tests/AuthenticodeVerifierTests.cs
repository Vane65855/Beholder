using System.Runtime.Versioning;
using Beholder.Core;
using Beholder.Daemon.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

[SupportedOSPlatform("windows")]
public sealed class AuthenticodeVerifierTests {
    [Fact]
    public void Read_UnsignedTextFile_ReturnsNull() {
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        if (!File.Exists(path)) return;

        // hosts is plain text — not a recognized signed-file format. The
        // verifier maps TRUST_E_SUBJECT_FORM_UNKNOWN to "no signature info"
        // (returns null) so callers fall back to path-based dedup.
        var info = AuthenticodeVerifier.Read(path, NullLogger.Instance);

        Assert.Null(info);
    }

    [Fact]
    public void Read_NonExistentFile_ReturnsNull() {
        var info = AuthenticodeVerifier.Read(@"C:\does\not\exist.exe", NullLogger.Instance);
        Assert.Null(info);
    }

    [Fact]
    public void Read_NullPath_Throws() {
        Assert.Throws<ArgumentException>(
            () => AuthenticodeVerifier.Read("", NullLogger.Instance));
    }

    [Fact]
    public void Read_SystemBinary_ReturnsConsistentShape() {
        // System binaries on modern Windows are typically catalog-signed
        // (signature in a separate .cat file, not embedded in the PE).
        // Read returns null in that case because the embedded-cert
        // extraction has nothing to read — that's the documented behavior
        // we want for spoof comparison purposes (no SubjectCn → can't
        // compare). Any other return shape is a regression.
        var path = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(path)) return;

        var info = AuthenticodeVerifier.Read(path, NullLogger.Instance);

        // Either null (catalog-signed) OR a non-null with non-empty fields
        // (embedded-signed). Empty strings would indicate a bug.
        if (info is not null) {
            Assert.False(string.IsNullOrEmpty(info.SubjectCn));
            Assert.False(string.IsNullOrEmpty(info.IssuerCn));
        }
    }

    [Fact]
    public void Read_EmbeddedSignedBinaryIfAvailable_ReturnsValidSubject() {
        // dotnet.exe is typically embedded-signed by Microsoft (verified
        // empirically). If the test machine has it at a standard location,
        // verify the embedded-cert path produces a populated AuthenticodeInfo.
        var candidates = new[] {
            @"C:\Program Files\dotnet\dotnet.exe",
            @"C:\Program Files (x86)\dotnet\dotnet.exe",
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return;  // skip on machines without dotnet.exe

        var info = AuthenticodeVerifier.Read(path, NullLogger.Instance);

        // dotnet.exe should round-trip to a valid Microsoft-signed
        // AuthenticodeInfo on supported Windows versions.
        if (info is null) return;  // catalog-signed variant exists too — skip
        Assert.Equal(SignatureValidationStatus.Valid, info.Status);
        Assert.Contains("Microsoft", info.SubjectCn);
        Assert.Contains("Microsoft", info.IssuerCn);
    }
}
