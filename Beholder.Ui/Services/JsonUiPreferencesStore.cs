using System;
using System.IO;
using System.Text.Json;
using Beholder.Ui.Models;
using Microsoft.Extensions.Logging;

namespace Beholder.Ui.Services;

/// <summary>
/// Stores <see cref="UiPreferences"/> as a small JSON file at
/// <c>%LOCALAPPDATA%\Beholder\ui-preferences.json</c>. Reads/writes are
/// synchronous — the file is a few bytes, read once at startup and written only
/// on the rare toggle. A missing or corrupt file degrades to defaults (the same
/// graceful posture as the daemon's <c>NullGeoIpResolver</c>), so a bad
/// preferences file never blocks the app from starting.
/// </summary>
internal sealed class JsonUiPreferencesStore : IUiPreferencesStore {
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<JsonUiPreferencesStore> _logger;

    public JsonUiPreferencesStore(ILogger<JsonUiPreferencesStore> logger)
        : this(DefaultFilePath(), logger) { }

    // Test seam: point the store at a temp path so tests don't touch the real
    // per-user file.
    internal JsonUiPreferencesStore(string filePath, ILogger<JsonUiPreferencesStore> logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);
        _filePath = filePath;
        _logger = logger;
    }

    public UiPreferences Load() {
        try {
            if (!File.Exists(_filePath)) return new UiPreferences();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<UiPreferences>(json, Options) ?? new UiPreferences();
        } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            _logger.LogWarning(ex, "Could not read UI preferences from {Path}; using defaults", _filePath);
            return new UiPreferences();
        }
    }

    public void Save(UiPreferences preferences) {
        ArgumentNullException.ThrowIfNull(preferences);
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(preferences, Options));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _logger.LogWarning(ex, "Could not save UI preferences to {Path}", _filePath);
        }
    }

    private static string DefaultFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beholder", "ui-preferences.json");
}
