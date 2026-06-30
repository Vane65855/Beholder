using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

/// <summary>
/// Covers <see cref="BuildVersion.Parse"/> — turning the SDK's
/// "version+full-sha" informational string (or its absence) into the display
/// version and short commit the nav bar and status strip show.
/// </summary>
public class BuildVersionTests {
    [Fact]
    public void Parse_VersionWithCommit_SplitsVersionAndShortCommit() {
        var build = BuildVersion.Parse("0.1.1+9835198fab5c5c9c6c428d464b4d6f3911ded475", null);

        Assert.Equal("0.1.1", build.DisplayVersion);
        Assert.Equal("9835198", build.ShortCommit);
        Assert.Equal("DEV-9835198", build.DeviceLabel);
    }

    [Fact]
    public void Parse_NoCommitMetadata_UsesEmptyCommitAndLocalLabel() {
        var build = BuildVersion.Parse("0.1.1", null);

        Assert.Equal("0.1.1", build.DisplayVersion);
        Assert.Equal("", build.ShortCommit);
        Assert.Equal("DEV-local", build.DeviceLabel);
    }

    [Fact]
    public void Parse_NullInformational_FallsBackToThreePartAssemblyVersion() {
        var build = BuildVersion.Parse(null, new Version(0, 1, 1, 0));

        Assert.Equal("0.1.1", build.DisplayVersion);
        Assert.Equal("", build.ShortCommit);
        Assert.Equal("DEV-local", build.DeviceLabel);
    }

    [Fact]
    public void Parse_ShortSha_KeepsWholeShaWhenUnderSevenChars() {
        var build = BuildVersion.Parse("2.0.0+abc", null);

        Assert.Equal("2.0.0", build.DisplayVersion);
        Assert.Equal("abc", build.ShortCommit);
    }

    [Fact]
    public void Parse_NoInformationalNoAssemblyVersion_UsesZeroVersion() {
        var build = BuildVersion.Parse(null, null);

        Assert.Equal("0.0.0", build.DisplayVersion);
        Assert.Equal("DEV-local", build.DeviceLabel);
    }
}
