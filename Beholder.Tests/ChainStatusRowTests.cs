using Beholder.Protocol.Local;
using Beholder.Ui.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public class ChainStatusRowTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromProto_NullProto_RendersNeverVerifiedPlaceholder() {
        var row = ChainStatusRow.FromProto(null, new FakeTimeProvider(FixedTimestamp));

        Assert.False(row.HasResult);
        Assert.Null(row.LastVerifiedAt);
        Assert.Equal(ChainStatusRow.NeverVerifiedLabel, row.LastVerifiedAtLabel);
        Assert.Empty(row.ResultSummary);
    }

    [Fact]
    public void FromProto_Success_RendersValidSummary() {
        var time = new FakeTimeProvider(FixedTimestamp);
        var verifiedAt = FixedTimestamp.AddSeconds(-30);
        var proto = new ChainStatus {
            LastVerifiedUnixNs = verifiedAt.ToUnixTimeMilliseconds() * 1_000_000L,
            IsValid = true,
            RowsVerified = 1247,
        };

        var row = ChainStatusRow.FromProto(proto, time);

        Assert.True(row.HasResult);
        Assert.True(row.IsValid);
        Assert.Equal(1247, row.RowsVerified);
        Assert.Equal("30s ago", row.LastVerifiedAtLabel);
        Assert.Contains("1,247", row.ResultSummary);
    }

    [Fact]
    public void FromProto_Failure_RendersFailedAtSeqInSummary() {
        var time = new FakeTimeProvider(FixedTimestamp);
        var proto = new ChainStatus {
            LastVerifiedUnixNs = FixedTimestamp.AddSeconds(-5).ToUnixTimeMilliseconds() * 1_000_000L,
            IsValid = false,
            RowsVerified = 99,
            FailedAtSeq = 42,
            ErrorMessage = "hash mismatch",
        };

        var row = ChainStatusRow.FromProto(proto, time);

        Assert.False(row.IsValid);
        Assert.Equal(42, row.FailedAtSeq);
        Assert.Equal("hash mismatch", row.ErrorMessage);
        Assert.Contains("seq 42", row.ResultSummary);
        Assert.Contains("hash mismatch", row.ResultSummary);
    }

    [Fact]
    public void RefreshRelativeLabel_TickerAdvancesLabel() {
        var time = new FakeTimeProvider(FixedTimestamp);
        var verifiedAt = FixedTimestamp.AddSeconds(-30);
        var row = ChainStatusRow.FromProto(new ChainStatus {
            LastVerifiedUnixNs = verifiedAt.ToUnixTimeMilliseconds() * 1_000_000L,
            IsValid = true,
            RowsVerified = 10,
        }, time);

        time.Advance(TimeSpan.FromMinutes(2));
        row.RefreshRelativeLabel(time);

        Assert.Equal("2m ago", row.LastVerifiedAtLabel);
    }

    [Fact]
    public void UpdateFromProto_NullAfterPopulated_ResetsToNeverVerified() {
        var time = new FakeTimeProvider(FixedTimestamp);
        var row = ChainStatusRow.FromProto(new ChainStatus {
            LastVerifiedUnixNs = FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            IsValid = true,
            RowsVerified = 1,
        }, time);

        row.UpdateFromProto(null, time);

        Assert.False(row.HasResult);
        Assert.Equal(ChainStatusRow.NeverVerifiedLabel, row.LastVerifiedAtLabel);
    }
}
