using System.Text.Json;
using Beholder.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Hosted service that loads persisted settings overrides at daemon startup
/// and applies them to the in-memory state singletons. Registered EARLY in
/// <c>Program.cs</c> so its <see cref="StartAsync"/> runs before any
/// consumer of the state singletons begins work — the registration-order
/// contract of <see cref="IHostedService"/> guarantees sequential
/// <c>StartAsync</c> dispatch.
/// </summary>
/// <remarks>
/// <para>This service is the bridge between the persistence layer
/// (<see cref="ISettingsOverridesStore"/>) and the runtime state
/// (<see cref="IRecordingSettingsState"/>, <see cref="IHostnameResolutionSettingsState"/>).
/// The state singletons are seeded from <c>IOptions&lt;T&gt;</c> defaults at
/// construction; this service replays any persisted user overrides on top of
/// those defaults.</para>
/// <para>Unknown keys in the store are logged and skipped — defensive against
/// a future version writing a key the current daemon doesn't recognise (e.g.,
/// downgrading the daemon below a release that added a new setting).</para>
/// </remarks>
internal sealed class SettingsOverridesService : IHostedService {
    private readonly ISettingsOverridesStore _store;
    private readonly IRecordingSettingsState _recordingState;
    private readonly IHostnameResolutionSettingsState _hostnameResolutionState;
    private readonly ILogger<SettingsOverridesService> _logger;

    public SettingsOverridesService(
        ISettingsOverridesStore store,
        IRecordingSettingsState recordingState,
        IHostnameResolutionSettingsState hostnameResolutionState,
        ILogger<SettingsOverridesService> logger
    ) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recordingState);
        ArgumentNullException.ThrowIfNull(hostnameResolutionState);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _recordingState = recordingState;
        _hostnameResolutionState = hostnameResolutionState;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        IReadOnlyDictionary<string, string> overrides;
        try {
            overrides = await _store.ListAllAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex,
                "Failed to load settings_overrides at startup; in-memory state stays at defaults");
            return;
        }
        if (overrides.Count == 0) {
            _logger.LogDebug("No settings overrides persisted; state stays at defaults");
            return;
        }

        // Apply the Recording section. Defaults come from the singleton's
        // current value (which was seeded from IOptions<RecordingOptions>);
        // override only what's persisted.
        var filterSelfTraffic = ReadBoolOverride(
            overrides, SettingsKeys.RecordingFilterSelfTraffic, _recordingState.FilterSelfTraffic);
        _recordingState.SetSettings(filterSelfTraffic);

        // Apply the Hostname Resolution section. All three are bundled into
        // one SetSettings call so the state singleton sees the change as a
        // single atomic transition.
        var enablePreload = ReadBoolOverride(
            overrides, SettingsKeys.DnsEnablePreload, _hostnameResolutionState.EnablePreload);
        var enableReverseDnsFallback = ReadBoolOverride(
            overrides, SettingsKeys.DnsEnableReverseDnsFallback, _hostnameResolutionState.EnableReverseDnsFallback);
        var enableSniCapture = ReadBoolOverride(
            overrides, SettingsKeys.SniEnableSniCapture, _hostnameResolutionState.EnableSniCapture);
        _hostnameResolutionState.SetSettings(enablePreload, enableReverseDnsFallback, enableSniCapture);

        _logger.LogInformation(
            "Applied {Count} settings overrides at startup", overrides.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool ReadBoolOverride(
        IReadOnlyDictionary<string, string> overrides, string key, bool defaultValue
    ) {
        if (!overrides.TryGetValue(key, out var valueJson)) return defaultValue;
        try {
            var parsed = JsonSerializer.Deserialize<bool>(valueJson);
            return parsed;
        } catch (JsonException ex) {
            _logger.LogWarning(ex,
                "Settings override {Key} has malformed JSON ({ValueJson}); falling back to default {Default}",
                key, valueJson, defaultValue);
            return defaultValue;
        }
    }
}
