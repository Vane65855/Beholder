using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beholder.Core;
using NSec.Cryptography;
using NsecPublicKey = NSec.Cryptography.PublicKey;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Produces a signed, self-verifying JSON export of the chain-hashed event log
/// (Phase 11.3). See <see cref="IChainExporter"/> for the contract and ADR 012
/// for the full envelope schema + verification rules.
/// </summary>
/// <remarks>
/// <para>
/// The envelope is <c>{ body, signature_alg, key_id, public_key_b64,
/// signature_b64 }</c>. The signature is
/// <c>Ed25519.Sign(SHA-256(canonicalUtf8(body)))</c> — a detached signature
/// over a digest of the canonical body, mirroring how
/// <see cref="CheckpointSignaturePayload"/> signs a fixed-layout digest rather
/// than raw variable bytes.
/// </para>
/// <para>
/// "Canonical" is pinned by <see cref="CanonicalOptions"/> (no indentation,
/// fixed property order via <c>[JsonPropertyOrder]</c> on every envelope
/// record). Verification re-serialises the body with the same options and
/// re-checks the signature, so the round-trip is symmetric by construction —
/// the design avoids the serializer-ordering fragility that signing raw JSON
/// text would introduce.
/// </para>
/// </remarks>
internal sealed class ChainExporter : IChainExporter {
    /// <summary>Bumped only on a breaking change to the envelope schema.</summary>
    private const int FormatVersion = 1;
    private const string SignatureAlgorithm = "Ed25519";

    /// <summary>
    /// The single source of truth for the canonical serialisation. Both the
    /// signer (here) and any verifier (<see cref="TryVerify"/>, third-party
    /// tools per ADR 012) must use these exact options or the signature won't
    /// reproduce.
    /// </summary>
    private static readonly JsonSerializerOptions CanonicalOptions = new() {
        WriteIndented = false,
        // Property order comes from [JsonPropertyOrder]; never sort by name.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ICheckpointKeyProvider _keyProvider;

    public ChainExporter(ICheckpointKeyProvider keyProvider) {
        ArgumentNullException.ThrowIfNull(keyProvider);
        _keyProvider = keyProvider;
    }

    public byte[] Export(
        IReadOnlyList<EventLogRow> rows,
        long fromSeq,
        long toSeq,
        DateTimeOffset exportedAt,
        string daemonVersion
    ) {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(daemonVersion);

        var body = BuildBody(rows, fromSeq, toSeq, exportedAt, daemonVersion);
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, CanonicalOptions);
        var digest = SHA256.HashData(bodyBytes);
        var signature = _keyProvider.Sign(digest);

        var envelope = new ChainExportEnvelope {
            Body = body,
            SignatureAlgorithm = SignatureAlgorithm,
            KeyId = _keyProvider.KeyId,
            PublicKeyBase64 = Convert.ToBase64String(_keyProvider.PublicKey.Span),
            SignatureBase64 = Convert.ToBase64String(signature),
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, CanonicalOptions);
    }

    /// <summary>
    /// Verifies an export envelope's Ed25519 signature against the public key
    /// embedded in the envelope. Re-serialises the body with the canonical
    /// options, re-digests, and checks. Returns false on any malformation
    /// (bad JSON, missing fields, wrong-length key/signature, mismatch).
    /// Exposed for tests and as the reference verification routine ADR 012
    /// documents for third parties.
    /// </summary>
    public static bool TryVerify(ReadOnlySpan<byte> envelopeBytes) {
        try {
            var envelope = JsonSerializer.Deserialize<ChainExportEnvelope>(envelopeBytes, CanonicalOptions);
            if (envelope?.Body is null) return false;
            if (!string.Equals(envelope.SignatureAlgorithm, SignatureAlgorithm, StringComparison.Ordinal)) {
                return false;
            }

            var publicKeyBytes = Convert.FromBase64String(envelope.PublicKeyBase64);
            var signature = Convert.FromBase64String(envelope.SignatureBase64);
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Body, CanonicalOptions);
            var digest = SHA256.HashData(bodyBytes);
            var publicKey = NsecPublicKey.Import(
                NSec.Cryptography.SignatureAlgorithm.Ed25519, publicKeyBytes, KeyBlobFormat.RawPublicKey);
            return NSec.Cryptography.SignatureAlgorithm.Ed25519.Verify(publicKey, digest, signature);
        } catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException) {
            return false;
        }
    }

    private static ChainExportBody BuildBody(
        IReadOnlyList<EventLogRow> rows, long fromSeq, long toSeq,
        DateTimeOffset exportedAt, string daemonVersion
    ) {
        var events = new List<ChainExportEvent>(rows.Count);
        foreach (var row in rows) {
            events.Add(new ChainExportEvent {
                Seq = row.Seq,
                TimestampUnixNs = row.Timestamp.ToUnixTimeMilliseconds() * 1_000_000L,
                Kind = row.Kind.ToString(),
                KindOrdinal = (int)row.Kind,
                PayloadBase64 = Convert.ToBase64String(row.Payload),
                PrevHashBase64 = Convert.ToBase64String(row.PrevHash),
                RowHashBase64 = Convert.ToBase64String(row.RowHash),
            });
        }
        return new ChainExportBody {
            FormatVersion = FormatVersion,
            DaemonVersion = daemonVersion,
            ExportedAtUnixNs = exportedAt.ToUnixTimeMilliseconds() * 1_000_000L,
            FromSeq = fromSeq,
            ToSeq = toSeq,
            EventCount = events.Count,
            Events = events,
        };
    }

    internal sealed class ChainExportEnvelope {
        [JsonPropertyName("body")]
        [JsonPropertyOrder(0)]
        public ChainExportBody? Body { get; init; }

        [JsonPropertyName("signature_alg")]
        [JsonPropertyOrder(1)]
        public string SignatureAlgorithm { get; init; } = "";

        [JsonPropertyName("key_id")]
        [JsonPropertyOrder(2)]
        public string KeyId { get; init; } = "";

        [JsonPropertyName("public_key_b64")]
        [JsonPropertyOrder(3)]
        public string PublicKeyBase64 { get; init; } = "";

        [JsonPropertyName("signature_b64")]
        [JsonPropertyOrder(4)]
        public string SignatureBase64 { get; init; } = "";
    }

    internal sealed class ChainExportBody {
        [JsonPropertyName("format_version")]
        [JsonPropertyOrder(0)]
        public int FormatVersion { get; init; }

        [JsonPropertyName("daemon_version")]
        [JsonPropertyOrder(1)]
        public string DaemonVersion { get; init; } = "";

        [JsonPropertyName("exported_at_unix_ns")]
        [JsonPropertyOrder(2)]
        public long ExportedAtUnixNs { get; init; }

        [JsonPropertyName("from_seq")]
        [JsonPropertyOrder(3)]
        public long FromSeq { get; init; }

        [JsonPropertyName("to_seq")]
        [JsonPropertyOrder(4)]
        public long ToSeq { get; init; }

        [JsonPropertyName("event_count")]
        [JsonPropertyOrder(5)]
        public int EventCount { get; init; }

        [JsonPropertyName("events")]
        [JsonPropertyOrder(6)]
        public IReadOnlyList<ChainExportEvent> Events { get; init; } = [];
    }

    internal sealed class ChainExportEvent {
        [JsonPropertyName("seq")]
        [JsonPropertyOrder(0)]
        public long Seq { get; init; }

        [JsonPropertyName("ts_unix_ns")]
        [JsonPropertyOrder(1)]
        public long TimestampUnixNs { get; init; }

        [JsonPropertyName("kind")]
        [JsonPropertyOrder(2)]
        public string Kind { get; init; } = "";

        // The row hash covers the kind's integer ordinal (4 bytes big-endian),
        // not the name above. Exported explicitly so a third party can recompute
        // row_hash from the envelope alone without Beholder's EventKind mapping.
        [JsonPropertyName("kind_ordinal")]
        [JsonPropertyOrder(3)]
        public int KindOrdinal { get; init; }

        [JsonPropertyName("payload_b64")]
        [JsonPropertyOrder(4)]
        public string PayloadBase64 { get; init; } = "";

        [JsonPropertyName("prev_hash_b64")]
        [JsonPropertyOrder(5)]
        public string PrevHashBase64 { get; init; } = "";

        [JsonPropertyName("row_hash_b64")]
        [JsonPropertyOrder(6)]
        public string RowHashBase64 { get; init; } = "";
    }
}
