using Beholder.Daemon.Pipeline;

namespace Beholder.Tests;

public class SelfTrafficFilterTests {
    [Fact]
    public void IsSelfProcess_DaemonExe_ReturnsTrue() =>
        Assert.True(SelfTrafficFilter.IsSelfProcess(@"C:\Program Files\Beholder\Beholder.Daemon.exe"));

    [Fact]
    public void IsSelfProcess_UiExe_ReturnsTrue() =>
        Assert.True(SelfTrafficFilter.IsSelfProcess(@"C:\Program Files\Beholder\Beholder.Ui.exe"));

    [Fact]
    public void IsSelfProcess_UnrelatedProcess_ReturnsFalse() =>
        Assert.False(SelfTrafficFilter.IsSelfProcess(@"C:\Program Files\Google\Chrome\chrome.exe"));

    [Fact]
    public void IsSelfProcess_DaemonLowercase_ReturnsTrue() =>
        Assert.True(SelfTrafficFilter.IsSelfProcess(@"C:\bin\beholder.daemon.exe"));

    [Fact]
    public void IsSelfProcess_DaemonNoExtension_ReturnsTrue() =>
        Assert.True(SelfTrafficFilter.IsSelfProcess("/opt/beholder/Beholder.Daemon"));

    [Fact]
    public void IsSelfProcess_UiNoExtension_ReturnsTrue() =>
        Assert.True(SelfTrafficFilter.IsSelfProcess("/opt/beholder/Beholder.Ui"));

    [Fact]
    public void IsSelfProcess_SubstringOfKnownName_ReturnsFalse() =>
        Assert.False(SelfTrafficFilter.IsSelfProcess(@"C:\apps\SomeBeholder.Daemon.exe.app.exe"));
}
