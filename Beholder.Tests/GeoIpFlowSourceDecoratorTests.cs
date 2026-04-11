using System.Net;
using Beholder.Core;
using Beholder.Daemon.GeoIp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class GeoIpFlowSourceDecoratorTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 11, 15, 30, 45, TimeSpan.Zero);

    [Fact]
    public void Constructor_NullInner_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => new GeoIpFlowSourceDecorator(
                null!,
                new FakeGeoIpResolver(),
                NullLogger<GeoIpFlowSourceDecorator>.Instance));
    }

    [Fact]
    public void Constructor_NullResolver_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => new GeoIpFlowSourceDecorator(
                new FakeFlowSource(),
                null!,
                NullLogger<GeoIpFlowSourceDecorator>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => new GeoIpFlowSourceDecorator(
                new FakeFlowSource(),
                new FakeGeoIpResolver(),
                null!));
    }

    [Fact]
    public async Task OnFlowEvent_PublicIp_EnrichedWithResolvedCountry() {
        var source = new FakeFlowSource();
        var resolver = new FakeGeoIpResolver();
        resolver.Map("8.8.8.8", CountryCode.FromAlpha2("US"));
        var decorator = new GeoIpFlowSourceDecorator(
            source, resolver, NullLogger<GeoIpFlowSourceDecorator>.Instance);
        await decorator.StartAsync(CancellationToken.None);
        FlowEvent? captured = null;
        decorator.OnFlowEvent += e => captured = e;

        source.EmitFlowEvent(BuildEvent(remoteIp: "8.8.8.8", country: CountryCode.Unknown));

        Assert.NotNull(captured);
        Assert.Equal(CountryCode.FromAlpha2("US"), captured!.Country);
    }

    [Fact]
    public async Task OnFlowEvent_PrivateIp_EnrichedWithLocal() {
        var source = new FakeFlowSource();
        var resolver = new FakeGeoIpResolver();
        resolver.Map("192.168.1.1", CountryCode.Local);
        var decorator = new GeoIpFlowSourceDecorator(
            source, resolver, NullLogger<GeoIpFlowSourceDecorator>.Instance);
        await decorator.StartAsync(CancellationToken.None);
        FlowEvent? captured = null;
        decorator.OnFlowEvent += e => captured = e;

        source.EmitFlowEvent(BuildEvent(remoteIp: "192.168.1.1", country: CountryCode.Unknown));

        Assert.NotNull(captured);
        Assert.Equal(CountryCode.Local, captured!.Country);
    }

    [Fact]
    public async Task OnFlowEvent_ResolverThrows_ForwardsOriginalEvent() {
        var source = new FakeFlowSource();
        var resolver = new FakeGeoIpResolver { ThrowOnResolve = true };
        var decorator = new GeoIpFlowSourceDecorator(
            source, resolver, NullLogger<GeoIpFlowSourceDecorator>.Instance);
        await decorator.StartAsync(CancellationToken.None);
        FlowEvent? captured = null;
        decorator.OnFlowEvent += e => captured = e;
        var original = BuildEvent(remoteIp: "8.8.8.8", country: CountryCode.FromAlpha2("US"));

        source.EmitFlowEvent(original);

        Assert.NotNull(captured);
        Assert.Same(original, captured);
        Assert.Equal(CountryCode.FromAlpha2("US"), captured!.Country);
    }

    [Fact]
    public async Task StartAsync_SubscribesToInner_AndCallsInnerStart() {
        var source = new FakeFlowSource();
        var decorator = new GeoIpFlowSourceDecorator(
            source, new FakeGeoIpResolver(), NullLogger<GeoIpFlowSourceDecorator>.Instance);
        Assert.Equal(0, source.SubscriberCount);

        await decorator.StartAsync(CancellationToken.None);

        Assert.Equal(1, source.SubscriberCount);
        Assert.True(source.Started);
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromInner_AndCallsInnerStop() {
        var source = new FakeFlowSource();
        var decorator = new GeoIpFlowSourceDecorator(
            source, new FakeGeoIpResolver(), NullLogger<GeoIpFlowSourceDecorator>.Instance);
        await decorator.StartAsync(CancellationToken.None);
        var forwardedAfterStop = 0;
        decorator.OnFlowEvent += _ => forwardedAfterStop++;

        await decorator.StopAsync(CancellationToken.None);
        source.EmitFlowEvent(BuildEvent());

        Assert.Equal(0, source.SubscriberCount);
        Assert.True(source.Stopped);
        Assert.Equal(0, forwardedAfterStop);
    }

    [Fact]
    public async Task AllFieldsPreservedExceptCountry() {
        var source = new FakeFlowSource();
        var resolver = new FakeGeoIpResolver();
        resolver.Map("8.8.8.8", CountryCode.FromAlpha2("US"));
        var decorator = new GeoIpFlowSourceDecorator(
            source, resolver, NullLogger<GeoIpFlowSourceDecorator>.Instance);
        await decorator.StartAsync(CancellationToken.None);
        FlowEvent? captured = null;
        decorator.OnFlowEvent += e => captured = e;
        var original = new FlowEvent(
            processId: 9876,
            processName: "unique.exe",
            processPath: @"C:\path\to\unique.exe",
            remoteAddress: IPAddress.Parse("8.8.8.8"),
            remotePort: 8443,
            bytesIn: 123,
            bytesOut: 456,
            country: CountryCode.Unknown,
            timestamp: FixedTimestamp);

        source.EmitFlowEvent(original);

        Assert.NotNull(captured);
        Assert.Equal(original.ProcessId, captured!.ProcessId);
        Assert.Equal(original.ProcessName, captured.ProcessName);
        Assert.Equal(original.ProcessPath, captured.ProcessPath);
        Assert.Equal(original.RemoteAddress, captured.RemoteAddress);
        Assert.Equal(original.RemotePort, captured.RemotePort);
        Assert.Equal(original.BytesIn, captured.BytesIn);
        Assert.Equal(original.BytesOut, captured.BytesOut);
        Assert.Equal(original.Timestamp, captured.Timestamp);
        Assert.Equal(CountryCode.FromAlpha2("US"), captured.Country);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("192.168.1.1")]
    [InlineData("::1")]
    [InlineData(null)]
    public void NullGeoIpResolver_Resolve_AnyAddress_ReturnsUnknown(string? address) {
        var resolver = new NullGeoIpResolver();
        var parsed = address is null ? null : IPAddress.Parse(address);

        var result = resolver.Resolve(parsed!);

        Assert.Equal(CountryCode.Unknown, result);
    }

    private static FlowEvent BuildEvent(
        string processName = "curl.exe",
        string processPath = @"C:\Windows\System32\curl.exe",
        string remoteIp = "8.8.8.8",
        int remotePort = 443,
        long bytesIn = 0,
        long bytesOut = 0,
        CountryCode? country = null
    ) {
        return new FlowEvent(
            processId: 4242,
            processName: processName,
            processPath: processPath,
            remoteAddress: IPAddress.Parse(remoteIp),
            remotePort: remotePort,
            bytesIn: bytesIn,
            bytesOut: bytesOut,
            country: country ?? CountryCode.Unknown,
            timestamp: FixedTimestamp);
    }

    private sealed class FakeFlowSource : IFlowSource {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public event Action<FlowEvent>? OnFlowEvent;

        public Task StartAsync(CancellationToken cancellationToken) {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) {
            Stopped = true;
            return Task.CompletedTask;
        }

        public void EmitFlowEvent(FlowEvent flowEvent) => OnFlowEvent?.Invoke(flowEvent);

        public int SubscriberCount => OnFlowEvent?.GetInvocationList().Length ?? 0;
    }

    private sealed class FakeGeoIpResolver : IGeoIpResolver {
        private readonly Dictionary<IPAddress, CountryCode> _map = new();

        public bool ThrowOnResolve { get; set; }

        public void Map(string address, CountryCode code) =>
            _map[IPAddress.Parse(address)] = code;

        public CountryCode Resolve(IPAddress address) {
            if (ThrowOnResolve) throw new InvalidOperationException("fake resolver failure");
            return _map.TryGetValue(address, out var code) ? code : CountryCode.Unknown;
        }
    }
}
