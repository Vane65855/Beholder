using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class SettingsRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeRecordingSettingsState _recordingState;
    private readonly FakeHostnameResolutionSettingsState _hostnameState;
    private readonly FakeSettingsOverridesStore _overridesStore;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public SettingsRpcTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();

        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        var timeProvider = new FakeTimeProvider(FixedTimestamp);
        _eventStore = new SqliteEventStore(connectionFactory, timeProvider);
        _recordingState = new FakeRecordingSettingsState(initialFilterSelfTraffic: true);
        _hostnameState = new FakeHostnameResolutionSettingsState();
        _overridesStore = new FakeSettingsOverridesStore();
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline,
            new FakeFirewallRuleStore(), new FakeAlertStore(),
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            _eventStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(), new FakeStorageStatsProvider(),
            _recordingState, _hostnameState, _overridesStore,
            timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetSettings_EchoesCurrentState() {
        _recordingState.SetSettings(false);
        _hostnameState.SetSettings(false, true, false);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.GetSettings(new Local.GetSettingsRequest(), context);

        Assert.False(response.Recording.FilterSelfTraffic);
        Assert.False(response.HostnameResolution.EnablePreload);
        Assert.True(response.HostnameResolution.EnableReverseDnsFallback);
        Assert.False(response.HostnameResolution.EnableSniCapture);
    }

    [Fact]
    public async Task SetRecordingSettings_RealTransition_PersistsAndAppendsChain() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.SetRecordingSettings(
            new Local.SetRecordingSettingsRequest {
                Values = new Local.RecordingSettingsValues { FilterSelfTraffic = false },
            }, context);

        Assert.True(response.Success);
        Assert.False(response.Values.FilterSelfTraffic);
        Assert.False(_recordingState.FilterSelfTraffic);
        Assert.Equal(1, _overridesStore.UpsertCallCount);
        var persisted = await _overridesStore.GetAsync(
            SettingsKeys.RecordingFilterSelfTraffic, CancellationToken.None);
        Assert.Equal("false", persisted);
        var verification = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.Equal(1, verification.RowsVerified);
    }

    [Fact]
    public async Task SetRecordingSettings_NoOp_SkipsPersistenceAndChain() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        // _recordingState seeded with FilterSelfTraffic=true; assert same.

        var response = await _service.SetRecordingSettings(
            new Local.SetRecordingSettingsRequest {
                Values = new Local.RecordingSettingsValues { FilterSelfTraffic = true },
            }, context);

        Assert.True(response.Success);
        Assert.True(response.Values.FilterSelfTraffic);
        Assert.Equal(0, _overridesStore.UpsertCallCount);
        var verification = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.Equal(0, verification.RowsVerified);
    }

    [Fact]
    public async Task SetRecordingSettings_NullValues_ThrowsInvalidArgument() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.SetRecordingSettings(new Local.SetRecordingSettingsRequest(), context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task SetRecordingSettings_PersistenceFails_ReturnsSoftFailure() {
        _overridesStore.ThrowOnUpsert = true;
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.SetRecordingSettings(
            new Local.SetRecordingSettingsRequest {
                Values = new Local.RecordingSettingsValues { FilterSelfTraffic = false },
            }, context);

        // In-memory state was updated; persistence failed; soft-fail with message.
        Assert.False(response.Success);
        Assert.Contains("Failed to persist", response.Message);
    }

    [Fact]
    public async Task SetHostnameResolutionSettings_RealTransition_PersistsAllThreeKeysAndChainsOnce() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.SetHostnameResolutionSettings(
            new Local.SetHostnameResolutionSettingsRequest {
                Values = new Local.HostnameResolutionSettingsValues {
                    EnablePreload = false,
                    EnableReverseDnsFallback = false,
                    EnableSniCapture = false,
                },
            }, context);

        Assert.True(response.Success);
        Assert.False(response.Values.EnablePreload);
        Assert.False(response.Values.EnableReverseDnsFallback);
        Assert.False(response.Values.EnableSniCapture);
        // Three separate upserts (one per dotted key).
        Assert.Equal(3, _overridesStore.UpsertCallCount);
        // One chain entry covers the whole bundle.
        var verification = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.Equal(1, verification.RowsVerified);
    }

    [Fact]
    public async Task SetHostnameResolutionSettings_NoOp_SkipsPersistenceAndChain() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        // _hostnameState seeded with all three = true.

        var response = await _service.SetHostnameResolutionSettings(
            new Local.SetHostnameResolutionSettingsRequest {
                Values = new Local.HostnameResolutionSettingsValues {
                    EnablePreload = true,
                    EnableReverseDnsFallback = true,
                    EnableSniCapture = true,
                },
            }, context);

        Assert.True(response.Success);
        Assert.Equal(0, _overridesStore.UpsertCallCount);
        var verification = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.Equal(0, verification.RowsVerified);
    }

    [Fact]
    public async Task SetHostnameResolutionSettings_PartialChange_PersistsAllThreeKeysStill() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        // Only flip one field — the daemon still re-asserts all three values
        // to the store so future startup loads see the user's full view.
        var response = await _service.SetHostnameResolutionSettings(
            new Local.SetHostnameResolutionSettingsRequest {
                Values = new Local.HostnameResolutionSettingsValues {
                    EnablePreload = true,
                    EnableReverseDnsFallback = false,
                    EnableSniCapture = true,
                },
            }, context);

        Assert.True(response.Success);
        Assert.False(response.Values.EnableReverseDnsFallback);
        Assert.Equal(3, _overridesStore.UpsertCallCount);
    }

    [Fact]
    public async Task SetHostnameResolutionSettings_NullValues_ThrowsInvalidArgument() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.SetHostnameResolutionSettings(
                new Local.SetHostnameResolutionSettingsRequest(), context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
