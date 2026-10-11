using Rotula.Core.Gs1;
using Rotula.Core.Validation;

namespace Rotula.Tests;

/// <summary>
/// GS1 element strings: reading and writing both forms, the FNC1 rule, validation, and the 48-character GS1-128 limit.
/// Spec citations are to the GS1 General Specifications release 26.0 (Jan 2026).
/// </summary>
public class Gs1MessageTests
{
    private const char GS = Gs1Message.GroupSeparator;

    // The year 2026 makes the century rule deterministic whatever day the tests run.
    private static readonly Gs1LintOptions Fixed = new(26);

    private static List<string> Keys(string bracketed)
    {
        var m = Gs1Message.ParseBracketed(bracketed, out var parse);
        Assert.NotNull(m);
        Assert.Empty(parse);
        return Gs1Validator.Validate(m!, Fixed).Select(i => i.MessageKey).ToList();
    }

    // ---- valid element strings quoted from the specification ---------------------------------------

    [Theory]
    // 1.4.2 GS1 element string: GTIN + batch/lot + date
    [InlineData("(01)09521234500001(10)ABC123(11)250504")]
    // 1.4.2: GLN of a physical location + GLN extension component
    [InlineData("(414)9521234500018(254)543LF")]
    // 3.3.1 / Table 7-9: SSCC 376104250021234569
    [InlineData("(00)376104250021234569")]
    // 5.5.2.3.1 GS1 DataBar Expanded sample: GTIN + net weight 1.50 kg... (3202 is pounds with two decimals)
    [InlineData("(01)90614141000015(3202)000150")]
    // 5.11.2 composite symbol structure samples
    [InlineData("(01)13112345678906(17)010615(10)A123456")]
    [InlineData("(01)03812345678908(10)ABCD123456(410)3898765432108")]
    // 5.11.8 sample composite symbols
    [InlineData("(01)93712345678904(3103)001234")]
    [InlineData("(01)03612345678904(11)990102")]
    // 7.8.6.1: GTIN 95012345678903 with net weight 4.00 kg
    [InlineData("(01)95012345678903(3102)000400")]
    // Dates: day 00 means "no day" for the 0-flavoured date tests (AI 17, expiry)
    [InlineData("(01)09521234500001(17)251200")]
    // Leap day
    [InlineData("(01)09521234500001(11)240229")]
    public void SpecificationExamples_AreValid(string bracketed) => Assert.DoesNotContain(Keys(bracketed), k => k != "Gs1.Message.Requires");

    [Theory]
    [InlineData("(01)09521234500002", "Gs1.Lint.CheckDigit")]                // wrong check digit
    [InlineData("(00)376104250021234560", "Gs1.Lint.CheckDigit")]            // SSCC with a wrong check digit
    [InlineData("(01)09521234500001(17)251301", "Gs1.Lint.DateMonth")]       // month 13
    [InlineData("(01)09521234500001(17)250231", "Gs1.Lint.DateDay")]         // 31 February
    [InlineData("(01)09521234500001(11)250229", "Gs1.Lint.DateDay")]         // 2025 is not a leap year
    [InlineData("(01)09521234500001(17)25121", "Gs1.Value.TooShort")]
    [InlineData("(01)09521234500001(10)ABCDEFGHIJKLMNOPQRSTU", "Gs1.Value.TooLong")]  // 21 characters, 20 allowed
    [InlineData("(01)09521234500001(10)AB CD", "Gs1.Lint.Cset82")]           // a space is not in the character set
    [InlineData("(01)0952123450000A", "Gs1.Lint.NonDigit")]
    [InlineData("(01)09521234500001(10)A(10)B", "Gs1.Message.Duplicate")]     // same AI, different values
    [InlineData("(01)09521234500001(02)09521234500001(37)5", "Gs1.Message.Excludes")]  // 01 and 02 exclude each other
    [InlineData("(4326)251200", "Gs1.Lint.DateDay")]                        // release date has no day-zero form (yymmdd)
    [InlineData("(0099)1", "Gs1.Ai.Unknown")]
    [InlineData("(422)999", "Gs1.Lint.Country")]                             // 999 is not an ISO 3166 country
    [InlineData("(3910)000100", "Gs1.Lint.Currency")]
    [InlineData("(7007)251301", "Gs1.Lint.DateMonth")]
    public void InvalidElementStrings_GiveTheDocumentedKey(string bracketed, string expectedKey)
    {
        Assert.Contains(expectedKey, Keys(bracketed));
    }

    [Fact]
    public void ITIP_PieceAndTotalAreChecked()
    {
        Assert.DoesNotContain(Keys("(8006)095212345000010102"), k => k.StartsWith("Gs1.Lint.Piece", StringComparison.Ordinal));
        Assert.Contains("Gs1.Lint.PieceExceeds", Keys("(8006)095212345000010201"));
        Assert.Contains("Gs1.Lint.PieceZero", Keys("(8006)095212345000010002"));
    }

    [Fact]
    public void RequiredCompanions_AreAWarning_NotAnError()
    {
        // (10) needs a GTIN (01, 02, 03 ...) in the item's data, but a label may carry it in another symbol.
        var m = Gs1Message.ParseBracketed("(10)ABC", out _)!;
        var issues = Gs1Validator.Validate(m, Fixed);
        var req = Assert.Single(issues, i => i.MessageKey == "Gs1.Message.Requires");
        Assert.Equal(ValidationSeverity.Warning, req.Severity);
        Assert.Equal("10", req.Args[0]);
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void RepeatedIdenticalElement_IsOnlyAWarning()
    {
        var m = Gs1Message.ParseBracketed("(01)09521234500001(10)A(10)A", out _)!;
        var issues = Gs1Validator.Validate(m, Fixed);
        Assert.Contains(issues, i => i.MessageKey == "Gs1.Message.DuplicateSame" && i.Severity == ValidationSeverity.Warning);
        Assert.DoesNotContain(issues, i => i.MessageKey == "Gs1.Message.Duplicate");
    }

    [Fact]
    public void DayZero_IsAcceptedWhereTheDictionaryUsesYymmd0_AndRefusedForYymmdd()
    {
        Assert.Null(Gs1Linters.Lint("yymmd0", "250100", "17", Fixed));
        Assert.NotNull(Gs1Linters.Lint("yymmdd", "250100", "4326", Fixed));
        Assert.Null(Gs1Linters.Lint("yyyymmd0", "20250100", "7250", Fixed));
        Assert.NotNull(Gs1Linters.Lint("yyyymmdd", "20250100", "7250", Fixed));
    }

    [Theory]
    // GS1 General Specifications 7.12: a two-digit year is read as within 49 years back and 50 ahead of the current year; the century
    // only matters on 29 February.
    [InlineData(26, "000229", true)]    // 2000 was a leap year
    [InlineData(26, "240229", true)]
    [InlineData(26, "250229", false)]
    [InlineData(26, "760229", true)]    // 76 is 2076, exactly 50 years ahead: a leap year
    [InlineData(26, "770229", false)]   // 77 is 1977, not 2077: not a leap year
    public void Century_FollowsTheReferenceYear(int reference, string date, bool valid) =>
        Assert.Equal(valid, Gs1Linters.Lint("yymmd0", date, "11", new Gs1LintOptions(reference)) is null);

    // ---- 48-character limit and FNC1 placement ----------------------------------------------------

    [Fact]
    public void DataCharacterCount_CountsIdentifiersAndSeparators_ButNotTheStartAndLeadingFnc1()
    {
        // 5.4.4.3: AIs and the FNC1 used as separator count; start, leading FNC1, check and stop do not.
        var m = Gs1Message.ParseBracketed("(00)376104250021234569(10)12345678901234567(11)250504", out _)!;
        Assert.Equal(20 + 2 + 17 + 1 + 8, m.DataCharacterCount);
        Assert.Equal(48, m.DataCharacterCount);
        Assert.DoesNotContain(Gs1Validator.ValidateForGs128(m, Fixed), i => i.MessageKey == "Gs1.Gs128.TooLong");
    }

    [Fact]
    public void FortyNineCharacters_AreTooLongForGs128()
    {
        var m = Gs1Message.ParseBracketed("(00)376104250021234569(10)123456789012345678(11)250504", out _)!;
        Assert.Equal(49, m.DataCharacterCount);
        var issue = Assert.Single(Gs1Validator.ValidateForGs128(m, Fixed), i => i.MessageKey == "Gs1.Gs128.TooLong");
        Assert.Equal(new[] { "49", "48" }, issue.Args);
    }

    [Fact]
    public void TheSeparatorIsOnlyWrittenAfterVariableLengthElements_ThatAreNotLast()
    {
        // 1.4.2 example: (01)09521234500001(10)ABC123<GS>(11)250504
        var m = Gs1Message.ParseBracketed("(01)09521234500001(10)ABC123(11)250504", out _)!;
        Assert.Equal("0109521234500001" + "10ABC123" + GS + "11250504", m.ToElementString());
        // Predefined length elements are never followed by a separator (7.8.6.1); the last element needs none.
        var fixedOnly = Gs1Message.ParseBracketed("(01)95012345678903(3102)000400(11)250504", out _)!;
        Assert.Equal("01950123456789033102000400" + "11250504", fixedOnly.ToElementString());
        // AI 402 (GSIN) has a fixed format but not a predefined length: it needs the separator when something follows (1.4.2 note).
        var gsin = Gs1Message.ParseBracketed("(402)12345678901234563(00)376104250021234569", out _)!;
        Assert.Equal("40212345678901234563" + GS + "00376104250021234569", gsin.ToElementString());
        // 1.4.2: no separator at the end of the string
        Assert.Equal("4149521234500018" + "254543LF", Gs1Message.ParseBracketed("(414)9521234500018(254)543LF", out _)!.ToElementString());
    }

    [Fact]
    public void ThreeVariableElements_GetTwoSeparators()
    {
        var m = Gs1Message.ParseBracketed("(91)0005886(10)0000410549(99)05", out _)!;
        Assert.Equal("910005886" + GS + "100000410549" + GS + "9905", m.ToElementString());
    }

    // ---- reading ---------------------------------------------------------------------------------

    [Fact]
    public void BothForms_RoundTrip()
    {
        const string text = "(01)09521234500001(10)ABC123(11)250504";
        var bracketed = Gs1Message.ParseBracketed(text, out var issues)!;
        Assert.Empty(issues);
        Assert.Equal(text, bracketed.ToBracketed());
        Assert.Equal(text, bracketed.ToHumanReadable());

        var scanned = Gs1Message.ParseElementString(bracketed.ToElementString(), out var scanIssues)!;
        Assert.Empty(scanIssues);
        Assert.Equal(bracketed.Elements, scanned.Elements);

        // A scanner's symbology identifier in front is ignored, and Parse() chooses the form itself.
        Assert.Equal(bracketed.Elements, Gs1Message.Parse("]C1" + bracketed.ToElementString(), out _)!.Elements);
        Assert.Equal(bracketed.Elements, Gs1Message.Parse(text, out _)!.Elements);
    }

    [Fact]
    public void Bracketed_AllowsSpacesBetweenElements_AndEscapedParentheses()
    {
        var m = Gs1Message.ParseBracketed("(01)09521234500001  (10)AB\\(C", out var issues)!;
        Assert.Empty(issues);
        Assert.Equal("AB(C", m.ValueOf("10"));
        Assert.Equal("(01)09521234500001(10)AB\\(C", m.ToBracketed());
    }

    [Theory]
    [InlineData("", "Gs1.Parse.Empty")]
    [InlineData("(01)09521234500001(10", "Gs1.Parse.Malformed")]
    [InlineData("01)0952", "Gs1.Parse.Malformed")]
    [InlineData("(1)5", "Gs1.Parse.Malformed")]
    [InlineData("(0A)5", "Gs1.Parse.Malformed")]
    public void BadBracketedText_IsRefusedWithAKey(string text, string key)
    {
        Assert.Null(Gs1Message.ParseBracketed(text, out var issues));
        Assert.Equal(key, Assert.Single(issues).MessageKey);
    }

    [Theory]
    [InlineData("500000", "Gs1.Parse.UnknownAi")]
    [InlineData("0109521234500001" + "\u001d" + "11250504", "Gs1.Parse.SeparatorAfterFixed")]   // FNC1 after a predefined length element
    [InlineData("10ABC\u001d", "Gs1.Parse.TrailingSeparator")]
    [InlineData("01095212345", "Gs1.Parse.TooShort")]
    public void BadScannerText_IsRefusedWithAKey(string text, string key)
    {
        Assert.Null(Gs1Message.ParseElementString(text, out var issues));
        Assert.Equal(key, Assert.Single(issues).MessageKey);
    }

    [Fact]
    public void LinterIssues_CarryTheIdentifierFirst()
    {
        var m = Gs1Message.ParseBracketed("(01)09521234500002", out _)!;
        var issue = Assert.Single(Gs1Validator.Validate(m, Fixed));
        Assert.Equal("Gs1.Lint.CheckDigit", issue.MessageKey);
        Assert.Equal(new[] { "01", "1" }, issue.Args);   // AI, and the digit that would be right
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    // ---- individual linters ----------------------------------------------------------------------

    [Theory]
    [InlineData("csum", "376104250021234569", true)]     // Table 7-9
    [InlineData("csum", "376104250021234568", false)]
    [InlineData("csumalpha", "22", true)]                // a value that is only the check pair of an empty body
    [InlineData("hyphen", "-", true)]
    [InlineData("hyphen", "A", false)]
    [InlineData("hhmi", "2359", true)]
    [InlineData("hhmi", "2400", false)]
    [InlineData("hhmi", "1260", false)]
    [InlineData("winding", "9", true)]
    [InlineData("winding", "2", false)]
    [InlineData("yesno", "1", true)]
    [InlineData("yesno", "2", false)]
    [InlineData("iso5218", "9", true)]
    [InlineData("iso5218", "3", false)]
    [InlineData("latitude", "1800000000", true)]
    [InlineData("latitude", "1800000001", false)]
    [InlineData("longitude", "3600000000", true)]
    [InlineData("longitude", "3600000001", false)]
    [InlineData("pieceoftotal", "0305", true)]
    [InlineData("pieceoftotal", "0503", false)]
    [InlineData("posinseqslash", "3/10", true)]
    [InlineData("posinseqslash", "11/10", false)]
    [InlineData("posinseqslash", "03/10", false)]
    [InlineData("nonzero", "0001", true)]
    [InlineData("nonzero", "0000", false)]
    [InlineData("nozeroprefix", "12", true)]
    [InlineData("nozeroprefix", "012", false)]
    [InlineData("zero", "0", true)]
    [InlineData("zero", "1", false)]
    [InlineData("importeridx", "_", true)]
    [InlineData("importeridx", "!", false)]
    [InlineData("mediatype", "05", true)]
    [InlineData("mediatype", "11", false)]
    [InlineData("mediatype", "85", true)]
    [InlineData("pcenc", "A%41B", true)]
    [InlineData("pcenc", "A%4", false)]
    [InlineData("hasnondigit", "123", false)]
    [InlineData("hasnondigit", "12A", true)]
    // ISO 13616 example IBAN from the standard's own register entries
    [InlineData("iban", "GB82WEST12345698765432", true)]
    [InlineData("iban", "GB82WEST12345698765433", false)]
    [InlineData("iso3166999", "999", true)]
    [InlineData("iso3166alpha2", "PA", true)]
    public void Linter_AcceptsAndRefuses(string linter, string value, bool valid) =>
        Assert.Equal(valid, Gs1Linters.Lint(linter, value, "00", Fixed) is null);

    [Fact]
    public void CheckPair_MatchesTheGmnExampleOfTheSpecification()
    {
        // General Specifications 7.9.5, Figure 7-15: the 25-character GMN 1987654Ad4X4bL5ttr2310c2K, whose check characters 2 and K come
        // from the sum 24521, 24521 mod 1021 = 17 = 0 * 32 + 17.
        Assert.Null(Gs1Linters.Lint("csumalpha", "1987654Ad4X4bL5ttr2310c2K", "8013", Fixed));
        Assert.NotNull(Gs1Linters.Lint("csumalpha", "1987654Ad4X4bL5ttr2310c2L", "8013", Fixed));     // wrong check character
        Assert.NotNull(Gs1Linters.Lint("csumalpha", "1987654Ad4X4bL5ttr2310d2K", "8013", Fixed));     // a changed character
        Assert.NotNull(Gs1Linters.Lint("csumalpha", "1987654Ad4X4bL5ttr2301c2K", "8013", Fixed));     // two characters swapped
        Assert.Equal("Gs1.Lint.CheckPair", Gs1Linters.Lint("csumalpha", "1987654Ad4X4bL5ttr2310c2L", "8013", Fixed)!.MessageKey);
        // The same value as a whole GMN element validates against the dictionary.
        Assert.Empty(Gs1Validator.ValidateElement(new Gs1Element("8013", "1987654Ad4X4bL5ttr2310c2K"), Fixed));
    }
}
