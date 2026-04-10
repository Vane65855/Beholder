using System.Net;
using Beholder.Core;

namespace Beholder.Tests;

public class FlowEventTests {
    private static FlowEvent BuildValid(long bytesOut = 2048) {
        return new FlowEvent(
            processId: 4242,
            processName: "firefox.exe",
            processPath: @"C:\Program Files\Mozilla Firefox\firefox.exe",
            remoteAddress: IPAddress.Parse("8.8.8.8"),
            remotePort: 443,
            bytesIn: 1024,
            bytesOut: bytesOut,
            country: CountryCode.FromAlpha2("US"),
            timestamp: new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero)
        );
    }

    [Fact]
    public void Constructor_ValidArguments_PopulatesAllProperties() {
        var flow = BuildValid();

        Assert.Equal(4242, flow.ProcessId);
        Assert.Equal("firefox.exe", flow.ProcessName);
        Assert.Equal(@"C:\Program Files\Mozilla Firefox\firefox.exe", flow.ProcessPath);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), flow.RemoteAddress);
        Assert.Equal(443, flow.RemotePort);
        Assert.Equal(1024, flow.BytesIn);
        Assert.Equal(2048, flow.BytesOut);
        Assert.Equal(CountryCode.FromAlpha2("US"), flow.Country);
        Assert.Equal(new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero), flow.Timestamp);
    }

    [Fact]
    public void Constructor_NullProcessName_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new FlowEvent(
            processId: 1,
            processName: null!,
            processPath: "/usr/bin/curl",
            remoteAddress: IPAddress.Loopback,
            remotePort: 80,
            bytesIn: 0,
            bytesOut: 0,
            country: CountryCode.Local,
            timestamp: DateTimeOffset.UnixEpoch
        ));
    }

    [Fact]
    public void Constructor_NullRemoteAddress_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new FlowEvent(
            processId: 1,
            processName: "curl",
            processPath: "/usr/bin/curl",
            remoteAddress: null!,
            remotePort: 80,
            bytesIn: 0,
            bytesOut: 0,
            country: CountryCode.Local,
            timestamp: DateTimeOffset.UnixEpoch
        ));
    }

    [Fact]
    public void Constructor_NegativeBytesIn_ThrowsArgumentOutOfRangeException() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowEvent(
            processId: 1,
            processName: "curl",
            processPath: "/usr/bin/curl",
            remoteAddress: IPAddress.Loopback,
            remotePort: 80,
            bytesIn: -1,
            bytesOut: 0,
            country: CountryCode.Local,
            timestamp: DateTimeOffset.UnixEpoch
        ));
    }

    [Fact]
    public void Equality_IdenticalProperties_AreEqual() {
        var first = BuildValid();
        var second = BuildValid();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Equality_DifferentBytesOut_AreNotEqual() {
        var first = BuildValid(bytesOut: 2048);
        var second = BuildValid(bytesOut: 4096);

        Assert.NotEqual(first, second);
    }
}
