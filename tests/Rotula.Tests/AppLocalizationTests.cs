using System.Globalization;
using System.Text.RegularExpressions;
using System.ComponentModel;
using Rotula.App.Localization;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Tests;

[Collection("App")]
public class AppLocalizationTests : IDisposable
{
    public AppLocalizationTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private static string RepoRoot() => UiStrings.RepoRoot();

    private static IReadOnlyDictionary<string, string> ReadResx(string language) => UiStrings.Read(language);

    /// <summary>A format hole such as {0}, {1:N0} or {2,-5}; escaped braces ({{ and }}) are removed before matching.</summary>
    private static readonly Regex Hole = new(@"\{\d+(?:,-?\d+)?(?::[^{}]*)?\}");

    private static List<string> Holes(string text) =>
        Hole.Matches(text.Replace("{{", string.Empty).Replace("}}", string.Empty)).Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void EveryResourceFile_IsAShippedLanguage_AndEveryShippedLanguageHasAFile()
    {
        // A Strings.xx.resx nobody can pick, or a menu language with no file, is a mistake either way.
        Assert.Equal(UiStrings.Languages.Order(StringComparer.Ordinal), UiStrings.FileLanguages().Order(StringComparer.Ordinal));
        Assert.Equal("en", UiStrings.Languages[0]);
    }

    [Theory]
    [MemberData(nameof(UiStrings.TranslationData), MemberType = typeof(UiStrings))]
    public void EveryTranslation_HasExactlyTheEnglishKeys(string language)
    {
        var en = UiStrings.Keys("en");
        var other = UiStrings.Keys(language);
        Assert.Empty(en.Except(other).Order(StringComparer.Ordinal));
        Assert.Empty(other.Except(en).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(UiStrings.LanguageData), MemberType = typeof(UiStrings))]
    public void NoTranslationIsEmpty_AndPlaceholdersMatchEnglish(string language)
    {
        var en = ReadResx("en");
        var other = ReadResx(language);
        var empty = other.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ToList();
        Assert.Empty(empty);
        // A message that uses {0} in English must use it in every language too, otherwise arguments silently vanish, and a
        // hole English does not have would throw a FormatException at run time.
        var differ = en.Where(p => other.TryGetValue(p.Key, out var t) && !Holes(p.Value).SequenceEqual(Holes(t))).Select(p => p.Key).ToList();
        Assert.Empty(differ);
    }

    [Theory]
    [MemberData(nameof(UiStrings.TranslationData), MemberType = typeof(UiStrings))]
    public void AccessKeys_AreKeptInEveryTranslation(string language)
    {
        // "_File" gives Alt+F; a translation must keep exactly one access key where English has one, and add none.
        static int Marks(string s) => Regex.Matches(s, "(?<!_)_(?!_)").Count;
        var en = ReadResx("en");
        var other = ReadResx(language);
        Assert.Empty(en.Where(p => Marks(p.Value) != Marks(other[p.Key])).Select(p => p.Key));
        // The top-level menus must still get distinct letters, or Alt+letter would stop at the first one.
        var menus = new[] { "Menu.File", "Menu.Edit", "Menu.View", "Menu.Help" }
            .Select(k => char.ToLowerInvariant(other[k][other[k].IndexOf('_') + 1])).ToList();
        Assert.Equal(menus.Count, menus.Distinct().Count());
    }

    [Fact]
    public void CanadianFrench_UsesNonBreakingSpacesBeforeDoublePunctuation_AndInsideGuillemets()
    {
        // OQLF typography: "Exemple : {0}" and « texte » must not break at the end of a line, so the space is a no-break one.
        var fr = ReadResx("fr-CA");
        Assert.Empty(fr.Where(p => Regex.IsMatch(p.Value, @"[\w»)] [:;!?](\s|$)")).Select(p => p.Key));
        Assert.Empty(fr.Where(p => p.Value.Contains("« ") || p.Value.Contains(" »")).Select(p => p.Key));
        Assert.Contains(" :", fr["QrContent.Hint.Text"]);
    }

    [Fact]
    public void Translations_DoNotTranslateTheProductName()
    {
        foreach (var lang in UiStrings.Translations)
            Assert.Contains("Rotula", ReadResx(lang)["About.Title"]);
    }

    [Fact]
    public void EveryKeyUsedInXamlOrCode_ExistsInEveryLanguage()
    {
        var src = Path.Combine(RepoRoot(), "src", "Rotula.App");
        var used = new HashSet<string>();

        foreach (var xaml in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
            foreach (Match m in Regex.Matches(File.ReadAllText(xaml), @"\{loc:T ([A-Za-z0-9.]+)\}"))
                used.Add(m.Groups[1].Value);

        // Literals in code that look like resource keys. Comment lines are skipped (they quote member names).
        var keyShape = new Regex("\"((?:Prop|Panel|Menu|Tool|Status|Dialog|File|Undo|Element|Validation|Preview|Preset|Import|Print|Image|Issue|Document|Encoding|Layers|About|Ruler|Zpl|Toolbar|Canvas|Common|Crash|Filter|Splitter|Data|Conn|Stock|Pack|Fields|Station|Policy|Update)(?:\\.[A-Za-z0-9]+)+)\"");
        // The .rotula serializer lives in Rotula.Files (moved out of the app); its File.Error.* keys must stay covered.
        var filesLib = Path.Combine(RepoRoot(), "src", "Rotula.Files");
        foreach (var cs in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(filesLib, "*.cs", SearchOption.AllDirectories)).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
            foreach (var line in File.ReadAllLines(cs).Where(l => !l.TrimStart().StartsWith("//")))
                foreach (Match m in keyShape.Matches(line))
                    used.Add(m.Groups[1].Value);

        Assert.NotEmpty(used);
        Assert.Empty(UiStrings.MissingInAnyLanguage(used));
    }

    [Fact]
    public void EveryMessageKeyEmittedByCore_HasALocalizedIssueMessage()
    {
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
        Assert.Empty(UiStrings.MissingInAnyLanguage(keys.Select(k => "Issue." + k)));
        Assert.Contains("Dpi.Barcode.ModuleRounded", keys);
        Assert.Contains("Dpi.Raw.NotScaled", keys);
    }

    [Fact]
    public void EveryKeyEmittedByRotulaData_HasTextInEveryLanguage()
    {
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
        Assert.Empty(UiStrings.MissingInAnyLanguage(keys));
    }

    [Theory]
    [MemberData(nameof(UiStrings.LanguageData), MemberType = typeof(UiStrings))]
    public void EveryPackWarningKey_FormatsWithItsArguments_InEveryLanguage(string lang)
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
    public void EveryMessageKeyEmittedByPrinting_IsTranslatedInEveryLanguage()
    {
        var en = ReadResx("en");
        var keys = new HashSet<string>();
        // Every failure category has a Print.Error.<Kind> key (PrintError.Create derives the key from the kind).
        foreach (var kind in Enum.GetValues<Rotula.Printing.PrintErrorKind>()) keys.Add("Print.Error." + kind);
        // Profile validation returns literal Print.Profile.*.Range keys; find them in the Printing sources.
        var printing = Path.Combine(RepoRoot(), "src", "Rotula.Printing");
        foreach (var file in Directory.EnumerateFiles(printing, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"in\")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""(Print\.[A-Za-z]+(?:\.[A-Za-z]+)+)"""))
                keys.Add(m.Groups[1].Value);
        Assert.True(keys.Count >= 16, "expected to find the Printing keys, found " + keys.Count);
        Assert.Empty(UiStrings.MissingInAnyLanguage(keys));
        // Messages with holes must not leave an unreplaced {n} when the library supplies fewer arguments than holes.
        foreach (var k in keys.Where(k => k.StartsWith("Print.Error.")))
            Assert.True(Regex.Matches(en[k], @"(?<!\{)\{(\d)\}(?!\})").Count <= 2, k);
    }

    [Fact]
    public void EveryEnumValueShownInTheUi_HasAName_InEveryLanguage()
    {
        var keys = new List<string>();
        void Check<T>() where T : struct, Enum
        {
            foreach (var v in Enum.GetValues<T>()) keys.Add($"Enum.{typeof(T).Name}.{v}");
        }
        Check<FieldOriginKind>(); Check<FieldRotation>(); Check<GraphicColor>(); Check<LineDirection>(); Check<BarcodeSymbology>();
        Check<HumanReadablePosition>(); Check<Code128Mode>(); Check<QrErrorCorrection>(); Check<TextAlignment>(); Check<ImageDitherMode>();
        Check<LengthUnit>(); Check<PrinterDpi>(); Check<LabelOrientation>(); Check<MediaType>(); Check<MediaTracking>();
        Check<Rotula.Core.Model.DataBarType>(); Check<DiagonalLean>(); Check<GraphicSymbol>(); Check<FieldDirection>(); Check<MsiCheckDigitMode>(); Check<CodablockMode>(); Check<Rotula.Core.Barcodes.BarcodeCategory>();
        Check<Rotula.Core.Merge.CopiesMode>(); Check<Rotula.Core.Merge.InvalidRecordPolicy>(); Check<Rotula.Core.Merge.CounterKind>();
        Check<Rotula.Core.Merge.CounterOverflow>(); Check<Rotula.Printing.PrinterTransport>();
        Check<Rotula.Printing.PrinterCondition>(); Check<Rotula.Printing.PrinterPrintMode>();
        Assert.Empty(UiStrings.MissingInAnyLanguage(keys));
    }

    [Fact]
    public void EveryBarcodeOption_HasALabel_AndEveryPreviewNote_HasATranslation()
    {
        var keys = new List<string> { "Preview.Note.AztecSpecial" };
        // Option rows are built from the catalogue by option name, so a new BarcodeOption without a label would show a missing-key text.
        var directLabels = new Dictionary<Rotula.Core.Barcodes.BarcodeOption, string>
        {
            [Rotula.Core.Barcodes.BarcodeOption.Height] = "Prop.BarHeight", [Rotula.Core.Barcodes.BarcodeOption.Pdf417SecurityLevel] = "Prop.Pdf417Security",
        };
        foreach (var option in Enum.GetValues<Rotula.Core.Barcodes.BarcodeOption>())
        {
            keys.Add(directLabels.TryGetValue(option, out var k) ? k : "Prop." + option);
        }
        foreach (var info in Rotula.Core.Barcodes.BarcodeCatalog.All.Where(i => i.PreviewNote is not null))
        {
            string? key = Rotula.App.Editing.BarcodePreviewNotes.KeyFor(info.Symbology);
            Assert.NotNull(key);
            keys.Add(key!);
        }
        Assert.Empty(UiStrings.MissingInAnyLanguage(keys));
    }

    [Fact]
    public void ResolveLanguage_FollowsWindowsUnlessChosen()
    {
        Assert.Equal("es", Loc.ResolveLanguage("auto", new CultureInfo("es-PA")));
        Assert.Equal("es", Loc.ResolveLanguage(null, new CultureInfo("es-ES")));
        Assert.Equal("en", Loc.ResolveLanguage("auto", new CultureInfo("de-DE"))); // unsupported -> English
        Assert.Equal("en", Loc.ResolveLanguage("auto", new CultureInfo("ja-JP")));
        Assert.Equal("en", Loc.ResolveLanguage("auto", new CultureInfo("en-US")));
        Assert.Equal("en", Loc.ResolveLanguage("auto", CultureInfo.InvariantCulture));
        Assert.Equal("en", Loc.ResolveLanguage("en", new CultureInfo("es-ES"))); // explicit choice wins
        Assert.Equal("es", Loc.ResolveLanguage("es", new CultureInfo("en-US")));
        Assert.Equal("en", Loc.ResolveLanguage("klingon", new CultureInfo("de-DE")));
    }

    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")] // one Portuguese ships: Portugal gets it rather than English
    [InlineData("pt-AO", "pt-BR")]
    [InlineData("pt", "pt-BR")]
    [InlineData("fr-CA", "fr-CA")]
    [InlineData("fr-FR", "fr-CA")] // one French ships: France, Belgium and Switzerland get it rather than English
    [InlineData("fr-BE", "fr-CA")]
    [InlineData("fr-CH", "fr-CA")]
    [InlineData("fr", "fr-CA")]
    [InlineData("es-MX", "es")]
    [InlineData("es-419", "es")]
    [InlineData("en-GB", "en")]
    [InlineData("it-IT", "en")]
    public void ResolveLanguage_MapsTheWindowsLanguage_ToTheClosestShippedOne(string windows, string expected)
    {
        Assert.Equal(expected, Loc.ResolveLanguage("auto", new CultureInfo(windows)));
        Assert.Equal(expected, Loc.ResolveLanguage(null, new CultureInfo(windows)));
        Assert.Contains(expected, Loc.SupportedLanguages);
    }

    [Theory]
    [InlineData("pt-BR", "fr-FR", "pt-BR")]
    [InlineData("fr-CA", "pt-BR", "fr-CA")]
    [InlineData("pt-BR", "en-US", "pt-BR")]
    [InlineData("fr-CA", "es-ES", "fr-CA")]
    [InlineData("PT-br", "en-US", "pt-BR")] // a hand-edited settings.json in another case still counts
    [InlineData("fr-ca", "en-US", "fr-CA")]
    public void ResolveLanguage_AnExplicitChoiceWins(string setting, string windows, string expected)
    {
        Assert.Equal(expected, Loc.ResolveLanguage(setting, new CultureInfo(windows)));
    }

    [Theory]
    [InlineData("en", "fr-CA", "en")]
    [InlineData("es", "pt-BR", "es")]
    [InlineData("auto", "pt-BR", "pt-BR")]
    [InlineData("auto", "fr-CA", "fr-CA")]
    // Codes that were never saved by any version fall back to "follow Windows", as before.
    [InlineData("pt", "en-US", "en")]
    [InlineData("fr", "en-US", "en")]
    [InlineData("", "fr-FR", "fr-CA")]
    public void ResolveLanguage_SettingsSavedBeforeTheNewLanguages_KeepTheirMeaning(string setting, string windows, string expected)
    {
        Assert.Equal(expected, Loc.ResolveLanguage(setting, new CultureInfo(windows)));
    }

    [Theory]
    [InlineData("pt-BR", "Cancelar")]
    [InlineData("fr-CA", "Annuler")]
    [InlineData("es", "Cancelar")]
    [InlineData("en", "Cancel")]
    public void EveryShippedLanguage_IsServedFromItsOwnResources(string language, string cancel)
    {
        Loc.Instance.SetLanguage(language);
        Assert.Equal(language, Loc.Instance.LanguageCode);
        Assert.Equal(language, Loc.Instance.Culture.Name);
        Assert.Equal(cancel, Loc.Instance["Common.Cancel"]);
        // Every key comes back from the language's own file, not the English fallback.
        var expected = UiStrings.Read(language);
        foreach (var key in new[] { "Menu.File", "Menu.Edit", "Menu.Language", "About.Title" })
            Assert.Equal(expected[key], Loc.Instance[key]);
    }

    [Fact]
    public void NumbersAndDates_FollowTheChosenLanguage()
    {
        // Loc.Get formats with the language's culture: Brazil and Canada write a decimal comma.
        Loc.Instance.SetLanguage("pt-BR");
        Assert.Equal("1,5", string.Format(Loc.Instance.Culture, "{0}", 1.5));
        Assert.Contains("1,5", Loc.Get("Qr.Size.Module", 1.5, 203));
        Loc.Instance.SetLanguage("fr-CA");
        Assert.Contains("1,5", Loc.Get("Qr.Size.Module", 1.5, 203));
        Loc.Instance.SetLanguage("en");
        Assert.Contains("1.5", Loc.Get("Qr.Size.Module", 1.5, 203));
    }

    [Fact]
    public void SwitchingToAndFromTheNewLanguages_RaisesTheIndexerChange()
    {
        var changes = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => changes.Add(e.PropertyName);
        Loc.Instance.PropertyChanged += handler;
        try
        {
            Loc.Instance.SetLanguage("fr-CA");
            Assert.Equal("Annuler", Loc.Instance["Common.Cancel"]);
            Loc.Instance.SetLanguage("pt-BR");
            Assert.Equal("Cancelar", Loc.Instance["Common.Cancel"]);
            Assert.Equal(2, changes.Count(c => c == "Item[]"));
        }
        finally
        {
            Loc.Instance.PropertyChanged -= handler;
        }
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
