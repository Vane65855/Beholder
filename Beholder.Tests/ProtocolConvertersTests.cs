using Beholder.Protocol;
using Core = Beholder.Core;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public class ProtocolConvertersTests {
    private static readonly DateTimeOffset FixedTimestamp =
        DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L);

    private static readonly DateTimeOffset SecondTimestamp =
        DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_001_000L);

    [Fact]
    public void CounterSnapshot_ToProto_AllFieldsPreserved() {
        var byCountry = new Dictionary<Core.CountryCode, long> {
            [Core.CountryCode.FromAlpha2("US")] = 5000,
            [Core.CountryCode.FromAlpha2("JP")] = 250,
            [Core.CountryCode.Local] = 42,
        };
        var source = BuildCounterSnapshot(byCountry);

        var proto = source.ToProto();

        Assert.Equal("firefox.exe", proto.ProcessName);
        Assert.Equal(@"C:\Program Files\Mozilla Firefox\firefox.exe", proto.ProcessPath);
        Assert.Equal(100_000L, proto.TotalBytesIn);
        Assert.Equal(50_000L, proto.TotalBytesOut);
        Assert.Equal(1_000L, proto.DeltaBytesIn);
        Assert.Equal(500L, proto.DeltaBytesOut);
        Assert.Equal(4, proto.ActiveConnectionCount);
        Assert.Equal(FixedTimestamp.ToUnixTimeNanoseconds(), proto.TimestampUnixNs);
        Assert.Equal(3, proto.BytesOutByCountry.Count);
        Assert.Equal(5000L, proto.BytesOutByCountry["US"]);
        Assert.Equal(250L, proto.BytesOutByCountry["JP"]);
        Assert.Equal(42L, proto.BytesOutByCountry["--"]);
    }

    [Fact]
    public void CounterSnapshot_EmptyBytesOutByCountry_ProducesEmptyMap() {
        var source = BuildCounterSnapshot(new Dictionary<Core.CountryCode, long>());

        var proto = source.ToProto();

        Assert.Empty(proto.BytesOutByCountry);
    }

    [Fact]
    public void FirewallRule_ToProto_AllFieldsPreserved() {
        var source = new Core.FirewallRule(
            id: 17,
            processPath: @"C:\Windows\System32\svchost.exe",
            direction: Core.Direction.Outbound,
            action: Core.FirewallAction.Block,
            source: Core.RuleSource.Manual,
            createdAt: FixedTimestamp,
            updatedAt: SecondTimestamp);

        var proto = source.ToProto();

        Assert.Equal(17, proto.Id);
        Assert.Equal(@"C:\Windows\System32\svchost.exe", proto.ProcessPath);
        Assert.Equal(Local.Direction.Outbound, proto.Direction);
        Assert.Equal(Local.FirewallAction.Block, proto.Action);
        Assert.Equal(Local.RuleSource.Manual, proto.Source);
        Assert.Equal(FixedTimestamp.ToUnixTimeNanoseconds(), proto.CreatedAtUnixNs);
        Assert.Equal(SecondTimestamp.ToUnixTimeNanoseconds(), proto.UpdatedAtUnixNs);
    }

    [Fact]
    public void Alert_ToProto_AllFieldsPreserved_UnreadAlert() {
        var source = new Core.Alert(
            seq: 42,
            kind: Core.AlertKind.NewProcess,
            processPath: @"C:\bin\new.exe",
            summary: "A new process appeared",
            timestamp: FixedTimestamp,
            firstViewedAt: null);

        var proto = source.ToProto();

        Assert.Equal(42L, proto.Seq);
        Assert.Equal(Local.AlertKind.NewProcess, proto.Kind);
        Assert.Equal(@"C:\bin\new.exe", proto.ProcessPath);
        Assert.Equal("A new process appeared", proto.Summary);
        Assert.Equal(FixedTimestamp.ToUnixTimeNanoseconds(), proto.TimestampUnixNs);
        Assert.Equal(0L, proto.FirstViewedAtUnixNs);
    }

    [Fact]
    public void Alert_ToProto_AllFieldsPreserved_ReadAlert() {
        var source = new Core.Alert(
            seq: 43,
            kind: Core.AlertKind.HashChanged,
            processPath: @"C:\bin\changed.exe",
            summary: "Binary hash drifted",
            timestamp: FixedTimestamp,
            firstViewedAt: SecondTimestamp);

        var proto = source.ToProto();

        Assert.Equal(SecondTimestamp.ToUnixTimeNanoseconds(), proto.FirstViewedAtUnixNs);
    }

    [Theory]
    [InlineData(Core.Direction.Inbound)]
    [InlineData(Core.Direction.Outbound)]
    public void Direction_ToProto_And_FromProto_RoundTrip(Core.Direction value) {
        Assert.Equal(value, value.ToProto().FromProto());
        Assert.Equal((int)value, (int)value.ToProto());
    }

    [Theory]
    [InlineData(Core.FirewallAction.Allow)]
    [InlineData(Core.FirewallAction.Block)]
    public void FirewallAction_ToProto_And_FromProto_RoundTrip(Core.FirewallAction value) {
        Assert.Equal(value, value.ToProto().FromProto());
        Assert.Equal((int)value, (int)value.ToProto());
    }

    [Theory]
    [InlineData(Core.RuleSource.Manual)]
    [InlineData(Core.RuleSource.Default)]
    [InlineData(Core.RuleSource.Remote)]
    public void RuleSource_ToProto_And_FromProto_RoundTrip(Core.RuleSource value) {
        Assert.Equal(value, value.ToProto().FromProto());
        Assert.Equal((int)value, (int)value.ToProto());
    }

    [Theory]
    [InlineData(Core.AlertKind.Unknown)]
    [InlineData(Core.AlertKind.NewProcess)]
    [InlineData(Core.AlertKind.HashChanged)]
    [InlineData(Core.AlertKind.ChainError)]
    public void AlertKind_ToProto_And_FromProto_RoundTrip(Core.AlertKind value) {
        Assert.Equal(value, value.ToProto().FromProto());
        Assert.Equal((int)value, (int)value.ToProto());
    }

    [Fact]
    public void ToUnixTimeNanoseconds_MatchesMillisTimesMillion() {
        var moment = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L);

        var ns = moment.ToUnixTimeNanoseconds();

        Assert.Equal(1_700_000_000_000_000_000L, ns);
    }

    // ---- Traffic query converter tests ----

    [Fact]
    public void TrafficTimePoint_ToProto_AllFieldsPreserved() {
        var source = new Core.TrafficTimePoint(FixedTimestamp, 5000, 3000);

        var proto = source.ToProto();

        Assert.Equal(FixedTimestamp.ToUnixTimeNanoseconds(), proto.TimestampUnixNs);
        Assert.Equal(5000, proto.BytesIn);
        Assert.Equal(3000, proto.BytesOut);
    }

    [Fact]
    public void TrafficTimePoint_ToDomain_RoundTrips() {
        var source = new Core.TrafficTimePoint(FixedTimestamp, 5000, 3000);
        var roundTripped = source.ToProto().ToDomain();

        Assert.Equal(source, roundTripped);
    }

    [Fact]
    public void DestinationSummary_ToProto_AllFieldsPreserved() {
        var source = new Core.DestinationSummary(
            "93.184.216.34", "example.com",
            Core.CountryCode.FromAlpha2("US"), 10_000, 5_000, 3);

        var proto = source.ToProto();

        Assert.Equal("93.184.216.34", proto.RemoteAddress);
        Assert.Equal("example.com", proto.Hostname);
        Assert.Equal("US", proto.Country);
        Assert.Equal(10_000, proto.TotalBytesIn);
        Assert.Equal(5_000, proto.TotalBytesOut);
        Assert.Equal(3, proto.ConnectionCount);
    }

    [Fact]
    public void DestinationSummary_ToProto_NullHostname_BecomesEmptyString() {
        var source = new Core.DestinationSummary(
            "1.2.3.4", null, Core.CountryCode.Unknown, 0, 0, 0);

        var proto = source.ToProto();

        Assert.Equal("", proto.Hostname);
    }

    [Fact]
    public void DestinationSummary_ToDomain_RoundTrips() {
        var source = new Core.DestinationSummary(
            "93.184.216.34", "example.com",
            Core.CountryCode.FromAlpha2("US"), 10_000, 5_000, 3);
        var roundTripped = source.ToProto().ToDomain();

        Assert.Equal(source, roundTripped);
    }

    [Fact]
    public void DestinationSummary_ToDomain_EmptyHostname_BecomesNull() {
        var proto = new Local.DestinationSummary {
            RemoteAddress = "1.2.3.4",
            Hostname = "",
            Country = "??",
            TotalBytesIn = 0,
            TotalBytesOut = 0,
            ConnectionCount = 0
        };

        var domain = proto.ToDomain();

        Assert.Null(domain.Hostname);
    }

    [Fact]
    public void CountryTrafficSummary_ToProto_AllFieldsPreserved() {
        var source = new Core.CountryTrafficSummary(
            Core.CountryCode.FromAlpha2("DE"), 50_000, 25_000);

        var proto = source.ToProto();

        Assert.Equal("DE", proto.Country);
        Assert.Equal(50_000, proto.TotalBytesIn);
        Assert.Equal(25_000, proto.TotalBytesOut);
    }

    [Fact]
    public void CountryTrafficSummary_ToDomain_RoundTrips() {
        var source = new Core.CountryTrafficSummary(
            Core.CountryCode.FromAlpha2("DE"), 50_000, 25_000);
        var roundTripped = source.ToProto().ToDomain();

        Assert.Equal(source, roundTripped);
    }

    [Fact]
    public void CountryTrafficSummary_ToDomain_SentinelCountryCodes() {
        var localProto = new Local.CountryTrafficSummary { Country = "--", TotalBytesIn = 1, TotalBytesOut = 2 };
        var unknownProto = new Local.CountryTrafficSummary { Country = "??", TotalBytesIn = 3, TotalBytesOut = 4 };

        Assert.Equal(Core.CountryCode.Local, localProto.ToDomain().Country);
        Assert.Equal(Core.CountryCode.Unknown, unknownProto.ToDomain().Country);
    }

    [Fact]
    public void ProtocolBreakdownSummary_ToProto_AllFieldsPreserved() {
        var source = new Core.ProtocolBreakdownSummary("HTTPS", "TCP", 10_000, 5_000);

        var proto = source.ToProto();

        Assert.Equal("HTTPS", proto.ProtocolName);
        Assert.Equal("TCP", proto.Transport);
        Assert.Equal(10_000, proto.TotalBytesIn);
        Assert.Equal(5_000, proto.TotalBytesOut);
    }

    [Fact]
    public void ProtocolBreakdownSummary_ToDomain_RoundTrips() {
        var source = new Core.ProtocolBreakdownSummary("DNS", "TCP", 100, 50);
        var roundTripped = source.ToProto().ToDomain();

        Assert.Equal(source, roundTripped);
    }

    [Fact]
    public void FromUnixTimeNanoseconds_ConvertsCorrectly() {
        var ns = 1_700_000_000_000_000_000L;
        var result = ns.FromUnixTimeNanoseconds();
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L), result);
    }

    private static Core.CounterSnapshot BuildCounterSnapshot(
        IReadOnlyDictionary<Core.CountryCode, long> byCountry
    ) {
        return new Core.CounterSnapshot(
            processName: "firefox.exe",
            processPath: @"C:\Program Files\Mozilla Firefox\firefox.exe",
            totalBytesIn: 100_000,
            totalBytesOut: 50_000,
            deltaBytesIn: 1_000,
            deltaBytesOut: 500,
            activeConnectionCount: 4,
            bytesOutByCountry: byCountry,
            timestamp: FixedTimestamp);
    }
}
