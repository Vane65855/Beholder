using Beholder.Daemon;

namespace Beholder.Tests;

public class ServiceCommandLineTests {
    [Theory]
    [InlineData("--install")]
    [InlineData("install")]
    [InlineData("--INSTALL")]
    public void Parse_InstallVerb_ReturnsInstall(string arg) =>
        Assert.Equal(ServiceCommand.Install, ServiceCommandLine.Parse([arg]));

    [Theory]
    [InlineData("--uninstall")]
    [InlineData("uninstall")]
    public void Parse_UninstallVerb_ReturnsUninstall(string arg) =>
        Assert.Equal(ServiceCommand.Uninstall, ServiceCommandLine.Parse([arg]));

    [Theory]
    [InlineData("--status")]
    [InlineData("status")]
    public void Parse_StatusVerb_ReturnsStatus(string arg) =>
        Assert.Equal(ServiceCommand.Status, ServiceCommandLine.Parse([arg]));

    [Theory]
    [InlineData("--console")]
    [InlineData("run")]
    public void Parse_ConsoleOrRunVerb_ReturnsRun(string arg) =>
        Assert.Equal(ServiceCommand.Run, ServiceCommandLine.Parse([arg]));

    [Fact]
    public void Parse_NoArgs_ReturnsRun() =>
        Assert.Equal(ServiceCommand.Run, ServiceCommandLine.Parse([]));

    [Fact]
    public void Parse_UnknownArg_ReturnsRun() =>
        Assert.Equal(ServiceCommand.Run, ServiceCommandLine.Parse(["--frobnicate", "value"]));

    [Fact]
    public void Parse_FirstRecognizedVerbWins() =>
        Assert.Equal(ServiceCommand.Install, ServiceCommandLine.Parse(["--install", "--uninstall"]));

    [Fact]
    public void Parse_NullArgs_Throws() =>
        Assert.Throws<ArgumentNullException>(() => ServiceCommandLine.Parse(null!));
}
