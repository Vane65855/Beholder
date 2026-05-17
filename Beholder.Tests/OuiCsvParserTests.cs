using Beholder.Daemon.Scanner;

namespace Beholder.Tests;

public sealed class OuiCsvParserTests {
    [Fact]
    public void Parse_EmptyReader_ReturnsEmptyDictionary() {
        var result = OuiCsvParser.Parse(new StringReader(string.Empty));

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_HeaderOnly_ReturnsEmptyDictionary() {
        const string csv = "Registry,Assignment,Organization Name,Organization Address\n";

        var result = OuiCsvParser.Parse(new StringReader(csv));

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_MaLRows_ExtractedAndUppercased() {
        const string csv = """
            Registry,Assignment,Organization Name,Organization Address
            MA-L,aabbcc,AcmeCorp,addr
            MA-L,001122,WidgetWorks,addr
            """;

        var result = OuiCsvParser.Parse(new StringReader(csv));

        Assert.Equal(2, result.Count);
        Assert.Equal("AcmeCorp", result["AABBCC"]);
        Assert.Equal("WidgetWorks", result["001122"]);
    }

    [Fact]
    public void Parse_NonMaLRows_SkippedSilently() {
        const string csv = """
            Registry,Assignment,Organization Name,Organization Address
            MA-L,AABBCC,AcmeCorp,addr
            MA-M,FFEE00,SubAssignment Vendor,addr
            MA-S,DDCCBB,SmallerAssign Vendor,addr
            IAB,001122,Old IAB Vendor,addr
            """;

        var result = OuiCsvParser.Parse(new StringReader(csv));

        Assert.Single(result);
        Assert.True(result.ContainsKey("AABBCC"));
    }

    [Fact]
    public void Parse_MalformedRow_SkippedSilently() {
        // Error-path test per PRINCIPLES.md "every error path must be tested" —
        // the parser must tolerate short rows / odd column counts / empty
        // vendor names without aborting the load. Hex-char validation is the
        // lookup's responsibility (see OuiVendorLookupTests), so the parser
        // happily stores a length-6 non-hex prefix that no real MAC would
        // ever match.
        const string csv = """
            Registry,Assignment,Organization Name,Organization Address
            MA-L,AABBCC,AcmeCorp,addr
            MA-L
            ,,,
            MA-L,SHORT,Short prefix vendor,addr
            MA-L,TOOLONG,Too long prefix vendor,addr
            MA-L,AABBCD,,empty vendor name
            MA-L,001122,WidgetWorks,addr
            """;

        var result = OuiCsvParser.Parse(new StringReader(csv));

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("AABBCC"));
        Assert.True(result.ContainsKey("001122"));
        Assert.False(result.ContainsKey("SHORT"));     // length 5, rejected
        Assert.False(result.ContainsKey("TOOLONG"));   // length 7, rejected
        Assert.False(result.ContainsKey("AABBCD"));    // empty vendor, rejected
    }

    [Fact]
    public void Parse_QuotedFieldsWithEmbeddedComma_PreservesFullContent() {
        const string csv = """
            Registry,Assignment,Organization Name,Organization Address
            MA-L,AABBCC,"Comma, Inc.","Some, Address"
            """;

        var result = OuiCsvParser.Parse(new StringReader(csv));

        Assert.Single(result);
        Assert.Equal("Comma, Inc.", result["AABBCC"]);
    }

    [Fact]
    public void Parse_NullReader_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => OuiCsvParser.Parse(null!));
    }
}
