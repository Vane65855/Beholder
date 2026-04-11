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
    public void AlertKind_ToProto_AllValues_MapToMatchingOrdinal(Core.AlertKind value) {
        Assert.Equal((int)value, (int)value.ToProto());
    }

    [Fact]
    public void ToUnixTimeNanoseconds_MatchesMillisTimesMillion() {
        var moment = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L);

        var ns = moment.ToUnixTimeNanoseconds();

        Assert.Equal(1_700_000_000_000_000_000L, ns);
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
