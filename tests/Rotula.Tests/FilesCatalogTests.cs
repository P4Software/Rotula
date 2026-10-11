using System.Reflection;
using System.Text.Json;
using Rotula.App.Editing;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>
/// The .rotula file must keep everything the ZPL command catalogue added to the model (new elements, field options, printer settings,
/// the stock id), open files written without them, and treat the new fields as untrusted input.
/// </summary>
public class FilesCatalogTests
{
    /// <summary>A document in which every new setting differs from its default.</summary>
    internal static LabelDocument FullDocument()
    {
        var doc = new LabelDocument
        {
            StockId = "demo-4x6.v2", LabelShift = -20, LabelTop = 15, PrintMode = PrintMode.Peel, PrePeel = true, MirrorImage = true,
            PrintRate = new PrintRateOptions { Speed = 4, SlewSpeed = 6, BackfeedSpeed = 2 }, MediaDarkness = -7.5m,
        };
        doc.FontAliases.Add(new FontAlias('Q', "E:ARIAL.TTF"));
        doc.FontAliases.Add(new FontAlias('7', "R:BODY.FNT"));
        doc.StoredGraphics.Add(new StoredGraphic("R:LOGO.GRF", 2, new byte[] { 0xFF, 0x00, 0xAA, 0x55, 0x0F, 0xF0 }));

        doc.Elements.Add(new TextElement
        {
            Text = "Serial 0001", X = 10, Y = 20, TextBox = new TextBoxOptions { WidthDots = 300, HeightDots = 80 }, Direction = FieldDirection.Vertical, InterCharacterGap = 4,
            FieldNumber = 12, FieldPrompt = "Lot number", EmbedCharacter = '#',
            Clock = new FieldClockOptions { Primary = '%', Secondary = '{', Third = '*' },
            Serial = new PrinterSerialOptions { Kind = PrinterSerialKind.Mask, Mask = "DD%A", IncrementText = "2", Increment = 5, LeadingZeros = true },
        });
        doc.Elements.Add(new DataFieldElement { VariableName = "sku", Serial = new PrinterSerialOptions { Kind = PrinterSerialKind.Number, Increment = -3 }, FieldNumber = 3 });
        doc.Elements.Add(new BarcodeElement
        {
            Symbology = BarcodeSymbology.Code128, Data = "ABC", FieldNumber = 9, FieldPrompt = "Scan", EmbedCharacter = '@',
            Clock = new FieldClockOptions { Primary = '!' }, Serial = new PrinterSerialOptions { Kind = PrinterSerialKind.Number, LeadingZeros = true },
        });
        doc.Elements.Add(new DiagonalLineElement { WidthDots = 120, HeightDots = 90, ThicknessDots = 5, Color = GraphicColor.White, Lean = DiagonalLean.Left, X = 5, Y = 6 });
        doc.Elements.Add(new EllipseElement { WidthDots = 140, HeightDots = 70, ThicknessDots = 4, Color = GraphicColor.White, X = 7, Y = 8 });
        doc.Elements.Add(new SymbolElement { Symbol = GraphicSymbol.Trademark, HeightDots = 33, WidthDots = 22, Rotation = FieldRotation.Rotated90, X = 9, Y = 10 });
        return doc;
    }

    private static LabelDocument RoundTrip(LabelDocument doc, out string json)
    {
        json = RotulaFileSerializer.Serialize(doc);
        return RotulaFileSerializer.Deserialize(json);
    }

    [Fact]
    public void EveryNewSettingAndElement_RoundTrips_AndIsStampedVersionSix()
    {
        var original = FullDocument();
        var copy = RoundTrip(original, out var json);
        // A second serialization equal to the first proves every field the file knows about survived.
        Assert.Equal(json, RotulaFileSerializer.Serialize(copy));
        Assert.Equal(6, JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());

        Assert.Equal("demo-4x6.v2", copy.StockId);
        Assert.Equal(-20, copy.LabelShift);
        Assert.Equal(15, copy.LabelTop);
        Assert.Equal(PrintMode.Peel, copy.PrintMode);
        Assert.True(copy.PrePeel);
        Assert.True(copy.MirrorImage);
        Assert.Equal(6, copy.PrintRate!.SlewSpeed);
        Assert.Equal(-7.5m, copy.MediaDarkness);
        Assert.Equal(new[] { ('Q', "E:ARIAL.TTF"), ('7', "R:BODY.FNT") }, copy.FontAliases.Select(a => (a.Letter, a.Path)));
        var g = Assert.Single(copy.StoredGraphics);
        Assert.Equal("R:LOGO.GRF", g.Path);
        Assert.Equal(2, g.BytesPerRow);
        Assert.Equal(original.StoredGraphics[0].Data, g.Data);

        var t = (TextElement)copy.Elements[0];
        Assert.Equal(300, t.TextBox!.WidthDots);
        Assert.Equal(FieldDirection.Vertical, t.Direction);
        Assert.Equal(4, t.InterCharacterGap);
        Assert.Equal(12, t.FieldNumber);
        Assert.Equal("Lot number", t.FieldPrompt);
        Assert.Equal('#', t.EmbedCharacter);
        Assert.Equal(('%', '{', '*'), (t.Clock!.Primary, t.Clock.Secondary!.Value, t.Clock.Third!.Value));
        Assert.Equal((PrinterSerialKind.Mask, "DD%A", "2", 5L, true), (t.Serial!.Kind, t.Serial.Mask, t.Serial.IncrementText, t.Serial.Increment, t.Serial.LeadingZeros));
        Assert.IsType<DataFieldElement>(copy.Elements[1]);
        Assert.Equal(-3, ((FieldDataElement)copy.Elements[1]).Serial!.Increment);
        Assert.Equal('@', ((BarcodeElement)copy.Elements[2]).EmbedCharacter);
        var d = Assert.IsType<DiagonalLineElement>(copy.Elements[3]);
        Assert.Equal((120, 90, 5, GraphicColor.White, DiagonalLean.Left), (d.WidthDots, d.HeightDots, d.ThicknessDots, d.Color, d.Lean));
        var e = Assert.IsType<EllipseElement>(copy.Elements[4]);
        Assert.Equal((140, 70, 4, GraphicColor.White), (e.WidthDots, e.HeightDots, e.ThicknessDots, e.Color));
        var s = Assert.IsType<SymbolElement>(copy.Elements[5]);
        Assert.Equal((GraphicSymbol.Trademark, 33, 22, FieldRotation.Rotated90), (s.Symbol, s.HeightDots, s.WidthDots, s.Rotation));
    }

    [Fact]
    public void CopyPaste_AndUndoSnapshots_KeepTheNewSettings()
    {
        var original = FullDocument();
        var snapshot = ElementCloner.CloneDocument(original);
        Assert.Equal(RotulaFileSerializer.Serialize(original), RotulaFileSerializer.Serialize(snapshot));
        var pasted = RotulaFileSerializer.DeserializeElements(RotulaFileSerializer.SerializeElements(original.Elements));
        Assert.Equal(original.Elements.Count, pasted.Count);
        Assert.Equal(original.Elements.Select(x => x.GetType()), pasted.Select(x => x.GetType()));
        var restored = new LabelDocument();
        ElementCloner.CopySettings(original, restored);
        Assert.Equal(original.StockId, restored.StockId);
        Assert.Equal(original.StoredGraphics.Count, restored.StoredGraphics.Count);
    }

    [Fact]
    public void ADocumentWithoutTheNewFeatures_KeepsItsOldVersion_AndWritesNoNewFields()
    {
        var doc = new LabelDocument();
        doc.Elements.Add(new TextElement { Text = "x" });
        doc.Elements.Add(new LineElement());
        string json = RotulaFileSerializer.Serialize(doc);
        Assert.Equal(1, JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());
        foreach (var name in new[] { "stockId", "labelShift", "printMode", "fontAliases", "storedGraphics", "textBox", "fieldNumber", "serial", "clock", "interCharacterGap" })
            Assert.DoesNotContain("\"" + name + "\"", json);
    }

    [Fact]
    public void AFileWrittenBeforeTheNewFields_OpensWithDefaults()
    {
        const string json = """{"formatVersion":4,"label":{},"elements":[{"type":"text","text":"hi"},{"type":"barcode","symbology":"Code128","data":"1"}]}""";
        var doc = RotulaFileSerializer.Deserialize(json);
        Assert.Null(doc.StockId);
        Assert.Equal(0, doc.LabelShift);
        Assert.Null(doc.PrintMode);
        Assert.Empty(doc.FontAliases);
        var t = (TextElement)doc.Elements[0];
        Assert.Null(t.TextBox);
        Assert.Equal(FieldDirection.Horizontal, t.Direction);
        Assert.Null(t.Serial);
    }

    [Fact]
    public void AOneOffNewFieldOnAnOldElement_StillNeedsVersionSix()
    {
        var doc = new LabelDocument();
        doc.Elements.Add(new TextElement { Text = "x", FieldNumber = 1 });
        Assert.Equal(6, JsonDocument.Parse(RotulaFileSerializer.Serialize(doc)).RootElement.GetProperty("formatVersion").GetInt32());
        var stock = new LabelDocument { StockId = "a-b" };
        Assert.Equal(6, JsonDocument.Parse(RotulaFileSerializer.Serialize(stock)).RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("../../x")]
    [InlineData("id;drop")]
    [InlineData("")]
    public void ABadStockId_IsDropped(string id)
    {
        string json = JsonSerializer.Serialize(new { formatVersion = 6, label = new { stockId = id }, elements = Array.Empty<object>() });
        Assert.Null(RotulaFileSerializer.Deserialize(json).StockId);
    }

    [Fact]
    public void ATooLongStockId_IsDropped()
    {
        string json = JsonSerializer.Serialize(new { formatVersion = 6, label = new { stockId = new string('a', 65) }, elements = Array.Empty<object>() });
        Assert.Null(RotulaFileSerializer.Deserialize(json).StockId);
        json = JsonSerializer.Serialize(new { formatVersion = 6, label = new { stockId = new string('a', 64) }, elements = Array.Empty<object>() });
        Assert.Equal(64, RotulaFileSerializer.Deserialize(json).StockId!.Length);
    }

    [Fact]
    public void HostileLabelSettings_AreIgnoredOrCapped()
    {
        string big = Convert.ToBase64String(new byte[3 * 1024 * 1024]);
        var graphics = Enumerable.Range(0, 8).Select(i => new { path = $"R:G{i}.GRF", bytesPerRow = 4, data = big }).ToList<object>();
        graphics.Add(new { path = "../../evil", bytesPerRow = 4, data = "AAAA" });
        graphics.Add(new { path = "R:BAD.GRF", bytesPerRow = 4, data = "!!!not base64!!!" });
        graphics.Add(new { path = "R:ODD.GRF", bytesPerRow = 7, data = "AAAAAAAA" });
        graphics.Add(new { path = "R:ZERO.GRF", bytesPerRow = 0, data = "AAAA" });
        var aliases = new List<object> { new { letter = "ab", path = "E:X.TTF" }, new { letter = "Q", path = new string('x', 500) }, new { letter = "\u0001", path = "E:X.TTF" }, new { letter = "Z", path = "E:OK.TTF" } };
        string json = JsonSerializer.Serialize(new
        {
            formatVersion = 6,
            label = new { printMode = "Explode", fontAliases = aliases, storedGraphics = graphics },
            elements = Array.Empty<object>(),
        });
        var doc = RotulaFileSerializer.Deserialize(json);
        Assert.Null(doc.PrintMode);
        Assert.Equal(new[] { 'Z' }, doc.FontAliases.Select(a => a.Letter));
        // Only graphics that fit in the 16 MB budget are kept (5 of the 8 big ones at 3 MB each); the broken ones are dropped.
        Assert.InRange(doc.StoredGraphics.Count, 1, 5);
        Assert.True(doc.StoredGraphics.Sum(g => (long)g.Data.Length) <= 16 * 1024 * 1024);
        Assert.DoesNotContain(doc.StoredGraphics, g => g.Path.Contains("EVIL") || g.Path.Contains("BAD") || g.Path.Contains("ODD") || g.Path.Contains("ZERO"));
    }

    [Fact]
    public void HostileFieldSettings_AreIgnoredOrCapped()
    {
        string huge = new('a', 100_000);
        string json = JsonSerializer.Serialize(new
        {
            formatVersion = 6,
            label = new { },
            elements = new object[]
            {
                new
                {
                    type = "text", text = "x", direction = "Sideways", fieldPrompt = huge, embedCharacter = "ab",
                    clock = new { primary = "", secondary = "xy" },
                    serial = new { kind = "Explode", mask = "D", increment = 1 },
                },
                new
                {
                    type = "text", text = "y", fieldPrompt = "ok", embedCharacter = "#", clock = new { primary = "%", secondary = "toolong" },
                    serial = new { kind = "Mask", mask = huge, incrementText = huge, increment = long.MaxValue },
                },
            },
        });
        var doc = RotulaFileSerializer.Deserialize(json);
        var a = (TextElement)doc.Elements[0];
        Assert.Equal(FieldDirection.Horizontal, a.Direction);
        Assert.Null(a.FieldPrompt);
        Assert.Null(a.EmbedCharacter);
        Assert.Null(a.Clock);
        Assert.Null(a.Serial);
        var b = (TextElement)doc.Elements[1];
        Assert.Equal("ok", b.FieldPrompt);
        Assert.Equal('#', b.EmbedCharacter);
        Assert.Null(b.Clock!.Secondary);
        Assert.Equal(PrinterSerialKind.Mask, b.Serial!.Kind);
        Assert.Equal(string.Empty, b.Serial.Mask);
        Assert.Equal(string.Empty, b.Serial.IncrementText);
    }

    [Fact]
    public void ANewElementWithAnUnknownEnumName_IsACleanCorruptError()
    {
        const string json = """{"formatVersion":6,"label":{},"elements":[{"type":"symbol","symbol":"Skull"}]}""";
        Assert.Equal("File.Error.Corrupt", Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(json)).MessageKey);
    }

    // ------------------------------------------------------------------------------------------------ the guard

    /// <summary>Model properties that are deliberately not stored under their own name, with the reason.</summary>
    private static readonly Dictionary<string, string> Renamed = new()
    {
        ["ImageElement.Source"] = "stored as SourceWidth, SourceHeight and SourcePixels",
        ["LabelDocument.Elements"] = "stored as the elements list of the file",
        ["LabelDocument.Width"] = "stored as a measure (value and unit)",
        ["LabelDocument.Height"] = "stored as a measure (value and unit)",
        ["LabelDocument.Variables"] = "stored inside the label object",
        ["LabelMargins.Left"] = "stored as a measure", ["LabelMargins.Top"] = "stored as a measure",
        ["LabelMargins.Right"] = "stored as a measure", ["LabelMargins.Bottom"] = "stored as a measure",
        ["LabelDocument.Encoding"] = "stored as CiCode and CodePage",
    };

    /// <summary>Properties that are not part of a saved label on purpose. Add to this list only with a reason.</summary>
    private static readonly HashSet<string> Transient = new()
    {
        // none at the moment: everything on the model is saved
    };

    private static IEnumerable<PropertyInfo> SavedProperties(Type model) =>
        model.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.DeclaringType == model || p.DeclaringType!.IsAbstract)
            // a property is part of the saved state when it can be set, or when it is a list that is filled in place
            .Where(p => p.SetMethod is { IsPublic: true } || (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>)));

    private static bool HasFileProperty(Type dto, string name)
    {
        for (var t = dto; t is not null; t = t.BaseType)
            if (t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase | BindingFlags.DeclaredOnly) is not null) return true;
        return false;
    }

    public static IEnumerable<object[]> ModelTypes()
    {
        var pairs = new (Type Model, Type Dto)[]
        {
            (typeof(LabelDocument), typeof(LabelDto)), (typeof(LabelMargins), typeof(MarginsDto)), (typeof(FontSpec), typeof(FontDto)),
            (typeof(PrintOptions), typeof(PrintOptionsDto)), (typeof(PrintRateOptions), typeof(PrintRateDto)), (typeof(FontAlias), typeof(FontAliasDto)),
            (typeof(StoredGraphic), typeof(StoredGraphicDto)), (typeof(LabelVariable), typeof(VariableDto)),
            (typeof(LabelElement), typeof(ElementDto)), (typeof(FieldDataElement), typeof(FieldDataElementDto)), (typeof(TextElement), typeof(TextElementDto)),
            (typeof(DataFieldElement), typeof(DataFieldElementDto)), (typeof(BarcodeElement), typeof(BarcodeElementDto)), (typeof(BoxElement), typeof(BoxElementDto)),
            (typeof(LineElement), typeof(LineElementDto)), (typeof(CircleElement), typeof(CircleElementDto)), (typeof(DiagonalLineElement), typeof(DiagonalLineElementDto)),
            (typeof(EllipseElement), typeof(EllipseElementDto)), (typeof(SymbolElement), typeof(SymbolElementDto)), (typeof(ImageElement), typeof(ImageElementDto)),
            (typeof(RawZplElement), typeof(RawZplElementDto)), (typeof(RecallPlaceholderElement), typeof(RecallPlaceholderElementDto)),
            (typeof(FontChoice), typeof(FontChoiceDto)), (typeof(TextBlockOptions), typeof(TextBlockDto)), (typeof(TextBoxOptions), typeof(TextBoxDto)),
            (typeof(PrinterSerialOptions), typeof(PrinterSerialDto)), (typeof(FieldClockOptions), typeof(FieldClockDto)),
            // Variable definitions (format version 7): every option of every kind must reach the file.
            (typeof(Rotula.Core.Variables.DateVariableOptions), typeof(DateVariableDto)), (typeof(Rotula.Core.Variables.DateOffset), typeof(DateOffsetDto)),
            (typeof(Rotula.Core.Variables.CounterVariableOptions), typeof(CounterVariableDto)), (typeof(Rotula.Core.Variables.PromptVariableOptions), typeof(PromptVariableDto)),
            (typeof(Rotula.Core.Variables.ComputedVariableOptions), typeof(ComputedVariableDto)),
        };
        return pairs.Select(p => new object[] { p.Model, p.Dto });
    }

    [Theory, MemberData(nameof(ModelTypes))]
    public void EveryPublicSettableModelProperty_IsSaved_OrExplicitlyTransient(Type model, Type dto)
    {
        var barcodeOptions = Enum.GetNames<BarcodeOption>().ToHashSet();
        var missing = new List<string>();
        foreach (var p in SavedProperties(model))
        {
            string key = model.Name + "." + p.Name;
            if (Renamed.ContainsKey(key) || Transient.Contains(key)) continue;
            // The later barcode options travel through BarcodeElement.ExportExtraOptions, one entry per BarcodeOption member.
            if (model == typeof(BarcodeElement) && barcodeOptions.Contains(p.Name)) continue;
            if (!HasFileProperty(dto, p.Name)) missing.Add(key);
        }
        Assert.True(missing.Count == 0,
            "These model properties are not in the .rotula file, so they would be silently lost on save: " + string.Join(", ", missing) +
            ". Add them to the DTO and the serializer, or list them as transient with a reason.");
    }

    [Fact]
    public void EveryElementClass_HasAFileType_AndACloneCase()
    {
        var elementTypes = typeof(LabelElement).Assembly.GetTypes().Where(t => typeof(LabelElement).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(elementTypes.Count >= 12);
        foreach (var type in elementTypes)
        {
            var element = (LabelElement)Activator.CreateInstance(type)!;
            var dto = RotulaFileSerializer.ToDto(element);
            Assert.Equal(type, RotulaFileSerializer.FromDto(dto).GetType());
            Assert.Equal(type, ElementCloner.Clone(element).GetType());
        }
    }
}
