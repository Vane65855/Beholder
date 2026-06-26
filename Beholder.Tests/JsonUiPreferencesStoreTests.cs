using Beholder.Ui.Models;
using Beholder.Ui.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public sealed class JsonUiPreferencesStoreTests : IDisposable {
    private readonly string _dir;
    private readonly string _path;

    public JsonUiPreferencesStoreTests() {
        _dir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _path = Path.Combine(_dir, "ui-preferences.json");
    }

    public void Dispose() {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private JsonUiPreferencesStore Store() =>
        new(_path, NullLogger<JsonUiPreferencesStore>.Instance);

    [Fact]
    public void Load_NoFile_ReturnsDefaults() {
        var prefs = Store().Load();

        Assert.True(prefs.CloseToTray);     // default: minimize-to-tray on
        Assert.False(prefs.TrayHintShown);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues() {
        Store().Save(new UiPreferences { CloseToTray = false, TrayHintShown = true });

        var prefs = Store().Load();
        Assert.False(prefs.CloseToTray);
        Assert.True(prefs.TrayHintShown);
    }

    [Fact]
    public void Save_CreatesMissingDirectory() {
        Assert.False(Directory.Exists(_dir));

        Store().Save(new UiPreferences());

        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults() {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "{ not valid json");

        var prefs = Store().Load();

        Assert.True(prefs.CloseToTray);   // graceful degradation, no throw
    }
}
