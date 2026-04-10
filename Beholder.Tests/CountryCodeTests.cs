using Beholder.Core;

namespace Beholder.Tests;

public class CountryCodeTests {
    [Fact]
    public void FromAlpha2_WithUpperCaseUs_ReturnsUsAndIsReal() {
        var code = CountryCode.FromAlpha2("US");

        Assert.Equal("US", code.Value);
        Assert.True(code.IsReal);
    }

    [Fact]
    public void FromAlpha2_WithLowerCaseUs_ReturnsUppercaseValue() {
        var code = CountryCode.FromAlpha2("us");

        Assert.Equal("US", code.Value);
    }

    [Fact]
    public void FromAlpha2_WithNull_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => CountryCode.FromAlpha2(null!));
    }

    [Fact]
    public void FromAlpha2_WithEmptyString_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(() => CountryCode.FromAlpha2(""));
    }

    [Fact]
    public void FromAlpha2_WithThreeCharacters_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(() => CountryCode.FromAlpha2("USA"));
    }

    [Fact]
    public void FromAlpha2_WithDigit_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(() => CountryCode.FromAlpha2("U1"));
    }

    [Fact]
    public void Local_HasDashDashValueAndIsNotReal() {
        Assert.Equal("--", CountryCode.Local.Value);
        Assert.False(CountryCode.Local.IsReal);
    }

    [Fact]
    public void Unknown_HasQuestionMarksValueAndIsNotReal() {
        Assert.Equal("??", CountryCode.Unknown.Value);
        Assert.False(CountryCode.Unknown.IsReal);
    }

    [Fact]
    public void Equality_SameValue_AreEqual() {
        var first = CountryCode.FromAlpha2("DE");
        var second = CountryCode.FromAlpha2("DE");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ToString_ReturnsValue() {
        var code = CountryCode.FromAlpha2("FR");

        Assert.Equal("FR", code.ToString());
    }

    [Fact]
    public void Default_Value_IsNullAndNotReal() {
        var code = default(CountryCode);

        Assert.Null(code.Value);
        Assert.False(code.IsReal);
        Assert.Equal("", code.ToString());
    }
}
