using Beholder.Core;

namespace Beholder.Tests;

public class ChainVerificationResultTests {
    [Fact]
    public void Success_ValidRowCount_ProducesValidResult() {
        var result = ChainVerificationResult.Success(100);

        Assert.True(result.IsValid);
        Assert.Equal(100, result.RowsVerified);
        Assert.Null(result.FailedAtSeq);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Failure_FailureDetails_ProducesInvalidResult() {
        var result = ChainVerificationResult.Failure(50, 42, "hash mismatch");

        Assert.False(result.IsValid);
        Assert.Equal(50, result.RowsVerified);
        Assert.Equal(42, result.FailedAtSeq);
        Assert.Equal("hash mismatch", result.ErrorMessage);
    }

    [Fact]
    public void Success_NegativeRowsVerified_ThrowsArgumentOutOfRangeException() {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChainVerificationResult.Success(-1));
    }

    [Fact]
    public void Failure_NegativeRowsVerified_ThrowsArgumentOutOfRangeException() {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChainVerificationResult.Failure(-1, 0, "x"));
    }

    [Fact]
    public void Failure_NullErrorMessage_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => ChainVerificationResult.Failure(0, 0, null!));
    }

    [Fact]
    public void Failure_WhitespaceErrorMessage_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(() => ChainVerificationResult.Failure(0, 0, "   "));
    }
}
