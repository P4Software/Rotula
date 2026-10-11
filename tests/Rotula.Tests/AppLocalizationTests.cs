using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rotula.App.Localization;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Tests;

[Collection("App")]
public class AppLocalizationTests : IDisposable
{
    public AppLocalizationTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Rotula.sln not found above the test folder.");
    }

    private static Dictionary<string, string> ReadResx(string language)
    {
        var path = Path.Combine(RepoRoot(), "src", "Rotula.App", "Resources", $"Strings.{language}.resx");
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);
    }

    [Fact]
    public void BothLanguageFiles_HaveExactlyTheSameKeys()
    {
        var en = ReadResx("en").Keys.ToHashSet();
        var es = ReadResx("es").Keys.ToHashSet();
        Assert.Empty(en.Except(es));
        Assert.Empty(es.Except(en));
    }

    [Fact]
    public void NoTranslationIsEmpty_AndPlaceholdersMatchBetweenLanguages()
    {
        var en = ReadResx("en");
        var es = ReadResx("es");
        foreach (var (key, text) in en)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), key);
            Assert.False(string.IsNullOrWhiteSpace(es[key]), key);
            // A message that uses {0} in English must use it in Spanish too, otherwise arguments silently vanish.
            var enHoles = Regex.Matches(text, @"(?<!\{)\{(\d)\}(?!\})").Select(m => m.Groups[1].Value).OrderBy(x => x);
            var esHoles = Regex.Matches(es[key], @"(?<!\{)\{(\d)\}(?!\})").Select(m => m.Groups[1].Value).OrderBy(x => x);
            Assert.True(enHoles.SequenceEqual(esHoles), $"placeholders differ for {key}");
        }
    }

    [Fact]
    public void EveryKeyUsedInXamlOrCode_ExistsInBothLanguages()
    {
        var en = ReadResx("en").Keys.ToHashSet();
        var es = ReadResx("es").Keys.ToHashSet();
        var src = Path.Combine(RepoRoot(), "src", "Rotula.App");
        var used = new HashSet<string>();

        foreach (var xaml in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
            foreach (Match m in Regex.Matches(File.ReadAllText(xaml), @"\{loc:T ([A-Za-z0-9.]+)\}"))
                used.Add(m.Groups[1].Value);

        // Literals in code that look like resource keys. Comment lines are skipped (they quote member names).
        var keyShape = new Regex("\"((?:Prop|Panel|Menu|Tool|Status|Dialog|File|Undo|Element|Validation|Preview|Preset|Import|Print|Image|Issue|Document|Encoding|Layers|About|Ruler|Zpl|Toolbar|Canvas|Common|Crash|Filter|Splitter|Data|Conn|Stock|Pack|Fields)(?:\\.[A-Za-z0-9]+)+)\"");
        // The .rotula serializer lives in Rotula.Files (moved out of the app); its File.Error.* keys must stay covered.
        var filesLib = Path.Combine(RepoRoot(), "src", "Rotula.Files");
        foreach (var cs in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(filesLib, "*.cs", SearchOption.AllDirectories)).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
            foreach (var line in File.ReadAllLines(cs).Where(l => !l.TrimStart().StartsWith("//")))
                foreach (Match m in keyShape.Matches(line))
                    used.Add(m.Groups[1].Value);

        Assert.NotEmpty(used);
        Assert.Empty(used.Where(k => !en.Contains(k)).OrderBy(k => k));
        Assert.Empty(used.Where(k => !es.Contains(k)).OrderBy(k => k));
    }

    [Fact]
    public void EveryMessageKeyEmittedByCore_HasALocalizedIssueMessage()
    {
        var en = ReadResx("en");
        var es = ReadResx("es");
        var core = Path.Combine(RepoRoot(), "src", "Rotula.Core");
        var keys = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"ValidationIssue\(\s*ValidationSeverity\.\w+,\s*""([A-Za-z0-9.]+)"""))
                keys.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"\bErr\(""([A-Za-z0-9.]+)"""))
                keys.Add(m.Groups[1].Value);
            // The resolution converter builds its issues with a literal Dpi.* key.
            foreach (Match m in Regex.Matches(text, @"""(Dpi\.[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*)"""))
                keys.Add(m.Groups[1].Value);
            // The importer and the merge engine report through helper methods, so their keys are found by shape.
            foreach (Match m in Regex.Matches(text, @"""((?:Import|Merge)\.[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*)"""))
                keys.Add(m.Groups[1].Value);
            // The stock library builds its keys through a helper, so they are found by shape too.
            foreach (Match m in Regex.Matches(text, @"""(Stocks\.[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*)"""))
                keys.Add(m.Groups[1].Value);
            // BarcodeValidator builds EAN-13 / UPC-A keys from a name: Barcode.{name}.Length / CheckDigit.
            foreach (Match m in Regex.Matches(text, @"ValidateGtin\(data, \d+, \d+, ""(\w+)"""))
            {
                keys.Add($"Barcode.{m.Groups[1].Value}.Length");
                keys.Add($"Barcode.{m.Groups[1].Value}.CheckDigit");
            }
        }
        Assert.True(keys.Count > 30, "expected to find Core message keys, found " + keys.Count);
        Assert.Empty(keys.Where(k => !en.ContainsKey("Issue." + k)).OrderBy(k => k));
        Assert.Empty(keys.Where(k => !es.ContainsKey("Issue." + k)).OrderBy(k => k));
        Assert.Contains("Dpi.Barcode.ModuleRounded", keys);
        Assert.Contains("Dpi.Raw.NotScaled", keys);
    }

    [Fact]
    public void EveryKeyEmittedByRotulaData_HasTextInBothLanguages()
    {
        var en = ReadResx("en");
        var es = ReadResx("es");
        var keys = new HashSet<string>();
        var registry = Rotula.Data.ProviderRegistry.Default;
        // Providers: name, every option label and help, every choice label (this reaches the Data.Dialect.* ids of the ODBC engine list).
        foreach (var d in registry.Providers)
        {
            keys.Add(d.DisplayNameKey);
            foreach (var o in d.Options)
            {
                keys.Add(o.LabelKey);
                if (o.HelpKey is not null) keys.Add(o.HelpKey);
                foreach (var c in o.Choices) keys.Add(c.LabelKey);
            }
        }
        // Presets: names and hints, from presets.json.
        foreach (var p in Rotula.Data.ProviderPresets.Builtin)
        {
            keys.Add(p.NameKey);
            foreach (var h in p.HintKeys) keys.Add(h);
            if (p.DriverMissingHintKey is not null) keys.Add(p.DriverMissingHintKey);
        }
        // Every SQL dialect id can appear in the ODBC "database behind the driver" list.
        foreach (var dialect in Rotula.Data.SqlDialects.All.Where(x => x.Id != "odbc")) keys.Add("Data.Dialect." + dialect.Id);
        // Constants of the pack and provider layers.
        foreach (var type in new[] { typeof(Rotula.Data.PackKeys), typeof(Rotula.Data.ProviderKeys), typeof(Rotula.Data.DataKeys) })
            foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
                keys.Add((string)f.GetRawConstantValue()!);

        Assert.Contains("Data.Preset.Supabase", keys);
        Assert.Contains("Data.Dialect.jet", keys);
        Assert.Contains("Data.Pack.UnsafeFolder", keys);
        Assert.Empty(keys.Where(k => !en.ContainsKey(k)).OrderBy(k => k));
        Assert.Empty(keys.Where(k => !es.ContainsKey(k)).OrderBy(k => k));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    public void EveryPackWarningKey_FormatsWithItsArguments_InBothLanguages(string lang)
    {
        Loc.Instance.SetLanguage(lang);
        foreach (var f in typeof(Rotula.Data.PackKeys).GetFields().Where(f => f.IsLiteral))
        {
            string key = (string)f.GetRawConstantValue()!;
            if (key.StartsWith("Data.Pack.Trust.", StringComparison.Ordinal)) continue;
            var w = new Rotula.Data.Packs.PackWarning(key, "pack-x", new[] { "pack-x", "1.2" });
            string text = Rotula.App.ViewModels.PackMessages.Describe(w);
            Assert.DoesNotContain("{", text);
            Assert.DoesNotContain("!Data.Pack", text);
        }
    }

    [Fact]
    public void EveryMessageKeyEmittedByPrinting_IsTranslatedInBothLanguages()
    {
        var en = ReadResx("en");
        var es = ReadResx("es");
        var keys = new HashSet<string>();
        // Every failure category has a Print.Error.<Kind> key (PrintError.Create derives the key from the kind).
        foreach (var kind in Enum.GetValues<Rotula.Printing.PrintErrorKind>()) keys.Add("Print.Error." + kind);
        // Profile validation returns literal Print.Profile.*.Range keys; find them in the Printing sources.
        var printing = Path.Combine(RepoRoot(), "src", "Rotula.Printing");
        foreach (var file in Directory.EnumerateFiles(printing, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"in\")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""(Print\.[A-Za-z]+(?:\.[A-Za-z]+)+)"""))
                keys.Add(m.Groups[1].Value);
        Assert.True(keys.Count >= 16, "expected to find the Printing keys, found " + keys.Count);
        Assert.Empty(keys.Where(k => !en.ContainsKey(k)).OrderBy(k => k));
        Assert.Empty(keys.Where(k => !es.ContainsKey(k)).OrderBy(k => k));
        // Messages with holes must not leave an unreplaced {n} when the library supplies fewer arguments than holes.
        foreach (var k in keys.Where(k => k.StartsWith("Print.Error.")))
            Assert.True(Regex.Matches(en[k], @"(?<!\{)\{(\d)\}(?!\})").Count <= 2, k);
    }

    [Fact]
    public void EveryEnumValueShownInTheUi_HasAName_InBothLanguages()
    {
        var en = ReadResx("en").Keys.ToHashSet();
        var es = ReadResx("es").Keys.ToHashSet();
        void Check<T>() where T : struct, Enum
        {
            foreach (var v in Enum.GetValues<T>())
            {
                string key = $"Enum.{typeof(T).Name}.{v}";
                Assert.True(en.Contains(key), key);
                Assert.True(es.Contains(key), key);
            }
        }
        Check<FieldOriginKind>(); Check<FieldRotation>(); Check<GraphicColor>(); Check<LineDirection>(); Check<BarcodeSymbology>();
        Check<HumanReadablePosition>(); Check<Code128Mode>(); Check<QrErrorCorrection>(); Check<TextAlignment>(); Check<ImageDitherMode>();
        Check<LengthUnit>(); Check<PrinterDpi>(); Check<LabelOrientation>(); Check<MediaType>(); Check<MediaTracking>();
        Check<Rotula.Core.Model.DataBarType>(); Check<DiagonalLean>(); Check<GraphicSymbol>(); Check<FieldDirection>(); Check<MsiCheckDigitMode>(); Check<CodablockMode>(); Check<Rotula.Core.Barcodes.BarcodeCategory>();
        Check<Rotula.Core.Merge.CopiesMode>(); Check<Rotula.Core.Merge.InvalidRecordPolicy>(); Check<Rotula.Core.Merge.CounterKind>();
        Check<Rotula.Core.Merge.CounterOverflow>(); Check<Rotula.Printing.PrinterTransport>();
    }

    [Fact]
    public void EveryBarcodeOption_HasALabel_AndEveryPreviewNote_HasATranslation()
    {
        var en = ReadResx("en");
        var es = ReadResx("es");
        // Option rows are built from the catalogue by option name, so a new BarcodeOption without a label would show a missing-key text.
        var directLabels = new Dictionary<Rotula.Core.Barcodes.BarcodeOption, string>
        {
            [Rotula.Core.Barcodes.BarcodeOption.Height] = "Prop.BarHeight", [Rotula.Core.Barcodes.BarcodeOption.Pdf417SecurityLevel] = "Prop.Pdf417Security",
        };
        foreach (var option in Enum.GetValues<Rotula.Core.Barcodes.BarcodeOption>())
        {
            string key = directLabels.TryGetValue(option, out var k) ? k : "Prop." + option;
            Assert.True(en.ContainsKey(key), key);
            Assert.True(es.ContainsKey(key), key);
        }
        foreach (var info in Rotula.Core.Barcodes.BarcodeCatalog.All.Where(i => i.PreviewNote is not null))
        {
            string? key = Rotula.App.Editing.BarcodePreviewNotes.KeyFor(info.Symbology);
            Assert.NotNull(key);
            Assert.True(en.ContainsKey(key!), key);
            Assert.True(es.ContainsKey(key!), key);
        }
        Assert.True(en.ContainsKey("Preview.Note.AztecSpecial") && es.ContainsKey("Preview.Note.AztecSpecial"));
    }

    [Fact]
    public void ResolveLanguage_FollowsWindowsUnlessChosen()
    {
        Assert.Equal("es", Loc.ResolveLanguage("auto", new CultureInfo("es-PA")));
        Assert.Equal("es", Loc.ResolveLanguage(null, new CultureInfo("es-ES")));
        Assert.Equal("en", Loc.ResolveLanguage("auto", new CultureInfo("fr-FR"))); // unsupported -> English
        Assert.Equal("en", Loc.ResolveLanguage("auto", new CultureInfo("en-US")));
        Assert.Equal("en", Loc.ResolveLanguage("en", new CultureInfo("es-ES"))); // explicit choice wins
        Assert.Equal("es", Loc.ResolveLanguage("es", new CultureInfo("en-US")));
        Assert.Equal("en", Loc.ResolveLanguage("klingon", new CultureInfo("fr-FR")));
    }

    [Fact]
    public void SwitchingLanguage_ChangesTextAtOnce_AndRaisesTheIndexerChange()
    {
        var changes = new List<string?>();
        Loc.Instance.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.Equal("Cancel", Loc.Instance["Common.Cancel"]);
        Loc.Instance.SetLanguage("es");
        Assert.Equal("Cancelar", Loc.Instance["Common.Cancel"]);
        Assert.Contains("Item[]", changes);
        Assert.Equal("es", Loc.Instance.LanguageCode);
    }

    [Fact]
    public void MissingKey_IsVisibleInsteadOfBlank()
    {
        Assert.Equal("!No.Such.Key!", Loc.Instance["No.Such.Key"]);
    }

    [Fact]
    public void FormatIssue_UsesTheMessageKeyAndArguments_InTheCurrentLanguage()
    {
        var issue = new ValidationIssue(ValidationSeverity.Error, "Barcode.Ean13.CheckDigit", "id", "7");
        Assert.Contains("7", Loc.FormatIssue(issue));
        Assert.Contains("EAN-13", Loc.FormatIssue(issue));
        Loc.Instance.SetLanguage("es");
        Assert.Contains("dígito de control", Loc.FormatIssue(issue));
    }

    [Fact]
    public void FormatIssue_TranslatesKeywordArguments_AndFallsBackForUnknownKeys()
    {
        var i25 = new ValidationIssue(ValidationSeverity.Error, "Barcode.I25.Length", "id", "odd");
        Assert.Contains("odd", Loc.FormatIssue(i25));
        Loc.Instance.SetLanguage("es");
        Assert.Contains("impar", Loc.FormatIssue(i25));

        var unknown = new ValidationIssue(ValidationSeverity.Warning, "Future.Thing", null);
        Assert.Contains("Future.Thing", Loc.FormatIssue(unknown));
    }

    [Fact]
    public void EnumName_IsTranslated()
    {
        Assert.Equal("Black", Loc.EnumName(GraphicColor.Black));
        Loc.Instance.SetLanguage("es");
        Assert.Equal("Negro", Loc.EnumName(GraphicColor.Black));
    }
}
