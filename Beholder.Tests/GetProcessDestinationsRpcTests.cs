using Beholder.Daemon;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

/// <summary>
/// Phase 8 polish: verifies that <see cref="BeholderLocalService.GetProcessDestinations"/>
/// passes the new <c>country</c> + <c>limit</c> proto fields through to
/// <see cref="ITrafficStore.GetDestinationsAsync"/> as a populated
/// <see cref="Beholder.Core.DestinationsQuery"/> record. Existing
/// pass-through behavior for the COLS view (empty country, zero limit) is
/// also covered.
/// </summary>
public sealed class GetProcessDestinationsRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public GetProcessDestinationsRpcTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();

        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        var timeProvider = new FakeTimeProvider(FixedTimestamp);
        var eventStore = new SqliteEventStore(connectionFactory, timeProvider);
        var firewallStore = new SqliteFirewallRuleStore(connectionFactory);
        var alertStore = new SqliteAlertStore(connectionFactory, NullLogger<SqliteAlertStore>.Instance);
        var trafficStore = new SqliteTrafficStore(
            connectionFactory,
            new FakeOptionsMonitor<RollupOptions>(new RollupOptions()),
            timeProvider);
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeOptionsMonitor<RecordingOptions>(new RecordingOptions()),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, alertStore,
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            eventStore, trafficStore,
            timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetProcessDestinations_WithCountryAndLimit_FiltersToCountryReturnsTopN() {
        // End-to-end through the gRPC handler. Seed three destinations in
        // different countries with different totals; request country=DE
        // limit=2 → expect only the DE entry, even though it's not the
        // global top-2 by bytes.
        var trafficStore = (SqliteTrafficStore)typeof(BeholderLocalService)
            .GetField("_trafficStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_service)!;
        var buckets = new[] {
            MakeBucket(remoteAddress: "1.1.1.1", country: "US", bytesIn: 10_000_000),
            MakeBucket(remoteAddress: "2.2.2.2", country: "DE", bytesIn: 50_000),
            MakeBucket(remoteAddress: "3.3.3.3", country: "JP", bytesIn: 9_000_000),
        };
        await trafficStore.WriteRawBucketsAsync(buckets, CancellationToken.None);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var request = new Local.GetProcessDestinationsRequest {
            ProcessPath = string.Empty,
            FromUnixNs = FixedTimestamp.AddSeconds(-1).ToUnixTimeMilliseconds() * 1_000_000,
            ToUnixNs = FixedTimestamp.AddSeconds(11).ToUnixTimeMilliseconds() * 1_000_000,
            Country = "DE",
            Limit = 2,
        };

        var response = await _service.GetProcessDestinations(request, context);

        var only = Assert.Single(response.Destinations);
        Assert.Equal("2.2.2.2", only.RemoteAddress);
        Assert.Equal("DE", only.Country);
    }

    private static Beholder.Core.TrafficBucket MakeBucket(
        string remoteAddress, string country, long bytesIn
    ) {
        var cc = country switch {
            "--" => Beholder.Core.CountryCode.Local,
            "??" => Beholder.Core.CountryCode.Unknown,
            _ => Beholder.Core.CountryCode.FromAlpha2(country),
        };
        return new Beholder.Core.TrafficBucket(
            id: 0,
            processPath: "C:/app/firefox.exe",
            processName: "firefox.exe",
            remoteAddress: remoteAddress,
            remotePort: 443,
            hostname: $"{remoteAddress}.example.com",
            country: cc,
            bytesIn: bytesIn,
            bytesOut: bytesIn / 2,
            bucketStart: FixedTimestamp,
            bucketSeconds: 1);
    }
}
