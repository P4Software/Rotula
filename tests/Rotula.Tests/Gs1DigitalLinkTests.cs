using Rotula.Core.Gs1;
using Rotula.Core.Validation;

namespace Rotula.Tests;

/// <summary>GS1 Digital Link URIs: key and qualifiers in the path, attributes in the query, percent-encoding, and reading them back.</summary>
public class Gs1DigitalLinkTests
{
    private static Gs1Message Msg(string bracketed) => Gs1Message.ParseBracketed(bracketed, out _)!;

    private static string Build(string bracketed, string prefix = Gs1DigitalLink.DefaultPrefix)
    {
        var uri = Gs1DigitalLink.Build(Msg(bracketed), out var issues, prefix);
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
        return uri!;
    }

    [Theory]
    // key only
    [InlineData("(01)09506000134352", "https://id.gs1.org/01/09506000134352")]
    // key, qualifier, attribute: the value of AI 17 goes to the query string
    [InlineData("(01)09506000134352(10)ABC123(17)251231", "https://id.gs1.org/01/09506000134352/10/ABC123?17=251231")]
    // qualifiers keep the order of the dictionary (22, 10, 21) whatever order the element string has
    [InlineData("(01)09506000134352(21)SER9(10)LOT7", "https://id.gs1.org/01/09506000134352/10/LOT7/21/SER9")]
    // a slash in a value is encoded
    [InlineData("(01)09506000134352(10)ABC/1", "https://id.gs1.org/01/09506000134352/10/ABC%2F1")]
    // SSCC is a key without qualifiers
    [InlineData("(00)376104250021234569", "https://id.gs1.org/00/376104250021234569")]
    // two attributes
    [InlineData("(01)09506000134352(15)260630(3103)001234", "https://id.gs1.org/01/09506000134352?15=260630&3103=001234")]
    public void Build_WritesPathAndQuery(string bracketed, string expected) => Assert.Equal(expected, Build(bracketed));

    [Fact]
    public void Build_UsesTheGivenPrefix_WithoutADoubleSlash()
    {
        Assert.Equal("https://example.com/p/01/09506000134352", Build("(01)09506000134352", "https://example.com/p/"));
    }

    [Fact]
    public void Build_EncodesReservedCharacters_AndReadsThemBack()
    {
        // Character set 82 holds characters (% & = ? +) that cannot stand in a URI path or query as they are.
        var message = Msg("(01)09506000134352(10)A%25B&C=D?E+G");
        var uri = Gs1DigitalLink.Build(message, out var issues);
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
        Assert.Equal("https://id.gs1.org/01/09506000134352/10/A%2525B%26C%3DD%3FE%2BG", uri);
        var back = Gs1DigitalLink.Parse(uri!, out var parseIssues)!;
        Assert.DoesNotContain(parseIssues, i => i.Severity == ValidationSeverity.Error);
        Assert.Equal("A%25B&C=D?E+G", back.Message.ValueOf("10"));
    }

    [Fact]
    public void Build_RefusesWhatTheStandardDoesNotAllow()
    {
        // no primary key
        Assert.Null(Gs1DigitalLink.Build(Msg("(10)ABC"), out var noKey));
        Assert.Contains(noKey, i => i.MessageKey == "Gs1.DigitalLink.NoKey");
        // two primary keys
        Assert.Null(Gs1DigitalLink.Build(Msg("(01)09506000134352(00)376104250021234569"), out var two));
        Assert.Contains(two, i => i.MessageKey == "Gs1.DigitalLink.ManyKeys");
        // 235 and 10 are qualifiers from different allowed sequences and cannot be mixed
        Assert.Null(Gs1DigitalLink.Build(Msg("(01)09506000134352(10)A(235)B"), out var mixed));
        Assert.Contains(mixed, i => i.MessageKey == "Gs1.DigitalLink.QualifierCombination");
        // an identifier that is not allowed as a Digital Link data attribute (8200 has no "?" flag in the dictionary)
        Assert.Null(Gs1DigitalLink.Build(Msg("(01)09506000134352(8200)https://x.test"), out var notAllowed));
        Assert.Contains(notAllowed, i => i.MessageKey == "Gs1.DigitalLink.NotAllowed");
        // invalid data is reported, too: the check digit of the GTIN
        Assert.Null(Gs1DigitalLink.Build(Msg("(01)09506000134353"), out var invalid));
        Assert.Contains(invalid, i => i.MessageKey == "Gs1.Lint.CheckDigit");
    }

    [Theory]
    [InlineData("(01)09506000134352(10)ABC123(17)251231")]
    [InlineData("(01)09506000134352(10)LOT7(21)SER9(15)260630")]
    [InlineData("(00)376104250021234569")]
    [InlineData("(414)9521234500018")]
    [InlineData("(01)09506000134352(10)A/B%C+D")]
    public void BuildThenParse_ReturnsTheSameElements(string bracketed)
    {
        var message = Msg(bracketed);
        var uri = Gs1DigitalLink.Build(message, out _)!;
        var parsed = Gs1DigitalLink.Parse(uri, out var issues)!;
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
        Assert.Equal("https://id.gs1.org", parsed.Prefix);
        Assert.Equal(message.Elements.OrderBy(e => e.Ai, StringComparer.Ordinal), parsed.Message.Elements.OrderBy(e => e.Ai, StringComparer.Ordinal));
        Assert.Empty(parsed.OtherParameters);
    }

    [Fact]
    public void Parse_AcceptsShortGtinsOtherParametersAndAPathPrefix()
    {
        var parsed = Gs1DigitalLink.Parse("https://brand.example/shop/01/9506000134352/10/L1?utm_source=label&17=251231", out var issues)!;
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
        Assert.Equal("https://brand.example/shop", parsed.Prefix);
        // The GTIN-13 is padded to the 14 digits of AI 01 (Digital Link standard).
        Assert.Equal("09506000134352", parsed.Message.ValueOf("01"));
        Assert.Equal("L1", parsed.Message.ValueOf("10"));
        Assert.Equal("251231", parsed.Message.ValueOf("17"));
        Assert.Equal(new[] { new KeyValuePair<string, string>("utm_source", "label") }, parsed.OtherParameters);
    }

    [Theory]
    [InlineData("not a uri", "Gs1.DigitalLink.Uri")]
    [InlineData("ftp://id.gs1.org/01/09506000134352", "Gs1.DigitalLink.Uri")]
    [InlineData("https://id.gs1.org/products/shoes", "Gs1.DigitalLink.NoKey")]
    [InlineData("https://id.gs1.org/01/09506000134352/10", "Gs1.DigitalLink.OddSegments")]
    [InlineData("https://id.gs1.org/01/09506000134352/21/S/10/L", "Gs1.DigitalLink.QualifierOrder")]
    [InlineData("https://id.gs1.org/00/376104250021234569?11=250101", "Gs1.Message.Requires")]   // a warning: AI 11 needs a GTIN
    [InlineData("https://id.gs1.org/01/09506000134353", "Gs1.Lint.CheckDigit")]
    [InlineData("https://id.gs1.org/01/09506000134352?8200=x", "Gs1.DigitalLink.NotAllowed")]
    public void Parse_ReportsProblems(string uri, string key)
    {
        var parsed = Gs1DigitalLink.Parse(uri, out var issues);
        Assert.Contains(issues, i => i.MessageKey == key);
        if (key is "Gs1.DigitalLink.Uri" or "Gs1.DigitalLink.NoKey" or "Gs1.DigitalLink.OddSegments") Assert.Null(parsed);
    }
}
