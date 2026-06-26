using System.Text.Json;
using Beholder.Core;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;

namespace Beholder.Tests;

/// <summary>
/// Exercises the Phase 11.3 signed chain exporter. Uses a real NSec Ed25519
/// keypair via <see cref="FakeCheckpointKeyProvider"/> so signature round-trips
/// exercise genuine crypto, not a stub.
/// </summary>
public sealed class ChainExporterTests : IDisposable {
    private static readonly DateTimeOffset ExportedAt = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeCheckpointKeyProvider _keyProvider = new();
    private readonly ChainExporter _exporter;

    public ChainExporterTests() {
        _exporter = new ChainExporter(_keyProvider);
    }

    public void Dispose() => _keyProvider.Dispose();

    [Fact]
    public void Constructor_NullKeyProvider_Throws() {
        Assert.Throws<ArgumentNullException>(() => new ChainExporter(null!));
    }

    [Fact]
    public void Export_ProducesEnvelopeWithExpectedTopLevelShape() {
        var bytes = _exporter.Export(MakeRows(3), fromSeq: 0, toSeq: 0, ExportedAt, "1.2.3");

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.Equal("Ed25519", root.GetProperty("signature_alg").GetString());
        Assert.Equal(_keyProvider.KeyId, root.GetProperty("key_id").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("public_key_b64").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("signature_b64").GetString()));
        var body = root.GetProperty("body");
        Assert.Equal(1, body.GetProperty("format_version").GetInt32());
        Assert.Equal("1.2.3", body.GetProperty("daemon_version").GetString());
        Assert.Equal(3, body.GetProperty("event_count").GetInt32());
        Assert.Equal(3, body.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void Export_EventCarriesPayloadAndHashChain() {
        var bytes = _exporter.Export(MakeRows(1), fromSeq: 0, toSeq: 0, ExportedAt, "1.0.0");

        using var doc = JsonDocument.Parse(bytes);
        var ev = doc.RootElement.GetProperty("body").GetProperty("events")[0];
        Assert.Equal(0, ev.GetProperty("seq").GetInt64());
        Assert.Equal("Counter", ev.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(ev.GetProperty("payload_b64").GetString()));
        Assert.False(string.IsNullOrEmpty(ev.GetProperty("prev_hash_b64").GetString()));
        Assert.False(string.IsNullOrEmpty(ev.GetProperty("row_hash_b64").GetString()));
    }

    [Fact]
    public void Export_EmptyChain_ProducesValidEnvelopeWithZeroEvents() {
        var bytes = _exporter.Export(Array.Empty<EventLogRow>(), fromSeq: 0, toSeq: 0, ExportedAt, "1.0.0");

        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal(0, doc.RootElement.GetProperty("body").GetProperty("event_count").GetInt32());
        Assert.True(ChainExporter.TryVerify(bytes));
    }

    [Fact]
    public void Export_RecordsRequestedRangeInMetadata() {
        var bytes = _exporter.Export(MakeRows(2), fromSeq: 5, toSeq: 9, ExportedAt, "1.0.0");

        using var doc = JsonDocument.Parse(bytes);
        var body = doc.RootElement.GetProperty("body");
        Assert.Equal(5, body.GetProperty("from_seq").GetInt64());
        Assert.Equal(9, body.GetProperty("to_seq").GetInt64());
    }

    [Fact]
    public void TryVerify_FreshlySignedExport_ReturnsTrue() {
        var bytes = _exporter.Export(MakeRows(5), fromSeq: 0, toSeq: 0, ExportedAt, "1.0.0");

        Assert.True(ChainExporter.TryVerify(bytes));
    }

    [Fact]
    public void TryVerify_TamperedEventPayload_ReturnsFalse() {
        var bytes = _exporter.Export(MakeRows(3), fromSeq: 0, toSeq: 0, ExportedAt, "1.0.0");
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        // Flip one base64 char inside the first event's payload region. Any
        // change to the body invalidates the signature over its digest.
        var tampered = json.Replace("\"payload_b64\":\"AA==\"", "\"payload_b64\":\"AQ==\"");
        Assert.NotEqual(json, tampered);   // guard: the replace actually hit

        Assert.False(ChainExporter.TryVerify(System.Text.Encoding.UTF8.GetBytes(tampered)));
    }

    [Fact]
    public void TryVerify_TamperedSignature_ReturnsFalse() {
        var bytes = _exporter.Export(MakeRows(2), fromSeq: 0, toSeq: 0, ExportedAt, "1.0.0");
        using var doc = JsonDocument.Parse(bytes);
        var goodSig = doc.RootElement.GetProperty("signature_b64").GetString()!;
        // Corrupt the signature's first byte while keeping it valid base64.
        var sigBytes = Convert.FromBase64String(goodSig);
        sigBytes[0] ^= 0xFF;
        var json = System.Text.Encoding.UTF8.GetString(bytes)
            .Replace(goodSig, Convert.ToBase64String(sigBytes));

        Assert.False(ChainExporter.TryVerify(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void TryVerify_MalformedJson_ReturnsFalse() {
        Assert.False(ChainExporter.TryVerify("not json"u8));
    }

    private static IReadOnlyList<EventLogRow> MakeRows(int count) {
        var rows = new List<EventLogRow>(count);
        for (var i = 0; i < count; i++) {
            var prev = new byte[ChainHasher.HashSize];
            var row = new byte[ChainHasher.HashSize];
            BitConverter.GetBytes((long)i).CopyTo(row, 0);
            BitConverter.GetBytes((long)(i - 1)).CopyTo(prev, 0);
            // First event gets a single 0x00 payload so the tamper test has a
            // known "AA==" base64 token to flip.
            var payload = i == 0 ? new byte[] { 0x00 } : new byte[] { (byte)i };
            rows.Add(new EventLogRow(
                i, ExportedAt.AddSeconds(i), EventKind.Counter, payload, prev, row));
        }
        return rows;
    }
}
