using Beholder.Core;

namespace Beholder.Tests;

public class ChainHasherTests {
    private const long Seq = 1L;
    private const long Timestamp = 1_700_000_000_000_000_000L;
    private const EventKind Kind = EventKind.Counter;

    private static byte[] BuildPayload() => [0x01, 0x02, 0x03];

    [Fact]
    public void ComputeRowHash_SameInputs_ProducesSameHash() {
        var payload = BuildPayload();

        var first = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);
        var second = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeRowHash_DifferentSeq_ProducesDifferentHash() {
        var payload = BuildPayload();

        var first = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);
        var second = ChainHasher.ComputeRowHash(Seq + 1, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeRowHash_DifferentTimestamp_ProducesDifferentHash() {
        var payload = BuildPayload();

        var first = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);
        var second = ChainHasher.ComputeRowHash(Seq, Timestamp + 1, Kind, payload, ChainHasher.ZeroPrevHash);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeRowHash_DifferentKind_ProducesDifferentHash() {
        var payload = BuildPayload();

        var first = ChainHasher.ComputeRowHash(Seq, Timestamp, EventKind.Counter, payload, ChainHasher.ZeroPrevHash);
        var second = ChainHasher.ComputeRowHash(Seq, Timestamp, EventKind.NewProcess, payload, ChainHasher.ZeroPrevHash);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeRowHash_DifferentPayload_ProducesDifferentHash() {
        var payloadA = BuildPayload();
        var payloadB = BuildPayload();
        payloadB[1] = 0xFF;

        var first = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payloadA, ChainHasher.ZeroPrevHash);
        var second = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payloadB, ChainHasher.ZeroPrevHash);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeRowHash_DifferentPrevHash_ProducesDifferentHash() {
        var payload = BuildPayload();
        var alteredPrev = (byte[])ChainHasher.ZeroPrevHash.Clone();
        alteredPrev[0] = 0x01;

        var first = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);
        var second = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, alteredPrev);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeRowHash_ReturnsExactly32Bytes() {
        var hash = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, BuildPayload(), ChainHasher.ZeroPrevHash);

        Assert.Equal(ChainHasher.HashSize, hash.Length);
    }

    [Fact]
    public void ComputeRowHash_InvalidPrevHashLength_ThrowsArgumentException() {
        var shortPrev = new byte[31];

        Assert.Throws<ArgumentException>(() =>
            ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, BuildPayload(), shortPrev));
    }

    [Fact]
    public void Verify_ValidHash_ReturnsTrue() {
        var payload = BuildPayload();
        var hash = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);

        var result = ChainHasher.Verify(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash, hash);

        Assert.True(result);
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsFalse() {
        var original = BuildPayload();
        var hash = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, original, ChainHasher.ZeroPrevHash);
        var tampered = BuildPayload();
        tampered[0] = 0xFF;

        var result = ChainHasher.Verify(Seq, Timestamp, Kind, tampered, ChainHasher.ZeroPrevHash, hash);

        Assert.False(result);
    }

    [Fact]
    public void Verify_TamperedHash_ReturnsFalse() {
        var payload = BuildPayload();
        var hash = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);
        hash[0] ^= 0x01;

        var result = ChainHasher.Verify(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash, hash);

        Assert.False(result);
    }

    [Fact]
    public void Verify_InvalidExpectedHashLength_ThrowsArgumentException() {
        var shortHash = new byte[31];

        Assert.Throws<ArgumentException>(() =>
            ChainHasher.Verify(Seq, Timestamp, Kind, BuildPayload(), ChainHasher.ZeroPrevHash, shortHash));
    }

    [Fact]
    public void ComputeRowHash_WithZeroPrevHash_Succeeds() {
        var hash = ChainHasher.ComputeRowHash(0, Timestamp, Kind, BuildPayload(), ChainHasher.ZeroPrevHash);

        Assert.Equal(ChainHasher.HashSize, hash.Length);
    }

    [Fact]
    public void ChainLink_ThreeRows_VerifiesEndToEnd() {
        var payload0 = new byte[] { 0xAA };
        var payload1 = new byte[] { 0xBB };
        var payload2 = new byte[] { 0xCC };

        var hash0 = ChainHasher.ComputeRowHash(0, 100, EventKind.Counter, payload0, ChainHasher.ZeroPrevHash);
        var hash1 = ChainHasher.ComputeRowHash(1, 200, EventKind.Counter, payload1, hash0);
        var hash2 = ChainHasher.ComputeRowHash(2, 300, EventKind.Counter, payload2, hash1);

        Assert.True(ChainHasher.Verify(0, 100, EventKind.Counter, payload0, ChainHasher.ZeroPrevHash, hash0));
        Assert.True(ChainHasher.Verify(1, 200, EventKind.Counter, payload1, hash0, hash1));
        Assert.True(ChainHasher.Verify(2, 300, EventKind.Counter, payload2, hash1, hash2));

        var tamperedPayload1 = new byte[] { 0xDD };
        Assert.False(ChainHasher.Verify(1, 200, EventKind.Counter, tamperedPayload1, hash0, hash1));
        Assert.True(ChainHasher.Verify(0, 100, EventKind.Counter, payload0, ChainHasher.ZeroPrevHash, hash0));
    }

    [Fact]
    public void ComputeRowHash_LargePayload_UsesArrayPoolPath() {
        var payload = new byte[2048];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xff);

        var hash = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);

        Assert.Equal(ChainHasher.HashSize, hash.Length);
        Assert.True(ChainHasher.Verify(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash, hash));
    }

    [Fact]
    public void ComputeRowHash_BoundaryPayload_AtExactStackallocThreshold() {
        // HeaderSize (20) + payload (972) + HashSize (32) = 1024, the inclusive threshold.
        var payload = new byte[972];

        var hash = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash);

        Assert.Equal(ChainHasher.HashSize, hash.Length);
        Assert.True(ChainHasher.Verify(Seq, Timestamp, Kind, payload, ChainHasher.ZeroPrevHash, hash));
    }

    [Fact]
    public void ComputeRowHash_EmptyPayload_Succeeds() {
        var hash = ChainHasher.ComputeRowHash(Seq, Timestamp, Kind, ReadOnlySpan<byte>.Empty, ChainHasher.ZeroPrevHash);

        Assert.Equal(ChainHasher.HashSize, hash.Length);
    }

    [Fact]
    public void Verify_InvalidPrevHashLength_ThrowsArgumentException() {
        var shortPrev = new byte[31];
        var anyHash = new byte[ChainHasher.HashSize];

        Assert.Throws<ArgumentException>(() =>
            ChainHasher.Verify(Seq, Timestamp, Kind, BuildPayload(), shortPrev, anyHash));
    }

    [Fact]
    public void ChainLink_TamperingRow1_BreaksDownstreamPrevHash() {
        var payload0 = new byte[] { 0xAA };
        var payload1 = new byte[] { 0xBB };

        var hash0 = ChainHasher.ComputeRowHash(0, 100, EventKind.Counter, payload0, ChainHasher.ZeroPrevHash);
        var hash1 = ChainHasher.ComputeRowHash(1, 200, EventKind.Counter, payload1, hash0);

        var tampered1 = new byte[] { 0xDD };
        var recomputed = ChainHasher.ComputeRowHash(1, 200, EventKind.Counter, tampered1, hash0);

        Assert.NotEqual(hash1, recomputed);
    }
}
