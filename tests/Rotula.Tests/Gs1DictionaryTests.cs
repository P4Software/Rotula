using Rotula.Core.Gs1;

namespace Rotula.Tests;

/// <summary>
/// Integrity of the embedded GS1 dictionary (built from the GS1 Barcode Syntax Dictionary release 2026-01-27 by tools/gs1-dictionary)
/// and of the syntax tests that go with it.
/// </summary>
public class Gs1DictionaryTests
{
    [Fact]
    public void Source_StatesReleaseLicenceAndCopyright()
    {
        var s = Gs1Dictionary.Source;
        Assert.Equal("2026-01-27", s.Release);
        Assert.Equal("Apache-2.0", s.License);
        Assert.Contains(s.Copyright, c => c.Contains("GS1 AISBL", StringComparison.Ordinal));
        Assert.Contains(s.Copyright, c => c.Contains("BWIPP", StringComparison.Ordinal));
        Assert.Contains(s.Copyright, c => c.Contains("Zint", StringComparison.Ordinal));
        Assert.Contains("github.com/gs1/gs1-syntax-dictionary", s.Url, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(s.Changes));
    }

    [Fact]
    public void Counts_MatchTheRelease()
    {
        // 224 lines in the release file; the ranges (31nn, 32nn, 91-99, ...) expand to 541 identifiers.
        Assert.Equal(224, Gs1Dictionary.EntryLineCount);
        Assert.Equal(541, Gs1Dictionary.All.Count);
        Assert.Equal(Gs1Dictionary.All.Count, Gs1Dictionary.All.Select(a => a.Ai).Distinct().Count());
    }

    [Fact]
    public void EveryIdentifier_IsWellFormed()
    {
        foreach (var d in Gs1Dictionary.All)
        {
            Assert.InRange(d.Ai.Length, 2, 4);
            Assert.All(d.Ai, c => Assert.True(char.IsAsciiDigit(c), d.Ai));
            Assert.NotEmpty(d.Components);
            Assert.False(string.IsNullOrWhiteSpace(d.Title), d.Ai);
            for (int i = 0; i < d.Components.Count; i++)
            {
                var c = d.Components[i];
                Assert.True(c.MinLength >= 1 && c.MinLength <= c.MaxLength, d.Ai);
                // Only the final component may have a variable length; optional ones come last.
                if (i < d.Components.Count - 1) Assert.True(c.IsFixedLength, d.Ai + " has a variable component before the last one");
                if (c.Optional) Assert.All(d.Components.Skip(i + 1), n => Assert.True(n.Optional, d.Ai));
            }
            if (d.PredefinedLength) Assert.True(d.HasFixedValueLength, d.Ai + " is predefined but not fixed");
        }
    }

    [Fact]
    public void Identifiers_AreAPrefixFreeSet()
    {
        // Message parsing without parentheses relies on this: the first 2, 3 or 4 digits that form a known identifier are the identifier.
        var all = Gs1Dictionary.All.Select(a => a.Ai).ToList();
        foreach (var a in all)
            Assert.DoesNotContain(all, b => b != a && b.StartsWith(a, StringComparison.Ordinal));
    }

    [Fact]
    public void PredefinedLength_AgreesWithTable7_6_OfTheGeneralSpecifications()
    {
        // GS1 General Specifications release 26.0, section 7.8.3, Table 7-6: the identifiers that start with these two digits have a predefined length.
        var table76 = new HashSet<string> { "00", "01", "02", "03", "04", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "31", "32", "33", "34", "35", "36", "41" };
        foreach (var d in Gs1Dictionary.All)
            Assert.Equal(table76.Contains(d.Ai[..2]), d.PredefinedLength);
    }

    [Fact]
    public void EveryLinterNamedInTheDictionary_IsImplementedOrExplicitlyNotChecked()
    {
        var used = Gs1Dictionary.All.SelectMany(a => a.Components).SelectMany(c => c.Linters).Distinct().ToList();
        Assert.NotEmpty(used);
        foreach (var name in used)
            Assert.True(Gs1Linters.IsImplemented(name) || Gs1Linters.NotChecked.Contains(name), "No implementation and no 'not checked' entry for linter " + name);
        // The two lists never overlap, and every 'not checked' name is really used by the dictionary.
        Assert.Empty(Gs1Linters.Implemented.Intersect(Gs1Linters.NotChecked));
        Assert.All(Gs1Linters.NotChecked, n => Assert.Contains(n, used));
        Assert.All(Gs1Linters.PartiallyChecked, n => Assert.True(Gs1Linters.IsImplemented(n)));
    }

    [Fact]
    public void Relations_PointAtRealIdentifiers()
    {
        var all = Gs1Dictionary.All.Select(a => a.Ai).ToList();
        foreach (var d in Gs1Dictionary.All)
        {
            foreach (var pattern in d.Excludes.Concat(d.Requires.SelectMany(r => r.SelectMany(alt => alt))))
                Assert.Contains(all, ai => Gs1Dictionary.Matches(pattern, ai));
            if (d.DigitalLinkQualifiers is { } seqs)
                foreach (var q in seqs.SelectMany(s => s)) Assert.True(Gs1Dictionary.IsKnown(q), q);
        }
    }

    [Fact]
    public void KnownEntries_HaveTheDocumentedShape()
    {
        var gtin = Gs1Dictionary.Get("01");
        Assert.Equal("GTIN", gtin.DataTitle);
        Assert.True(gtin.PredefinedLength && gtin.HasFixedValueLength && gtin.IsNumeric);
        Assert.Equal("N14", gtin.FormatText);
        Assert.Equal(new[] { "csum", "gcppos2" }, gtin.Components[0].Linters);
        Assert.True(gtin.IsDigitalLinkKey);
        Assert.Equal(new[] { new[] { "22", "10", "21" }, new[] { "235" } }, gtin.DigitalLinkQualifiers!.Select(s => s.ToArray()).ToArray());

        var lot = Gs1Dictionary.Get("10");
        Assert.Equal("X..20", lot.FormatText);
        Assert.False(lot.PredefinedLength);
        Assert.True(lot.RequiresFnc1Separator);
        Assert.Equal("Batch or lot number", lot.Title);

        // 253 has an optional serial component; 421 has a three-digit country and a postal code.
        Assert.Equal("N13 [X..17]", Gs1Dictionary.Get("253").FormatText);
        Assert.Equal("N3 X..9", Gs1Dictionary.Get("421").FormatText);

        var weight = Gs1Dictionary.Get("3103");
        Assert.True(weight.DecimalPlacesInAi && weight.PredefinedLength);
        Assert.Equal(6, weight.MaxValueLength);
        Assert.False(Gs1Dictionary.Get("8003").PredefinedLength);
        Assert.Null(Gs1Dictionary.Find("99999"));
        Assert.Equal("01", Gs1Dictionary.FindAtStart("0109521234500001".AsSpan())!.Ai);
        Assert.Equal("3103", Gs1Dictionary.FindAtStart("3103000123".AsSpan())!.Ai);
    }

    [Theory]
    [InlineData("310n", "3103", true)]
    [InlineData("310n", "3113", false)]
    [InlineData("31nn", "3202", false)]
    [InlineData("31nn", "3102", true)]
    [InlineData("01", "01", true)]
    [InlineData("01", "010", false)]
    public void WildcardPatterns_MatchDigitsOnly(string pattern, string ai, bool expected) =>
        Assert.Equal(expected, Gs1Dictionary.Matches(pattern, ai));

    [Fact]
    public void CodeLists_HaveTheSizesOfTheReferenceLists()
    {
        // The lists behind the iso3166, iso3166alpha2, iso4217 and packagetype tests, counted through the tests themselves.
        int Count(string linter, IEnumerable<string> candidates) => candidates.Count(c => Gs1Linters.Lint(linter, c, "00") is null);
        var numbers = Enumerable.Range(0, 1000).Select(n => n.ToString("000"));
        var pairs = from a in Enumerable.Range('A', 26) from b in Enumerable.Range('A', 26) select ((char)a).ToString() + (char)b;
        Assert.Equal(249, Count("iso3166", numbers));
        Assert.Equal(179, Count("iso4217", numbers));
        Assert.Equal(249, Count("iso3166alpha2", pairs));
        Assert.Null(Gs1Linters.Lint("iso3166", "840", "421")); Assert.Null(Gs1Linters.Lint("iso3166", "724", "421")); Assert.NotNull(Gs1Linters.Lint("iso3166", "999", "421"));
        Assert.Null(Gs1Linters.Lint("iso3166999", "999", "7030"));
        Assert.Null(Gs1Linters.Lint("iso4217", "978", "3910")); Assert.Null(Gs1Linters.Lint("iso4217", "840", "3910")); Assert.NotNull(Gs1Linters.Lint("iso4217", "000", "3910"));
        Assert.Null(Gs1Linters.Lint("iso3166alpha2", "PA", "4307")); Assert.Null(Gs1Linters.Lint("iso3166alpha2", "ES", "4307")); Assert.NotNull(Gs1Linters.Lint("iso3166alpha2", "XX", "4307"));
        Assert.Null(Gs1Linters.Lint("packagetype", "PX", "7041")); Assert.Null(Gs1Linters.Lint("packagetype", "X11", "7041")); Assert.NotNull(Gs1Linters.Lint("packagetype", "QQQ", "7041"));
    }
}
