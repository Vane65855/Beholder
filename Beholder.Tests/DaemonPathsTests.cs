using Beholder.Daemon;

namespace Beholder.Tests;

public class DaemonPathsTests {
    [Fact]
    public void ResolveWritableDataRoot_NotService_ReturnsExeRelativeDataDir() {
        var expected = Path.Combine(AppContext.BaseDirectory, "data");
        Assert.Equal(expected, DaemonPaths.ResolveWritableDataRoot(hostedAsWindowsService: false));
    }

    [Fact]
    public void ResolveWritableDataRoot_Service_ReturnsProgramDataRoot() {
        Assert.Equal(DaemonPaths.ServiceDataRoot, DaemonPaths.ResolveWritableDataRoot(hostedAsWindowsService: true));
    }

    [Fact]
    public void ServiceDataRoot_IsBeholderUnderCommonApplicationData() {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.Equal(Path.Combine(programData, "Beholder"), DaemonPaths.ServiceDataRoot);
    }

    [Fact]
    public void ReadOnlyAssetRoot_IsAlwaysExeRelative() {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "data"), DaemonPaths.ReadOnlyAssetRoot);
    }

    [Fact]
    public void WritableDataRoot_ServiceVsDev_DivergeSoTheServiceNeverWritesBesideTheBinary() {
        Assert.NotEqual(
            DaemonPaths.ResolveWritableDataRoot(hostedAsWindowsService: true),
            DaemonPaths.ResolveWritableDataRoot(hostedAsWindowsService: false));
    }
}
