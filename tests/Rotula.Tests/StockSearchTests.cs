using System.Diagnostics;
using Rotula.Core.Stocks;
using static Rotula.Tests.StockTestSupport;

namespace Rotula.Tests;

/// <summary>Search over the bundled library: part numbers, many size spellings, text, filters, near sizes, favourites.</summary>
public class StockSearchTests
{
    private static StockSearchResult Run(string q, StockSearchOptions? o = null) => Bundled.Search(q, o);

    private static bool Is(StockEntry e, double w, double h) => e.HeightMm is { } hh && Math.Abs(e.WidthMm - w) <= 0.6 && Math.Abs(hh - h) <= 0.6;

    // ---- Part numbers -------------------------------------------------------------------------------

    [Theory]
    [InlineData("10010041")]
    [InlineData(" 10010041 ")]
    [InlineData("1001-0041")]
    public void PartNumber_ExactComesFirst(string query)
    {
        var r = Run(query);
        Assert.Equal("zebra-10010041", r.Hits[0].Entry.Id);
        Assert.True(r.Hits[0].Matched.HasFlag(StockMatchKind.PartExact));
    }

    [Fact]
    public void PartNumber_PrefixAndSubstringRankBelowExact()
    {
        var prefix = Run("1001004");
        Assert.True(prefix.Hits.Count >= 1);
        Assert.All(prefix.Hits.Take(3), h => Assert.StartsWith("1001004", h.Entry.PartNumber!.Replace("-", ""), StringComparison.OrdinalIgnoreCase));
        Assert.True(prefix.Hits[0].Matched.HasFlag(StockMatchKind.PartPrefix));

        var inside = Run("010041");
        Assert.Contains(inside.Hits, h => h.Entry.Id == "zebra-10010041" && h.Matched.HasFlag(StockMatchKind.PartContains));
    }

    [Fact]
    public void PartNumber_IgnoresCaseAndPunctuation()
    {
        var a = Run("02d102152300k");
        var b = Run("02D-102152300-K");
        Assert.NotEmpty(a.Hits);
        Assert.Equal(a.Hits[0].Entry.Id, b.Hits[0].Entry.Id);
    }

    // ---- Sizes in many spellings --------------------------------------------------------------------

    [Theory]
    [InlineData("4x6")]
    [InlineData("4 x 6")]
    [InlineData("4 x 6 in")]
    [InlineData("4x6in")]
    [InlineData("4in x 6in")]
    [InlineData("4\"x6\"")]
    [InlineData("4\" x 6\"")]
    [InlineData("4 X 6 INCHES")]
    [InlineData("4 by 6 inch")]
    [InlineData("4×6")]
    [InlineData("4 por 6 pulgadas")]
    [InlineData("102x152")]
    [InlineData("102 x 152 mm")]
    [InlineData("10.2 x 15.2 cm")]
    [InlineData("101.6x152.4mm")]
    [InlineData("6x4 in")]
    public void Size_FourBySixInSpellings(string query)
    {
        var r = Run(query, new StockSearchOptions { MaxResults = 1000 });
        Assert.NotNull(r.Size);
        Assert.True(r.TotalHits > 20, query + " found " + r.TotalHits);
        Assert.All(r.Hits, h => Assert.True(Is(h.Entry, 101.6, 152.4) || Is(h.Entry, 152.4, 101.6), h.Entry.ToString()));
    }

    [Fact]
    public void Size_SwappedOrderIsMarkedAndRankedLower()
    {
        var r = Run("6x4 in", new StockSearchOptions { MaxResults = 1000 });
        // Real 6 x 4 in stocks match as typed; the 4 x 6 in ones match swapped and rank below them.
        Assert.Contains(r.Hits, h => h.Matched.HasFlag(StockMatchKind.SizeSwapped));
        Assert.True(r.Hits.First().Matched.HasFlag(StockMatchKind.Size));
        Assert.Equal(StockMatchKind.Size, r.Hits[0].Matched & (StockMatchKind.Size | StockMatchKind.SizeSwapped));
        int firstSwapped = r.Hits.ToList().FindIndex(h => h.Matched.HasFlag(StockMatchKind.SizeSwapped));
        Assert.All(r.Hits.Take(firstSwapped), h => Assert.True(h.Matched.HasFlag(StockMatchKind.Size)));
        var straight = Run("4x6 in");
        Assert.True(straight.Hits[0].Matched.HasFlag(StockMatchKind.Size));
    }

    [Fact]
    public void Size_WristbandFoundByItsLongSideFirst()
    {
        // Stored as 25.4 x 279.4 (short side first); people type 11 x 1.
        var r = Run("11 x 1 in", new StockSearchOptions { MaxResults = 1000 });
        Assert.Contains(r.Hits, h => h.Entry.Category == "wristband" && h.Matched.HasFlag(StockMatchKind.SizeSwapped));
        var r2 = Run("1 x 11 in");
        Assert.Contains(r2.Hits, h => h.Entry.Category == "wristband" && h.Matched.HasFlag(StockMatchKind.Size));
    }

    [Fact]
    public void Size_DecimalCommaAndFractionsOfAnInch()
    {
        var r = Run("2,25 x 1,25 in");
        Assert.NotEmpty(r.Hits);
        Assert.All(r.Hits, h => Assert.True(Is(h.Entry, 57.15, 31.75) || Is(h.Entry, 31.75, 57.15)));
    }

    [Fact]
    public void Size_NearSuggestionsForAMetricQuery()
    {
        // 100 x 150 mm is not 4 x 6 in: it is 1.6 and 2.4 mm away, so the inch stock is a suggestion, not a hit.
        var r = Run("100x150mm");
        Assert.All(r.Hits, h => Assert.True(Is(h.Entry, 100, 150) || Is(h.Entry, 150, 100)));
        Assert.Contains(r.NearHits, h => Is(h.Entry, 101.6, 152.4));
        Assert.True(r.NearHits.Zip(r.NearHits.Skip(1)).All(p => p.First.SizeDeltaMm <= p.Second.SizeDeltaMm));
        Assert.All(r.NearHits, h => Assert.InRange(h.SizeDeltaMm!.Value, 0.6, 3.0));

        var off = Run("100x150mm", new StockSearchOptions { NearToleranceMm = 0 });
        Assert.Empty(off.NearHits);
    }

    [Fact]
    public void Size_FindNearIsSortedAndWithinTolerance()
    {
        var near = Bundled.FindNear(100, 150, 3.0, new StockSearchOptions { MaxResults = 500 });
        Assert.NotEmpty(near);
        Assert.All(near, h => Assert.True(h.SizeDeltaMm <= 3.0));
        Assert.True(near.Zip(near.Skip(1)).All(p => p.First.SizeDeltaMm <= p.Second.SizeDeltaMm));
        Assert.Empty(Bundled.FindNear(1000, 1000, 3));
    }

    [Theory]
    [InlineData("30x2")]
    [InlineData("1x30")]
    [InlineData("0.5 x 40")]
    public void Size_ImplausibleUnitlessSizeDoesNotListEverything(string query)
    {
        var r = Run(query, new StockSearchOptions { MaxResults = 5000 });
        Assert.True(r.TotalHits < 837, query + " returned " + r.TotalHits);
        Assert.Null(r.Size);
    }

    [Fact]
    public void Size_RollLengthIsNotALabelSize()
    {
        // "4 in x 30.5 m" is a continuous roll (width x length in metres), not a 4 x 30.5 in label.
        Assert.False(StockSizeParser.TryParse("Z-Perform 1000D 2.4 mil 4 in x 30.5 m", out _));
        var r = Run("4 in x 30.5 m");
        Assert.Null(r.Size);
    }

    [Fact]
    public void Size_WithWordsNarrowsTheResults()
    {
        var all = Run("4x6", new StockSearchOptions { MaxResults = 1000 });
        var zp = Run("z-perform 4x6", new StockSearchOptions { MaxResults = 1000 });
        Assert.True(zp.TotalHits > 0 && zp.TotalHits < all.TotalHits);
        Assert.All(zp.Hits, h => Assert.Contains("Z-Perform", h.Entry.Name + h.Entry.Series + h.Entry.StockFamily, StringComparison.OrdinalIgnoreCase));
        Assert.All(zp.Hits, h => Assert.True(Is(h.Entry, 101.6, 152.4) || Is(h.Entry, 152.4, 101.6)));
    }

    // ---- Words ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("polypro 3000t", "PolyPro")]
    [InlineData("z-band direct", "Z-Band")]
    [InlineData("zband direct", "Z-Band")]
    [InlineData("ZBAND", "Z-Band")]
    [InlineData("8000d lab", "8000D")]
    public void Words_MatchNameAndFamilyIgnoringPunctuation(string query, string mustContain)
    {
        var r = Run(query, new StockSearchOptions { MaxResults = 1000 });
        Assert.NotEmpty(r.Hits);
        Assert.All(r.Hits, h => Assert.Contains(mustContain, h.Entry.Name + "|" + h.Entry.Series + "|" + h.Entry.StockFamily, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Words_AllWordsMustMatchAndGarbageFindsNothing()
    {
        Assert.Empty(Run("polypro zzzzqq").Hits);
        Assert.Empty(Run("qqqqxxxx").Hits);
            }

    [Fact]
    public void Words_EmptyQueryListsEverythingUpToTheCap()
    {
        var r = Run("   ");
        Assert.Equal(837, r.TotalHits);
        Assert.Equal(100, r.Hits.Count);
        Assert.Equal(837, Run("", new StockSearchOptions { MaxResults = 5000 }).Hits.Count);
    }

    [Fact]
    public void Words_AccentsAreFolded()
    {
        var c = StockCatalog.FromJson(Utf8(FileJson(new[] { EntryJson("a-1", "Etiqueta t\\u00E9rmica ni\\u00F1o") })));
        Assert.Single(c.Search("termica nino").Hits);
        Assert.Single(c.Search("térmica").Hits);
    }

    [Fact]
    public void Words_LongOrHostileQueriesAreHarmless()
    {
        Assert.NotNull(Run(new string('x', 100_000)));
        Assert.NotNull(Run(string.Concat(Enumerable.Repeat("1 x ", 5000))));
        Assert.NotNull(Run("\0\u0001￿"));
        Assert.NotNull(Bundled.Search(null));
    }

    // ---- Filters --------------------------------------------------------------------------------------

    [Fact]
    public void Filter_VerifiedOnly()
    {
        var all = Run("", new StockSearchOptions { MaxResults = 5000 });
        var ok = Run("", new StockSearchOptions { MaxResults = 5000, VerifiedOnly = true });
        Assert.Equal(809, ok.TotalHits);
        Assert.Equal(837, all.TotalHits);
        Assert.All(ok.Hits, h => Assert.True(h.Entry.Verified));
    }

    [Fact]
    public void Filter_BrandAndFamily()
    {
        var avery = Run("", new StockSearchOptions { Brand = "avery" });
        Assert.Equal(3, avery.TotalHits);
        var fam = Run("", new StockSearchOptions { Family = "z-select 4000d", MaxResults = 1000 });
        Assert.True(fam.TotalHits >= 50);
        Assert.All(fam.Hits, h => Assert.Contains("4000D", h.Entry.StockFamily + h.Entry.Series, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Filter_LayoutRollSheetRound()
    {
        Assert.Equal(3, Run("", new StockSearchOptions { Layout = StockLayout.Sheet }).TotalHits);
        Assert.Equal(834, Run("", new StockSearchOptions { Layout = StockLayout.Roll }).TotalHits);
        Assert.Equal(0, Run("", new StockSearchOptions { Layout = StockLayout.Round }).TotalHits);
        var catalog = StockCatalog.FromJson(Utf8(FileJson(new[] { EntryJson("r-1", "Round dot", "\"shape\":\"round\"") })));
        Assert.Equal(StockLayout.Round, catalog.Entries[0].Layout);
        Assert.Single(catalog.Search("", new StockSearchOptions { Layout = StockLayout.Round }).Hits);
    }

    [Fact]
    public void Filter_PrintTechnology()
    {
        var dt = Run("", new StockSearchOptions { Media = StockMedia.DirectThermal, MaxResults = 5000 });
        var tt = Run("", new StockSearchOptions { Media = StockMedia.ThermalTransfer, MaxResults = 5000 });
        Assert.All(dt.Hits, h => Assert.True(h.Entry.Media is StockMedia.DirectThermal or StockMedia.Either));
        Assert.All(tt.Hits, h => Assert.True(h.Entry.Media is StockMedia.ThermalTransfer or StockMedia.Either));
        Assert.True(dt.TotalHits > 300 && tt.TotalHits > 400);
    }

    [Fact]
    public void Filter_NativeUnit()
    {
        var inch = Run("", new StockSearchOptions { NativeUnit = StockUnit.Inches, MaxResults = 5000 });
        var mm = Run("", new StockSearchOptions { NativeUnit = StockUnit.Millimeters, MaxResults = 5000 });
        Assert.Equal(837, inch.TotalHits + mm.TotalHits);
        Assert.Contains(inch.Hits, h => h.Entry.Id == "std-4x6in");
        Assert.Contains(mm.Hits, h => h.Entry.Id == "iso-102x152mm");
        Assert.Equal("4 x 6 in", Bundled.Find("std-4x6in")!.SizeText);
        Assert.Equal("101.6 x 152.4 mm", Bundled.Find("iso-102x152mm")!.SizeText);
    }

    [Fact]
    public void Filters_CombineWithASizeQuery()
    {
        var r = Run("4x6", new StockSearchOptions { Brand = "Zebra", VerifiedOnly = true, Media = StockMedia.DirectThermal, MaxResults = 1000 });
        Assert.NotEmpty(r.Hits);
        Assert.All(r.Hits, h => { Assert.Equal("Zebra", h.Entry.Brand); Assert.True(h.Entry.Verified); });
    }

    [Fact]
    public void Ranking_IsDeterministic()
    {
        var a = Run("z-perform 4x6").Hits.Select(h => h.Entry.Id).ToList();
        var b = Run("z-perform 4x6").Hits.Select(h => h.Entry.Id).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Performance_SearchIsInstant()
    {
        // Not a tight assertion (CI machines vary); the measured figure goes to the report.
        _ = Run("warm up 4x6");
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
        {
            _ = Run("z-perform 4x6");
            _ = Run("10010041");
            _ = Run("100x150mm");
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"600 searches took {sw.ElapsedMilliseconds} ms");
    }

    // ---- Favourites and recent ------------------------------------------------------------------------

    [Fact]
    public void UserList_FavoritesAndRecent()
    {
        var list = new StockUserList();
        Assert.True(list.ToggleFavorite("std-4x6in"));
        Assert.True(list.ToggleFavorite("zebra-10010041"));
        Assert.True(list.IsFavorite("STD-4X6IN"));
        Assert.False(list.ToggleFavorite("std-4x6in"));
        Assert.Equal(new[] { "zebra-10010041" }, list.Favorites.ToArray());
        Assert.False(list.ToggleFavorite("bad id!"));

        for (int i = 0; i < 30; i++) list.Touch("id-" + i);
        list.Touch("id-5");
        Assert.Equal(StockUserList.MaxRecent, list.Recent.Count);
        Assert.Equal("id-5", list.Recent[0]);
        Assert.Equal(1, list.Recent.Count(r => r == "id-5"));

        list.Touch("std-4x6in");
        Assert.Equal("std-4x6in", list.ResolveRecent(Bundled)[0].Id);
        Assert.Equal("zebra-10010041", list.ResolveFavorites(Bundled)[0].Id);
    }

    [Fact]
    public void UserList_JsonRoundTripAndDamagedInput()
    {
        var list = new StockUserList();
        list.ToggleFavorite("a-1");
        list.Touch("b-2");
        var again = StockUserList.FromJson(list.ToJson());
        Assert.Equal(list.Favorites, again.Favorites);
        Assert.Equal(list.Recent, again.Recent);

        foreach (string bad in new[] { "", "not json", "[1]", "{\"version\":7,\"favorites\":[\"x\"]}", "{\"version\":1,\"favorites\":[1,2]}", "{\"version\":1,\"favorites\":[\"bad id!\",\"ok-1\"]}" })
            Assert.NotNull(StockUserList.FromJson(bad));
        Assert.Equal(new[] { "ok-1" }, StockUserList.FromJson("{\"version\":1,\"favorites\":[\"bad id!\",\"ok-1\"]}").Favorites.ToArray());
        Assert.Empty(StockUserList.FromJson(new string('x', 500_000)).Favorites);
    }
}
