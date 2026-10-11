using System.Text;
using Rotula.Core.Stocks;
using Rotula.Core.Validation;
using static Rotula.Tests.StockTestSupport;

namespace Rotula.Tests;

/// <summary>Loader, validator and damaged-file behaviour of the stock library.</summary>
public class StockCatalogTests
{
    [Fact]
    public void Bundled_LoadsEveryEntryWithoutIssues()
    {
        var c = StockCatalog.LoadBundled();
        // Exact figures on purpose: a loader that is too strict must fail loudly instead of silently dropping entries.
        Assert.Empty(c.Issues);
        Assert.Equal(837, c.Count);
        Assert.Equal(797, c.Entries.Count(e => e.Verified));
        Assert.Equal(40, c.Entries.Count(e => !e.Verified));
        Assert.Equal(1, c.Metadata.SchemaVersion);
        Assert.Contains("not affiliated", c.Metadata.Notes!, StringComparison.OrdinalIgnoreCase);
        Assert.Same(c, StockCatalog.LoadBundled());
    }

    [Fact]
    public void Bundled_KeepsTheOddEntries()
    {
        var c = Bundled;
        Assert.Equal(StockSensing.Mark, c.Find("zebra-10010062")!.Sensing);
        Assert.Equal(57, c.Entries.Count(e => e.HeightMm is null));
        Assert.All(c.Entries.Where(e => e.HeightMm is null), e => Assert.True(e.RollLengthM > 0 && e.Format is StockFormat.Continuous or StockFormat.Linerless));
        Assert.All(c.Entries, e => Assert.True(e.Sources.Count > 0 || e.SourceUrl is not null, e.Id));
        Assert.All(c.Entries, e => Assert.Equal(StockOrigin.Bundled, e.Origin));
    }

    [Fact]
    public void Bundled_PassesTheValidator()
    {
        // The same check CI runs on community contributions.
        string path = Path.Combine(Path.GetTempPath(), "rotula-stocks-validate-" + Guid.NewGuid().ToString("N") + ".json");
        using (var s = typeof(StockCatalog).Assembly.GetManifestResourceStream("Rotula.Core.Stocks.stocks.json")!)
        using (var f = File.Create(path)) s.CopyTo(f);
        try
        {
            var report = StockCatalogValidator.ValidateFile(path);
            Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors));
            Assert.Empty(report.Warnings);
            Assert.Equal(837, report.EntryCount);
            Assert.Equal(797, report.VerifiedCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Bundled_UnitsAgreeAndRangesArePlausible()
    {
        foreach (var e in Bundled.Entries)
        {
            Assert.InRange(Math.Abs(e.WidthMm - e.WidthIn * 25.4), 0, 0.6);
            if (e.HeightMm is { } h) Assert.InRange(Math.Abs(h - e.HeightIn!.Value * 25.4), 0, 0.6);
            Assert.InRange(e.WidthMm, 5, 600);
            if (e.CornerRadiusMm is { } r) Assert.InRange(r, 0, 3.2);
        }
        Assert.Equal(Bundled.Count, Bundled.Entries.Select(e => e.Id.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Bundled_HasNoPriceFieldsAndNoPrivatePaths()
    {
        string text;
        using (var s = typeof(StockCatalog).Assembly.GetManifestResourceStream("Rotula.Core.Stocks.stocks.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var names = new HashSet<string>();
        Collect(doc.RootElement, names);
        Assert.DoesNotContain(names, StockLimits.IsPriceLikeName);
        Assert.DoesNotMatch(@"[A-Za-z]:\\", text);
        Assert.DoesNotContain("price", text, StringComparison.OrdinalIgnoreCase);

        static void Collect(System.Text.Json.JsonElement el, HashSet<string> into)
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var p in el.EnumerateObject()) { into.Add(p.Name); Collect(p.Value, into); }
            else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var i in el.EnumerateArray()) Collect(i, into);
        }
    }

    [Fact]
    public void Bundled_RoundTripsThroughTheWriter()
    {
        string json = StockCatalog.ToJson(Bundled.Metadata, Bundled.Entries);
        var again = StockCatalog.FromJson(Utf8(json), "again.json", StockOrigin.Bundled);
        Assert.Empty(again.Issues);
        Assert.Equal(Bundled.Count, again.Count);
        for (int i = 0; i < Bundled.Count; i++)
        {
            var a = Bundled.Entries[i];
            var b = again.Entries[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.WidthMm, b.WidthMm);
            Assert.Equal(a.HeightMm, b.HeightMm);
            Assert.Equal(a.Sensing, b.Sensing);
            Assert.Equal(a.Verified, b.Verified);
            Assert.Equal(a.Sources, b.Sources);
        }
    }

    [Fact]
    public void Lookup_ByIdAndPartNumberIgnoresCaseAndPunctuation()
    {
        var c = Bundled;
        Assert.NotNull(c.Find("ZEBRA-10010041"));
        Assert.Single(c.FindByPartNumber("10010041"));
        Assert.Equal("zebra-10010041", c.FindByPartNumber(" 1001-0041 ")[0].Id);
        Assert.Empty(c.FindByPartNumber(""));
        Assert.Null(c.Find("nope"));
        Assert.Contains("Zebra", c.Brands);
        Assert.Contains("Z-Perform 1000D", c.Families);
    }

    // ---- User files ----------------------------------------------------------------------------------

    [Fact]
    public void User_MissingFolderIsEmptyAndQuiet()
    {
        Assert.Equal(0, StockCatalog.LoadUser(null).Count);
        var c = StockCatalog.LoadUser(Path.Combine(Path.GetTempPath(), "rotula-does-not-exist-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(0, c.Count);
        Assert.Empty(c.Issues);
    }

    [Fact]
    public void User_AddsAndOverridesById()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("mine.json"), FileJson(new[]
        {
            EntryJson("zebra-10010041", "My override of a Zebra part"),
            EntryJson("mine-1", "My own label"),
        }));
        var user = StockCatalog.LoadUser(dir.Path);
        Assert.Empty(user.Issues);
        Assert.Equal(2, user.Count);
        Assert.All(user.Entries, e => Assert.Equal(StockOrigin.User, e.Origin));

        var merged = Bundled.Merged(user);
        Assert.Equal(Bundled.Count + 1, merged.Count);
        Assert.Equal("My override of a Zebra part", merged.Find("zebra-10010041")!.Name);
        Assert.Equal(StockOrigin.User, merged.Find("mine-1")!.Origin);
        // The bundled catalogue itself is unchanged.
        Assert.NotEqual("My override of a Zebra part", Bundled.Find("zebra-10010041")!.Name);
        Assert.Equal(merged.Count, StockCatalog.LoadCombined(dir.Path).Count);
    }

    [Fact]
    public void User_LaterFileOverridesEarlierFile()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.json"), FileJson(new[] { EntryJson("x-1", "From a") }));
        File.WriteAllText(dir.File("b.json"), FileJson(new[] { EntryJson("x-1", "From b") }));
        var user = StockCatalog.LoadUser(dir.Path);
        Assert.Equal("From b", Assert.Single(user.Entries).Name);
    }

    [Fact]
    public void User_BadEntriesAreSkippedWithMessageKeys()
    {
        using var dir = new TempDir();
        var entries = new[]
        {
            EntryJson("ok-1"),
            "{\"name\":\"no id\",\"widthMm\":50,\"heightMm\":25}",
            "42",
            EntryJson("bad id!", "Bad id"),
            "{\"id\":\"mismatch\",\"name\":\"n\",\"widthMm\":50.8,\"heightMm\":25.4,\"widthIn\":2,\"heightIn\":3}",
            "{\"id\":\"neg\",\"name\":\"n\",\"widthMm\":-5,\"heightMm\":25.4}",
            "{\"id\":\"huge\",\"name\":\"n\",\"widthMm\":9000,\"heightMm\":25.4}",
            "{\"id\":\"tiny\",\"name\":\"n\",\"widthMm\":1,\"heightMm\":25.4}",
            "{\"id\":\"nosize\",\"name\":\"n\"}",
            "{\"id\":\"noheight\",\"name\":\"n\",\"widthMm\":50}",
            "{\"id\":\"noname\",\"widthMm\":50,\"heightMm\":25}",
            "{\"id\":\"badtype\",\"name\":\"n\",\"widthMm\":\"fifty\",\"heightMm\":25}",
            "{\"id\":\"http\",\"name\":\"n\",\"widthMm\":50,\"heightMm\":25,\"sourceUrl\":\"http://example.com/x\"}",
            "{\"id\":\"js\",\"name\":\"n\",\"widthMm\":50,\"heightMm\":25,\"sourceUrl\":\"javascript:alert(1)\"}",
            "{\"id\":\"cols\",\"name\":\"n\",\"widthMm\":50,\"heightMm\":25,\"columns\":500}",
            EntryJson("ok-1", "duplicate of the first"),
            EntryJson("ok-2", "Second good one"),
        };
        File.WriteAllText(dir.File("u.json"), FileJson(entries));
        var c = StockCatalog.LoadUser(dir.Path);
        Assert.Equal(new[] { "ok-1", "ok-2" }, c.Entries.Select(e => e.Id).ToArray());
        var keys = c.Issues.Select(i => i.MessageKey).ToList();
        Assert.Contains(StockMessageKeys.EntryMissingId, keys);
        Assert.Contains(StockMessageKeys.EntryNotAnObject, keys);
        Assert.Contains(StockMessageKeys.EntryBadId, keys);
        Assert.Contains(StockMessageKeys.EntryUnitMismatch, keys);
        Assert.Contains(StockMessageKeys.EntryValueOutOfRange, keys);
        Assert.Contains(StockMessageKeys.EntrySizeOutOfRange, keys);
        Assert.Contains(StockMessageKeys.EntryMissingSize, keys);
        Assert.Contains(StockMessageKeys.EntryMissingName, keys);
        Assert.Contains(StockMessageKeys.EntryBadField, keys);
        Assert.Contains(StockMessageKeys.EntryBadUrl, keys);
        Assert.Contains(StockMessageKeys.EntryDuplicateId, keys);
        // Message keys and arguments only: no sentence in any argument list.
        Assert.All(c.Issues, i => Assert.StartsWith("Stocks.", i.MessageKey));
        Assert.All(c.Issues, i => Assert.Equal(ValidationSeverity.Warning, i.Severity));
        var mismatch = c.Issues.First(i => i.MessageKey == StockMessageKeys.EntryUnitMismatch);
        Assert.Equal(new[] { "mismatch", "height", "25.4", "3" }, mismatch.Args.ToArray());
    }

    [Fact]
    public void User_PriceFieldsAreIgnoredAndNeverStored()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("p.json"), FileJson(new[] { EntryJson("p-1", "Tagged", "\"price\":12.5,\"listPrice\":\"$9\",\"msrp\":3") }));
        var c = StockCatalog.LoadUser(dir.Path);
        var e = Assert.Single(c.Entries);
        Assert.Equal(3, c.Issues.Count(i => i.MessageKey == StockMessageKeys.EntryPriceFieldIgnored));
        string written = StockCatalog.ToJson(c.Metadata, c.Entries);
        Assert.DoesNotContain("price", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12.5", written);
        Assert.Equal("Tagged", e.Name);
    }

    [Fact]
    public void User_DamagedFilesNeverThrow()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a-garbage.json"), "this is not json {{{");
        File.WriteAllBytes(dir.File("b-binary.json"), new byte[] { 0, 1, 2, 0xFF, 0xFE, 0x80, 0x00, 0x13 });
        File.WriteAllText(dir.File("c-empty.json"), "");
        File.WriteAllText(dir.File("d-array.json"), "[1,2,3]");
        File.WriteAllText(dir.File("e-newer.json"), FileJson(new[] { EntryJson("n-1") }, schemaVersion: 99));
        File.WriteAllText(dir.File("f-noentries.json"), "{\"metadata\":{\"schemaVersion\":1},\"entries\":[]}");
        File.WriteAllText(dir.File("g-nometa.json"), "{\"entries\":[" + EntryJson("m-1") + "]}");
        File.WriteAllText(dir.File("h-deep.json"), new string('[', 5000) + new string(']', 5000));
        File.WriteAllText(dir.File("i-truncated.json"), FileJson(new[] { EntryJson("t-1") })[..80]);
        File.WriteAllText(dir.File("z-good.json"), FileJson(new[] { EntryJson("good-1") }));
        File.WriteAllText(dir.File("note.txt"), "ignored: not a .json file");

        var c = StockCatalog.LoadUser(dir.Path);
        Assert.Equal("good-1", Assert.Single(c.Entries).Id);
        var keys = c.Issues.Select(i => i.MessageKey).ToList();
        Assert.Contains(StockMessageKeys.FileInvalidJson, keys);
        Assert.Contains(StockMessageKeys.FileUnsupportedSchema, keys);
        Assert.Contains(StockMessageKeys.FileNoEntries, keys);
        // Only file names go into arguments, never full paths.
        Assert.DoesNotContain(c.Issues, i => i.Args.Any(a => a.Contains(dir.Path, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void User_OversizedFileIsRefusedWithoutReadingIt()
    {
        using var dir = new TempDir();
        using (var f = File.Create(dir.File("big.json")))
        {
            f.SetLength(StockLimits.MaxFileBytes + 1);
        }
        var c = StockCatalog.LoadUser(dir.Path);
        Assert.Equal(0, c.Count);
        var issue = Assert.Single(c.Issues);
        Assert.Equal(StockMessageKeys.FileTooLarge, issue.MessageKey);
        Assert.Equal("big.json", issue.Args[0]);
    }

    [Fact]
    public void User_EntryCountIsCapped()
    {
        using var dir = new TempDir();
        var sb = new StringBuilder("{\"metadata\":{\"schemaVersion\":1},\"entries\":[");
        for (int i = 0; i < StockLimits.MaxEntriesPerFile + 5; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"e").Append(i).Append("\",\"name\":\"n\",\"widthMm\":50.8,\"heightMm\":25.4}");
        }
        sb.Append("]}");
        File.WriteAllText(dir.File("many.json"), sb.ToString());
        var c = StockCatalog.LoadUser(dir.Path);
        Assert.Equal(StockLimits.MaxEntriesPerFile, c.Count);
        Assert.Contains(c.Issues, i => i.MessageKey == StockMessageKeys.FileTooManyEntries);
    }

    [Fact]
    public void User_FileWithByteOrderMarkLoads()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("bom.json"), FileJson(new[] { EntryJson("bom-1") }), new UTF8Encoding(true));
        Assert.Equal(1, StockCatalog.LoadUser(dir.Path).Count);
    }

    [Fact]
    public void User_ControlCharactersAreStrippedFromText()
    {
        var c = StockCatalog.FromJson(Utf8(FileJson(new[] { EntryJson("c-1", "Line one\\nLine two\\u0007") })));
        Assert.Equal("Line one Line two", Assert.Single(c.Entries).Name);
    }

    [Fact]
    public void User_TooManyFilesAreCapped()
    {
        using var dir = new TempDir();
        for (int i = 0; i < StockLimits.MaxUserFiles + 3; i++)
            File.WriteAllText(dir.File($"f{i:000}.json"), FileJson(new[] { EntryJson("f" + i) }));
        var c = StockCatalog.LoadUser(dir.Path);
        Assert.Equal(StockLimits.MaxUserFiles, c.Count);
        Assert.Contains(c.Issues, i => i.MessageKey == StockMessageKeys.FileTooManyFiles);
    }

    // ---- Validator -----------------------------------------------------------------------------------

    [Fact]
    public void Validator_AcceptsAGoodContribution()
    {
        string json = FileJson(new[] { EntryJson("good-1", "Good", "\"sourceUrl\":\"https://example.com/p\"") });
        var r = StockCatalogValidator.Validate(Utf8(json));
        Assert.True(r.IsValid, string.Join(";", r.Errors));
        Assert.Equal(1, r.EntryCount);
    }

    [Theory]
    [InlineData("price", "\"price\":1.5", StockMessageKeys.EntryPriceFieldIgnored)]
    [InlineData("noprovenance", "\"sources\":[]", StockMessageKeys.ValidateNoProvenance)]
    [InlineData("http", "\"sourceUrl\":\"http://example.com\"", StockMessageKeys.EntryBadUrl)]
    public void Validator_RejectsBadEntries(string id, string patch, string expectedKey)
    {
        string entry = EntryJson(id);
        if (patch.StartsWith("\"sources\"")) entry = entry.Replace("\"sources\":[\"t\"]", patch);
        else entry = entry.TrimEnd('}') + "," + patch + "}";
        var r = StockCatalogValidator.Validate(Utf8(FileJson(new[] { entry })));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.MessageKey == expectedKey);
    }

    [Fact]
    public void Validator_RejectsDuplicatesUnitMismatchAndTinyLabels()
    {
        var r = StockCatalogValidator.Validate(Utf8(FileJson(new[]
        {
            EntryJson("dup"), EntryJson("DUP"),
            "{\"id\":\"mm\",\"name\":\"n\",\"widthMm\":50.8,\"heightMm\":25.4,\"widthIn\":2,\"heightIn\":1.1,\"sources\":[\"t\"]}",
            "{\"id\":\"tiny\",\"name\":\"n\",\"widthMm\":4,\"heightMm\":25.4,\"sources\":[\"t\"]}",
        })));
        Assert.Contains(r.Errors, e => e.MessageKey == StockMessageKeys.EntryDuplicateId);
        Assert.Contains(r.Errors, e => e.MessageKey == StockMessageKeys.EntryUnitMismatch);
        Assert.Contains(r.Errors, e => e.MessageKey == StockMessageKeys.EntrySizeOutOfRange);
    }

    [Fact]
    public void Validator_VerifiedEntriesNeedBothUnits()
    {
        string entry = "{\"id\":\"v\",\"name\":\"n\",\"widthMm\":50.8,\"heightMm\":25.4,\"sources\":[\"t\"],\"verified\":true}";
        var r = StockCatalogValidator.Validate(Utf8(FileJson(new[] { entry })));
        var err = Assert.Single(r.Errors);
        Assert.Equal(StockMessageKeys.ValidateIncompleteVerified, err.MessageKey);
        Assert.Contains("widthIn", err.Args[1]);
    }

    [Fact]
    public void Validator_NeedsMetadataAndNeverThrowsOnGarbage()
    {
        Assert.False(StockCatalogValidator.Validate(Utf8("{\"entries\":[" + EntryJson() + "]}")).IsValid);
        Assert.False(StockCatalogValidator.Validate(Utf8("garbage")).IsValid);
        Assert.False(StockCatalogValidator.ValidateFile(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json")).IsValid);
        Assert.False(StockCatalogValidator.Validate(Utf8("[]")).IsValid);
    }
}
