using System.Text;

namespace Beholder.Core.Tls;

/// <summary>
/// Extracts the Server Name Indication (SNI) extension from a TLS
/// ClientHello record. Designed for the SNI-capture path (ADR 006) but kept
/// in <c>Beholder.Core</c> with no Windows or platform dependencies — the
/// future Linux SNI source will reuse it unchanged.
/// </summary>
/// <remarks>
/// The parser is intentionally defensive. Every length field is bounds-checked
/// against the remaining buffer; any malformed, truncated, or unexpected
/// structure returns <c>false</c> with no allocation and no exception. This
/// matches the calling convention used by <see cref="IReverseDnsResolver"/>
/// and other "skip silently on failure" seams in the resolution ladder.
///
/// Scope:
/// <list type="bullet">
///   <item>TLS 1.2 and TLS 1.3 ClientHello — both use the same record + handshake
///     framing; TLS 1.3 sends <c>0x03 0x03</c> in the record header for
///     compatibility, so version detection is uniform.</item>
///   <item>SNI extension type 0 (<c>server_name</c>), name_type 0 (<c>host_name</c>),
///     first entry only — RFC 6066 allows a list but in practice modern stacks
///     send exactly one host_name entry.</item>
///   <item>Returns <c>false</c> for non-ClientHello records, ClientHellos
///     without an SNI extension, ECH-encrypted ClientHellos (extension type
///     <c>0xfe0d</c>), and any malformed input.</item>
/// </list>
/// </remarks>
public static class TlsClientHelloParser {
    private const byte ContentTypeHandshake = 0x16;
    private const byte HandshakeTypeClientHello = 0x01;
    private const ushort ExtensionTypeServerName = 0x0000;
    private const ushort ExtensionTypeEncryptedClientHello = 0xfe0d;
    private const byte ServerNameTypeHostName = 0x00;

    /// <summary>
    /// Attempts to extract the SNI hostname from a TLS record. Returns
    /// <c>true</c> with <paramref name="hostname"/> set if the buffer
    /// contains a well-formed ClientHello carrying a server_name extension
    /// with at least one host_name entry. Returns <c>false</c> in every
    /// other case, including malformed or truncated input — no exception
    /// is thrown.
    /// </summary>
    /// <param name="tlsRecord">
    /// Raw TLS record bytes starting at the record header
    /// (<c>0x16 0x03 0xXX 0xLL 0xLL ...</c>). May be a TCP segment payload
    /// or a fragment thereof; only the first record is examined.
    /// </param>
    /// <param name="hostname">
    /// On success, the ASCII-decoded hostname (e.g. <c>"www.example.com"</c>).
    /// On failure, <c>null</c>.
    /// </param>
    public static bool TryExtractSni(ReadOnlySpan<byte> tlsRecord, out string? hostname) {
        hostname = null;

        // TLS record header: 1-byte ContentType, 2-byte ProtocolVersion,
        // 2-byte Length. Total 5 bytes.
        if (tlsRecord.Length < 5) return false;
        if (tlsRecord[0] != ContentTypeHandshake) return false;
        if (tlsRecord[1] != 0x03) return false; // Major version
        // Minor version (tlsRecord[2]) ranges 0x01..0x04 for TLS 1.0..1.3;
        // we don't validate it because TLS 1.3 ClientHellos send 0x03 in
        // the record header for compatibility regardless of negotiated version.

        var recordLength = ReadU16(tlsRecord, 3);
        if (recordLength < 4) return false;
        // Truncated capture — the on-wire record is larger than what we have.
        // Don't try to parse partial data; the production source will see the
        // full first packet of the handshake.
        if (5 + recordLength > tlsRecord.Length) return false;

        var handshake = tlsRecord.Slice(5, recordLength);

        // Handshake header: 1-byte HandshakeType, 3-byte Length.
        if (handshake.Length < 4) return false;
        if (handshake[0] != HandshakeTypeClientHello) return false;
        var handshakeBodyLength = ReadU24(handshake, 1);
        if (4 + handshakeBodyLength > handshake.Length) return false;

        var body = handshake.Slice(4, handshakeBodyLength);
        return TryParseClientHelloBody(body, out hostname);
    }

    /// <summary>
    /// Parses the body of a ClientHello (everything after the 4-byte handshake
    /// header) and walks to the SNI extension. The body layout is:
    /// <c>legacy_version (2) + random (32) + session_id (var) + cipher_suites
    /// (var) + compression_methods (var) + extensions (var)</c>.
    /// </summary>
    private static bool TryParseClientHelloBody(ReadOnlySpan<byte> body, out string? hostname) {
        hostname = null;

        // legacy_version (2 bytes) + random (32 bytes)
        if (body.Length < 34) return false;
        var p = 34;

        // session_id: 1-byte length, then bytes (max 32 per RFC).
        if (p + 1 > body.Length) return false;
        var sessionIdLen = body[p];
        if (sessionIdLen > 32) return false;
        p += 1 + sessionIdLen;

        // cipher_suites: 2-byte length, then bytes.
        if (p + 2 > body.Length) return false;
        var cipherLen = ReadU16(body, p);
        // RFC: cipher_suites length is in bytes, must be even (each suite is
        // 2 bytes), at least 2, at most 2^16-2.
        if (cipherLen < 2 || (cipherLen & 1) != 0) return false;
        p += 2 + cipherLen;

        // compression_methods: 1-byte length, then bytes.
        if (p + 1 > body.Length) return false;
        var compLen = body[p];
        // RFC: at least 1 (MUST include null compression).
        if (compLen < 1) return false;
        p += 1 + compLen;

        // extensions: 2-byte total length, then list. Optional in TLS 1.2 but
        // mandatory in TLS 1.3, and SNI requires it either way.
        if (p + 2 > body.Length) return false;
        var extTotalLen = ReadU16(body, p);
        p += 2;
        if (p + extTotalLen > body.Length) return false;

        var extensions = body.Slice(p, extTotalLen);
        return TryFindAndParseSniExtension(extensions, out hostname);
    }

    /// <summary>
    /// Walks the extensions list looking for the server_name extension.
    /// If the encrypted_client_hello extension (0xfe0d) is present, returns
    /// <c>false</c> because the real SNI is encrypted and not recoverable
    /// from this ClientHello — the caller should fall back to reverse DNS or
    /// raw IP.
    /// </summary>
    private static bool TryFindAndParseSniExtension(ReadOnlySpan<byte> extensions, out string? hostname) {
        hostname = null;
        var p = 0;
        var sawEncryptedClientHello = false;
        var sniData = ReadOnlySpan<byte>.Empty;
        var sawSni = false;

        while (p + 4 <= extensions.Length) {
            var extType = ReadU16(extensions, p);
            var extLen = ReadU16(extensions, p + 2);
            p += 4;
            if (p + extLen > extensions.Length) return false;

            switch (extType) {
                case ExtensionTypeServerName:
                    sniData = extensions.Slice(p, extLen);
                    sawSni = true;
                    break;
                case ExtensionTypeEncryptedClientHello:
                    sawEncryptedClientHello = true;
                    break;
            }

            p += extLen;
        }

        // ECH wins over SNI even if both are present — the SNI in the outer
        // ClientHello is a decoy when ECH is in play.
        if (sawEncryptedClientHello) return false;
        if (!sawSni) return false;
        return TryParseServerNameExtension(sniData, out hostname);
    }

    /// <summary>
    /// Parses a server_name extension's data and returns the first host_name
    /// entry. Layout:
    /// <c>server_name_list_length (2) + entries[] where each entry is
    /// name_type (1) + hostname_length (2) + hostname_bytes (var)</c>.
    /// </summary>
    private static bool TryParseServerNameExtension(ReadOnlySpan<byte> data, out string? hostname) {
        hostname = null;
        if (data.Length < 5) return false;

        var listLen = ReadU16(data, 0);
        if (2 + listLen > data.Length) return false;
        var list = data.Slice(2, listLen);
        if (list.Length < 3) return false;

        // First entry only. Modern stacks send exactly one host_name entry;
        // RFC 6066 permits more but the IANA registry has only host_name (0)
        // as a valid name_type, so a list of more than one is unusual.
        if (list[0] != ServerNameTypeHostName) return false;
        var hostLen = ReadU16(list, 1);
        if (3 + hostLen > list.Length) return false;
        if (hostLen == 0) return false;

        var hostBytes = list.Slice(3, hostLen);

        // Validate that the hostname is printable ASCII before allocating the
        // string — defends against malformed extensions that pass the length
        // checks but contain control bytes or non-ASCII.
        if (!IsLikelyHostname(hostBytes)) return false;

        hostname = Encoding.ASCII.GetString(hostBytes);
        return true;
    }

    /// <summary>
    /// True if every byte is in the ASCII range allowed in DNS hostnames:
    /// letters, digits, hyphen, and dot (RFC 1123 + ASCII compat for IDNs in
    /// Punycode form, which use only [a-z0-9-] after xn-- prefix). Rejects
    /// any control byte or 8-bit value. Empty span returns false.
    /// </summary>
    private static bool IsLikelyHostname(ReadOnlySpan<byte> bytes) {
        if (bytes.Length == 0) return false;
        foreach (var b in bytes) {
            if (b == '.' || b == '-') continue;
            if (b >= '0' && b <= '9') continue;
            if (b >= 'A' && b <= 'Z') continue;
            if (b >= 'a' && b <= 'z') continue;
            return false;
        }
        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static int ReadU24(ReadOnlySpan<byte> data, int offset) =>
        (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
}
