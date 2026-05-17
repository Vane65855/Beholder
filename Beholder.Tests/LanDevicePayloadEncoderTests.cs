using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public sealed class LanDevicePayloadEncoderTests {
    [Fact]
    public void EncodeFirstSeen_DecodeFirstSeen_RoundTripsAllFields() {
        var encoded = LanDevicePayloadEncoder.EncodeFirstSeen(
            mac: "aa:bb:cc:dd:ee:ff",
            ip: "192.168.1.42",
            vendor: "AcmeCorp",
            hostname: "router.lan");

        var decoded = LanDevicePayloadEncoder.TryDecodeFirstSeen(encoded);

        Assert.NotNull(decoded);
        Assert.Equal("aa:bb:cc:dd:ee:ff", decoded.Mac);
        Assert.Equal("192.168.1.42", decoded.Ip);
        Assert.Equal("AcmeCorp", decoded.Vendor);
        Assert.Equal("router.lan", decoded.Hostname);
    }

    [Fact]
    public void EncodeFirstSeen_NullVendorAndHostname_RoundTripAsNull() {
        var encoded = LanDevicePayloadEncoder.EncodeFirstSeen(
            mac: "aa:bb:cc:dd:ee:ff",
            ip: "192.168.1.42",
            vendor: null,
            hostname: null);

        var decoded = LanDevicePayloadEncoder.TryDecodeFirstSeen(encoded);

        Assert.NotNull(decoded);
        Assert.Null(decoded.Vendor);
        Assert.Null(decoded.Hostname);
    }

    [Fact]
    public void TryDecodeFirstSeen_MalformedPayload_ReturnsNull() {
        var garbage = "not-json"u8.ToArray();

        var decoded = LanDevicePayloadEncoder.TryDecodeFirstSeen(garbage);

        Assert.Null(decoded);
    }

    [Fact]
    public void TryDecodeFirstSeen_MissingRequiredField_ReturnsNull() {
        // Valid JSON but missing the required 'ip' field.
        var partial = """{"mac":"aa:bb:cc:dd:ee:ff"}"""u8.ToArray();

        var decoded = LanDevicePayloadEncoder.TryDecodeFirstSeen(partial);

        Assert.Null(decoded);
    }

    [Fact]
    public void EncodeFirstSeen_NullMac_ThrowsArgumentException() {
        Assert.Throws<ArgumentNullException>(() => LanDevicePayloadEncoder.EncodeFirstSeen(
            mac: null!, ip: "1.2.3.4", vendor: null, hostname: null));
    }

    [Fact]
    public void EncodeFirstSeen_NullIp_ThrowsArgumentException() {
        Assert.Throws<ArgumentNullException>(() => LanDevicePayloadEncoder.EncodeFirstSeen(
            mac: "aa:bb:cc:dd:ee:ff", ip: null!, vendor: null, hostname: null));
    }

    [Fact]
    public void EncodeMacChanged_DecodeMacChanged_RoundTripsAllFields() {
        var oldMacFirstSeen = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);

        var encoded = LanDevicePayloadEncoder.EncodeMacChanged(
            ip: "192.168.1.42",
            oldMac: "aa:aa:aa:aa:aa:aa",
            newMac: "bb:bb:bb:bb:bb:bb",
            oldMacFirstSeen: oldMacFirstSeen);

        var decoded = LanDevicePayloadEncoder.TryDecodeMacChanged(encoded);

        Assert.NotNull(decoded);
        Assert.Equal("192.168.1.42", decoded.Ip);
        Assert.Equal("aa:aa:aa:aa:aa:aa", decoded.OldMac);
        Assert.Equal("bb:bb:bb:bb:bb:bb", decoded.NewMac);
        Assert.Equal(oldMacFirstSeen, decoded.OldMacFirstSeen);
    }

    [Fact]
    public void TryDecodeMacChanged_MalformedPayload_ReturnsNull() {
        var garbage = "{not-valid-json"u8.ToArray();

        var decoded = LanDevicePayloadEncoder.TryDecodeMacChanged(garbage);

        Assert.Null(decoded);
    }

    [Fact]
    public void EncodeMacChanged_NullIpOrMac_ThrowsArgumentException() {
        var when = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentNullException>(() => LanDevicePayloadEncoder.EncodeMacChanged(
            ip: null!, oldMac: "aa:aa:aa:aa:aa:aa", newMac: "bb:bb:bb:bb:bb:bb", oldMacFirstSeen: when));
        Assert.Throws<ArgumentNullException>(() => LanDevicePayloadEncoder.EncodeMacChanged(
            ip: "1.2.3.4", oldMac: null!, newMac: "bb:bb:bb:bb:bb:bb", oldMacFirstSeen: when));
        Assert.Throws<ArgumentNullException>(() => LanDevicePayloadEncoder.EncodeMacChanged(
            ip: "1.2.3.4", oldMac: "aa:aa:aa:aa:aa:aa", newMac: null!, oldMacFirstSeen: when));
    }

    [Fact]
    public void EncodeFirstSeen_TwoCallsSameInput_ProduceByteIdenticalOutput() {
        // Determinism guard: chain hash covers exact payload bytes.
        var a = LanDevicePayloadEncoder.EncodeFirstSeen(
            "aa:bb:cc:dd:ee:ff", "192.168.1.42", "AcmeCorp", "router.lan");
        var b = LanDevicePayloadEncoder.EncodeFirstSeen(
            "aa:bb:cc:dd:ee:ff", "192.168.1.42", "AcmeCorp", "router.lan");

        Assert.Equal(a, b);
    }
}
