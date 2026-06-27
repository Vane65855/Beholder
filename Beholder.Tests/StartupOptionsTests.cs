using Beholder.Ui;

namespace Beholder.Tests;

public class StartupOptionsTests {
    [Theory]
    [InlineData("--tray")]
    [InlineData("--minimized")]
    [InlineData("--TRAY")]
    [InlineData("--Minimized")]
    public void StartMinimizedToTray_WithFlag_ReturnsTrue(string flag) =>
        Assert.True(StartupOptions.StartMinimizedToTray([flag]));

    [Fact]
    public void StartMinimizedToTray_FlagAmongOtherArgs_ReturnsTrue() =>
        Assert.True(StartupOptions.StartMinimizedToTray(["--foo", "--tray", "bar"]));

    [Fact]
    public void StartMinimizedToTray_NoFlag_ReturnsFalse() =>
        Assert.False(StartupOptions.StartMinimizedToTray(["--foo", "run"]));

    [Fact]
    public void StartMinimizedToTray_Empty_ReturnsFalse() =>
        Assert.False(StartupOptions.StartMinimizedToTray([]));

    [Fact]
    public void StartMinimizedToTray_Null_ReturnsFalse() =>
        Assert.False(StartupOptions.StartMinimizedToTray(null));
}
