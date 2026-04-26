using Beholder.Core.Tls;

namespace Beholder.Tests;

/// <summary>
/// Covers <see cref="TlsClientHelloParser.TryExtractSni"/>'s happy path
/// (TLS 1.2 + 1.3 with SNI), the no-SNI / no-ClientHello paths (returns
/// false), and the malformed-input paths (returns false without throwing).
/// All ClientHello bytes are built synthetically — no network or fixture
/// files — so the tests stay deterministic and reviewable.
/// </summary>
public sealed class TlsClientHelloParserTests {
    [Fact]
    public void TryExtractSni_ValidTls12ClientHello_ReturnsHostname() {
        var bytes = ClientHelloBuilder.Build("example.com");

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.True(ok);
        Assert.Equal("example.com", sni);
    }

    [Fact]
    public void TryExtractSni_Tls13UsesLegacyVersionInRecord_ReturnsHostname() {
        // TLS 1.3 ClientHellos send 0x03 0x03 in the record header for
        // compatibility; our parser only validates the major version and
        // doesn't care about the minor.
        var bytes = ClientHelloBuilder.Build("www.example.com",
            recordVersion: 0x0303,
            handshakeVersion: 0x0304);

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.True(ok);
        Assert.Equal("www.example.com", sni);
    }

    [Fact]
    public void TryExtractSni_NoServerNameExtension_ReturnsFalse() {
        var bytes = ClientHelloBuilder.Build(sni: null);

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_EncryptedClientHelloPresent_ReturnsFalse() {
        // ECH (extension type 0xfe0d) must beat the outer SNI even if the
        // outer SNI looks valid — the real hostname is encrypted and the
        // outer SNI is a decoy.
        var bytes = ClientHelloBuilder.Build("decoy.example.com", includeEch: true);

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_NonHandshakeRecord_ReturnsFalse() {
        // ContentType 0x17 is application_data, not handshake.
        var bytes = ClientHelloBuilder.Build("example.com");
        bytes[0] = 0x17;

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_NotClientHello_ReturnsFalse() {
        // HandshakeType 0x02 is ServerHello, not ClientHello.
        var bytes = ClientHelloBuilder.Build("example.com");
        bytes[5] = 0x02;

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_TruncatedRecord_ReturnsFalse() {
        // Cut the record after the handshake header — the ClientHello body
        // is missing entirely.
        var full = ClientHelloBuilder.Build("example.com");
        var truncated = full.AsSpan(0, 10).ToArray();

        var ok = TlsClientHelloParser.TryExtractSni(truncated, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_TruncatedExtensions_ReturnsFalse() {
        // Cut the record midway through the extensions block, after declaring
        // a longer extension-list length than what's actually present.
        var full = ClientHelloBuilder.Build("example.com");
        var truncated = full.AsSpan(0, full.Length - 5).ToArray();

        var ok = TlsClientHelloParser.TryExtractSni(truncated, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_RandomBytes_ReturnsFalse() {
        // No structure at all — must not throw, must return false.
        var bytes = new byte[256];
        new Random(1234).NextBytes(bytes);
        // Force the ContentType byte to NOT be 0x16 so we don't accidentally
        // build a valid record by chance — that would be flaky.
        bytes[0] = 0x00;

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_EmptyHostname_ReturnsFalse() {
        // Length = 0 in the host_name entry. Should be rejected so we don't
        // ingest empty strings into the cache.
        var bytes = ClientHelloBuilder.Build(sni: "");

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_HostnameWithControlBytes_ReturnsFalse() {
        // The length field says we have a hostname, but the bytes contain
        // a NUL or other control byte. IsLikelyHostname should reject.
        var bytes = ClientHelloBuilder.Build("foo\x00bar");

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_EmptyInput_ReturnsFalse() {
        var ok = TlsClientHelloParser.TryExtractSni(ReadOnlySpan<byte>.Empty, out var sni);

        Assert.False(ok);
        Assert.Null(sni);
    }

    [Fact]
    public void TryExtractSni_HostnameWithDigitsAndHyphens_ReturnsHostname() {
        // Common real-world SNI: rr5---sn-4g5ednrl.googlevideo.com (digits
        // and triple-dash). Make sure we don't reject it.
        var bytes = ClientHelloBuilder.Build("rr5---sn-4g5ednrl.googlevideo.com");

        var ok = TlsClientHelloParser.TryExtractSni(bytes, out var sni);

        Assert.True(ok);
        Assert.Equal("rr5---sn-4g5ednrl.googlevideo.com", sni);
    }
}

/// <summary>
/// Builds synthetic TLS ClientHello byte sequences for unit tests. Layout
/// follows RFC 8446 (TLS 1.3) which is wire-compatible with TLS 1.2's
/// ClientHello at the framing level the parser cares about.
/// </summary>
internal static class ClientHelloBuilder {
    public static byte[] Build(
        string? sni,
        ushort recordVersion = 0x0303,
        ushort handshakeVersion = 0x0303,
        bool includeEch = false
    ) {
        // ClientHello body: legacy_version (2) + random (32) + session_id (1+0)
        // + cipher_suites (2 + 4 = TLS_AES_128_GCM_SHA256, TLS_AES_256_GCM_SHA384)
        // + compression_methods (1 + 1 = null) + extensions.
        var body = new List<byte>();
        body.Add((byte)(handshakeVersion >> 8));
        body.Add((byte)(handshakeVersion & 0xff));
        body.AddRange(Enumerable.Repeat<byte>(0, 32)); // random
        body.Add(0); // session_id length = 0
        body.Add(0); body.Add(4); // cipher_suites length = 4
        body.Add(0x13); body.Add(0x01); // TLS_AES_128_GCM_SHA256
        body.Add(0x13); body.Add(0x02); // TLS_AES_256_GCM_SHA384
        body.Add(1); // compression_methods length = 1
        body.Add(0); // null compression

        var extensions = new List<byte>();
        if (sni is not null) {
            // server_name extension (type 0x0000):
            //   ext_data = list_length (2) + entry { name_type (1) + hostname (2 + bytes) }
            var hostBytes = System.Text.Encoding.ASCII.GetBytes(sni);
            var entry = new List<byte> { 0x00 }; // host_name
            entry.Add((byte)(hostBytes.Length >> 8));
            entry.Add((byte)(hostBytes.Length & 0xff));
            entry.AddRange(hostBytes);
            var extData = new List<byte>();
            extData.Add((byte)(entry.Count >> 8));
            extData.Add((byte)(entry.Count & 0xff));
            extData.AddRange(entry);
            extensions.Add(0x00); extensions.Add(0x00); // type
            extensions.Add((byte)(extData.Count >> 8));
            extensions.Add((byte)(extData.Count & 0xff));
            extensions.AddRange(extData);
        }
        if (includeEch) {
            // encrypted_client_hello extension (type 0xfe0d) — empty data is
            // fine for the test; we only check presence.
            extensions.Add(0xfe); extensions.Add(0x0d);
            extensions.Add(0x00); extensions.Add(0x00);
        }

        body.Add((byte)(extensions.Count >> 8));
        body.Add((byte)(extensions.Count & 0xff));
        body.AddRange(extensions);

        // Handshake header: type (1 byte = 0x01 ClientHello) + length (3 bytes)
        var handshake = new List<byte> { 0x01 };
        handshake.Add((byte)((body.Count >> 16) & 0xff));
        handshake.Add((byte)((body.Count >> 8) & 0xff));
        handshake.Add((byte)(body.Count & 0xff));
        handshake.AddRange(body);

        // Record header: ContentType (0x16) + ProtocolVersion (2) + length (2)
        var record = new List<byte> { 0x16 };
        record.Add((byte)(recordVersion >> 8));
        record.Add((byte)(recordVersion & 0xff));
        record.Add((byte)(handshake.Count >> 8));
        record.Add((byte)(handshake.Count & 0xff));
        record.AddRange(handshake);

        return record.ToArray();
    }
}
