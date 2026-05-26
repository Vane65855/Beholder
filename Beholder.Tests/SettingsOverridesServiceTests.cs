using Beholder.Core;
using Beholder.Daemon.Pipeline;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class SettingsOverridesServiceTests {
    [Fact]
    public async Task StartAsync_EmptyStore_LeavesStateAtSeededDefaults() {
        var store = new FakeSettingsOverridesStore();
        var recording = new FakeRecordingSettingsState(initialFilterSelfTraffic: true);
        var hostname = new FakeHostnameResolutionSettingsState(
            initialEnablePreload: true,
            initialEnableReverseDnsFallback: true,
            initialEnableSniCapture: true);
        var service = new SettingsOverridesService(
            store, recording, hostname, new FakeAlertSettingsState(), NullLogger<SettingsOverridesService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.True(recording.FilterSelfTraffic);
        Assert.True(hostname.EnablePreload);
        Assert.True(hostname.EnableReverseDnsFallback);
        Assert.True(hostname.EnableSniCapture);
    }

    [Fact]
    public async Task StartAsync_PopulatedStore_AppliesOverrides() {
        var store = new FakeSettingsOverridesStore();
        store.Seed(SettingsKeys.RecordingFilterSelfTraffic, "false");
        store.Seed(SettingsKeys.DnsEnablePreload, "false");
        store.Seed(SettingsKeys.DnsEnableReverseDnsFallback, "false");
        store.Seed(SettingsKeys.SniEnableSniCapture, "false");
        var recording = new FakeRecordingSettingsState(initialFilterSelfTraffic: true);
        var hostname = new FakeHostnameResolutionSettingsState();
        var service = new SettingsOverridesService(
            store, recording, hostname, new FakeAlertSettingsState(), NullLogger<SettingsOverridesService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.False(recording.FilterSelfTraffic);
        Assert.False(hostname.EnablePreload);
        Assert.False(hostname.EnableReverseDnsFallback);
        Assert.False(hostname.EnableSniCapture);
    }

    [Fact]
    public async Task StartAsync_PartialOverrides_KeepsDefaultsForUnsetKeys() {
        var store = new FakeSettingsOverridesStore();
        // Only persist one of three Hostname Resolution toggles.
        store.Seed(SettingsKeys.DnsEnableReverseDnsFallback, "false");
        var recording = new FakeRecordingSettingsState(initialFilterSelfTraffic: true);
        var hostname = new FakeHostnameResolutionSettingsState(
            initialEnablePreload: true,
            initialEnableReverseDnsFallback: true,
            initialEnableSniCapture: true);
        var service = new SettingsOverridesService(
            store, recording, hostname, new FakeAlertSettingsState(), NullLogger<SettingsOverridesService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.True(recording.FilterSelfTraffic);    // unchanged default
        Assert.True(hostname.EnablePreload);         // unchanged default
        Assert.False(hostname.EnableReverseDnsFallback); // override applied
        Assert.True(hostname.EnableSniCapture);      // unchanged default
    }

    [Fact]
    public async Task StartAsync_MalformedJson_FallsBackToDefault() {
        var store = new FakeSettingsOverridesStore();
        store.Seed(SettingsKeys.RecordingFilterSelfTraffic, "not-a-bool");
        var recording = new FakeRecordingSettingsState(initialFilterSelfTraffic: true);
        var hostname = new FakeHostnameResolutionSettingsState();
        var service = new SettingsOverridesService(
            store, recording, hostname, new FakeAlertSettingsState(), NullLogger<SettingsOverridesService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Malformed override is logged + skipped; default is preserved.
        Assert.True(recording.FilterSelfTraffic);
    }

    [Fact]
    public async Task StartAsync_ListAllThrows_DoesNotPropagateException() {
        var store = new ThrowingOverridesStore();
        var recording = new FakeRecordingSettingsState();
        var hostname = new FakeHostnameResolutionSettingsState();
        var service = new SettingsOverridesService(
            store, recording, hostname, new FakeAlertSettingsState(), NullLogger<SettingsOverridesService>.Instance);

        // StartAsync must not throw — daemon startup proceeds with defaults.
        await service.StartAsync(CancellationToken.None);

        Assert.True(recording.FilterSelfTraffic);
    }

    private sealed class ThrowingOverridesStore : ISettingsOverridesStore {
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated");
        public Task UpsertAsync(string name, string valueJson, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated");
        public Task<IReadOnlyDictionary<string, string>> ListAllAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated");
    }
}
