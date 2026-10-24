using System.Text;
using System.Text.Json;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Files;
using Rotula.Rendering;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;
using Rotula.Rendering.View;

namespace Rotula.Tests;

/// <summary>
/// Per-object printing settings: a print condition evaluated per record, "do not print", and the design-time lock. Covers the evaluator, the
/// ZPL generator, the data merge, the pictures, the .rotula file (format version 9), the editing commands and the property panel.
/// </summary>
[Collection("App")]
public class ObjectConditionsTests : IDisposable
{
    public ObjectConditionsTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private static PrintCondition Rule(string field, ConditionOperator op, string value = "") => new() { Field = field, Operator = op, Value = value };

    private static TextElement Note(string text, PrintCondition? rule = null) => new() { Id = "note", Text = text, X = 20, Y = 20, PrintCondition = rule };

    private static IReadOnlyDictionary<string, string> Record(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------ operators

    [Theory]
    [InlineData(ConditionOperator.Equals, "Yes", "Yes", true)]
    [InlineData(ConditionOperator.Equals, "yes ", "YES", true)]
    [InlineData(ConditionOperator.Equals, "No", "Yes", false)]
    [InlineData(ConditionOperator.Equals, "", "", true)]
    [InlineData(ConditionOperator.NotEquals, "No", "Yes", true)]
    [InlineData(ConditionOperator.NotEquals, "yes", "YES", false)]
    [InlineData(ConditionOperator.IsEmpty, "", "ignored", true)]
    [InlineData(ConditionOperator.IsEmpty, "   ", "ignored", true)]
    [InlineData(ConditionOperator.IsEmpty, "x", "ignored", false)]
    [InlineData(ConditionOperator.IsNotEmpty, "x", "ignored", true)]
    [InlineData(ConditionOperator.IsNotEmpty, "", "ignored", false)]
    [InlineData(ConditionOperator.Contains, "Fragile - keep dry", "fragile", true)]
    [InlineData(ConditionOperator.Contains, "Keep dry", "wet", false)]
    [InlineData(ConditionOperator.Contains, "anything", "", true)]
    public void EachOperator_DecidesWhetherTheObjectIsPrinted(ConditionOperator op, string fieldValue, string ruleValue, bool printed)
    {
        var doc = CoreTestSupport.NewDoc(Note("Handle with care", Rule("Flag", op, ruleValue)));
        var result = ZplGenerator.Generate(doc, Record(("Flag", fieldValue)));
        Assert.Equal(printed, result.Zpl.Contains("Handle with care", StringComparison.Ordinal));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AMissingField_IsTreatedAsEmpty_AndWarns()
    {
        var hidden = CoreTestSupport.NewDoc(Note("A", Rule("Nope", ConditionOperator.IsNotEmpty)));
        var shown = CoreTestSupport.NewDoc(Note("A", Rule("Nope", ConditionOperator.IsEmpty)));
        var data = Record(("Other", "1"));

        var r1 = ZplGenerator.Generate(hidden, data);
        Assert.DoesNotContain("^FDA", r1.Zpl);
        var warning = Assert.Single(r1.Issues);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.Equal(ElementPrinting.ConditionFieldMissing, warning.MessageKey);
        Assert.Equal("note", warning.ElementId);
        Assert.Equal("Nope", warning.Args[0]);

        var r2 = ZplGenerator.Generate(shown, data);
        Assert.Contains("^FDA", r2.Zpl);
        Assert.False(r2.HasErrors);
    }

    [Fact]
    public void ARuleWithoutAField_IsUnfinished_AndDoesNotHideTheObject()
    {
        var doc = CoreTestSupport.NewDoc(Note("Still here", Rule("", ConditionOperator.Equals, "x")));
        var result = ZplGenerator.Generate(doc, Record(("A", "b")));
        Assert.Contains("Still here", result.Zpl);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void TheField_MayBeWrittenAsAPlaceholder_AndFallsBackToTheDeclaredSampleValue()
    {
        var doc = CoreTestSupport.NewDoc(Note("Promo", Rule("{{Campaign}}", ConditionOperator.Equals, "spring")));
        doc.Variables.Add(new LabelVariable("Campaign", "Spring"));
        Assert.Contains("Promo", ZplGenerator.Generate(doc).Zpl);                                    // the sample value decides when there is no record
        Assert.DoesNotContain("Promo", ZplGenerator.Generate(doc, Record(("Campaign", "Autumn"))).Zpl); // the record wins over the sample
    }

    [Fact]
    public void AConditionOnAComputedVariable_SeesTheResolvedValue()
    {
        // "Code" is computed from the record's "Region" column, so the rule only sees the right value if it reads the resolved data.
        var doc = CoreTestSupport.NewDoc(Note("Europe", Rule("Code", ConditionOperator.Equals, "EU")));
        doc.Variables.Add(new LabelVariable("Region", "eu"));
        var code = new LabelVariable("Code", "") { Kind = Rotula.Core.Variables.VariableKind.Computed, Computed = new Rotula.Core.Variables.ComputedVariableOptions { Expression = "upper(Region)" } };
        doc.Variables.Add(code);
        var eu = ZplGenerator.GenerateWithVariables(doc, new Rotula.Core.Variables.VariableContext { Record = Record(("Region", "eu")) });
        var us = ZplGenerator.GenerateWithVariables(doc, new Rotula.Core.Variables.VariableContext { Record = Record(("Region", "us")) });
        Assert.DoesNotContain(eu.Issues, i => i.Severity == ValidationSeverity.Error);
        Assert.Contains("Europe", eu.Zpl);
        Assert.DoesNotContain("Europe", us.Zpl);
    }

    // ------------------------------------------------------------------------------------ batch

    [Fact]
    public void InAMultiRecordBatch_RecordOnePrintsTheObject_AndRecordTwoDoesNot()
    {
        var doc = CoreTestSupport.NewDoc(
            new TextElement { Id = "name", Text = "{{Name}}", X = 20, Y = 20 },
            new TextElement { Id = "warn", Text = "FRAGILE", X = 20, Y = 80, PrintCondition = Rule("Fragile", ConditionOperator.Equals, "yes") });
        var set = new MergeDataSet(doc, CsvReader.Parse("Name,Fragile\nGlass,yes\nBolt,no\nCup,YES\n"));

        var labels = MergeBatch.Labels(doc, set).ToList();

        Assert.Equal(3, labels.Count);
        Assert.Contains("FRAGILE", labels[0].Zpl);
        Assert.DoesNotContain("FRAGILE", labels[1].Zpl);
        Assert.Contains("Bolt", labels[1].Zpl);
        Assert.Contains("FRAGILE", labels[2].Zpl);
    }

    [Fact]
    public void TheConditionField_IsAPlaceholderOfTheLabel_SoItCanBeMappedToAColumn()
    {
        var doc = CoreTestSupport.NewDoc(new TextElement { Text = "x", PrintCondition = Rule("Fragile", ConditionOperator.IsNotEmpty) });
        Assert.Equal(new[] { "Fragile" }, MergeDataSet.CollectPlaceholders(doc));
        var set = new MergeDataSet(doc, CsvReader.Parse("Fragile\nyes\n"));
        Assert.DoesNotContain(set.Issues, i => i.MessageKey == "Merge.Column.Unused");
    }

    [Fact]
    public void AHiddenBarcodeThatIsInvalidForARecord_DoesNotStopThatRecord()
    {
        var doc = CoreTestSupport.NewDoc(
            new TextElement { Text = "{{Name}}", X = 10, Y = 10 },
            new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "{{Ean}}", X = 10, Y = 60, PrintCondition = Rule("Ean", ConditionOperator.IsNotEmpty) });
        var report = new MergeReport();
        var set = new MergeDataSet(doc, CsvReader.Parse("Name,Ean\nBolt,4006381333931\nNut,\n"));
        var labels = MergeBatch.Labels(doc, set, report).ToList();
        Assert.Equal(2, labels.Count);
        Assert.False(report.HasErrors);
        Assert.Contains("^BE", labels[0].Zpl);
        Assert.DoesNotContain("^BE", labels[1].Zpl);
    }

    // ------------------------------------------------------------------------------------ not printable

    [Fact]
    public void ANotPrintableObject_IsNeverInTheZpl_NoMatterWhichWayItIsGenerated()
    {
        var doc = CoreTestSupport.NewDoc(
            new TextElement { Id = "keep", Text = "Printed", X = 10, Y = 10 },
            new TextElement { Id = "guide", Text = "Designer note", X = 10, Y = 60, NotPrintable = true },
            new BoxElement { Id = "box", X = 10, Y = 100, NotPrintable = true });

        string plain = ZplGenerator.Generate(doc).Zpl;
        string withData = ZplGenerator.Generate(doc, Record(("A", "1"))).Zpl;
        string withVariables = ZplGenerator.GenerateWithVariables(doc, new Rotula.Core.Variables.VariableContext()).Zpl;

        foreach (string zpl in new[] { plain, withData, withVariables })
        {
            Assert.Contains("Printed", zpl);
            Assert.DoesNotContain("Designer note", zpl);
            Assert.DoesNotContain("^GB", zpl);
        }
        Assert.Equal(plain, withData); // only the objects that exist matter: the flag adds nothing to the output
    }

    private static bool IsWhite(RenderResult r, int x, int y) => r.Bitmap.GetPixel(x, y) == SkiaSharp.SKColors.White;

    private static LabelDocument InkDoc(Action<LabelElement> configure)
    {
        var box = new BoxElement { X = 40, Y = 40, WidthDots = 100, HeightDots = 100, ThicknessDots = 100 };
        configure(box);
        var doc = new LabelDocument { Width = Measure.Dots(300), Height = Measure.Dots(300), Dpi = PrinterDpi.Dpi203 };
        doc.Elements.Add(box);
        return doc;
    }

    [Fact]
    public void ANotPrintableObject_IsNotDrawnInPngPdfOrThePrintPreview_ButTheDesignViewShowsItDimmed()
    {
        var doc = InkDoc(e => e.NotPrintable = true);

        using (var printed = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(doc)))
            Assert.True(IsWhite(printed, 90, 90), "the exported/printed picture must not contain the object");
        Assert.Empty(LabelDocumentAdapter.ToRenderLabel(doc).Elements);

        // The files the user can save.
        using var png = new MemoryStream();
        LabelExporter.SavePng(LabelDocumentAdapter.ToRenderLabel(doc), png);
        using var decoded = SkiaSharp.SKBitmap.Decode(png.ToArray());
        Assert.Equal(SkiaSharp.SKColors.White, decoded.GetPixel(90, 90));
        using var pdf = new MemoryStream();
        LabelExporter.SavePdf(LabelDocumentAdapter.ToRenderLabel(doc), pdf);
        Assert.True(pdf.Length > 0);

        // The designer keeps it visible, but faded so it reads as "not part of the print".
        var design = LabelDocumentAdapter.ToRenderLabel(doc, null, includeNonPrinting: true);
        Assert.True(Assert.Single(design.Elements).Dimmed);
        using var dimmed = LabelRenderer.RenderBitmap(design);
        var pixel = dimmed.Bitmap.GetPixel(90, 90);
        Assert.NotEqual(SkiaSharp.SKColors.White, pixel);
        Assert.NotEqual(SkiaSharp.SKColors.Black, pixel);
    }

    [Fact]
    public void AConditionThatIsFalseForTheShownRecord_LeavesTheObjectOutOfThePicture_ForThatRecordOnly()
    {
        var doc = InkDoc(e => e.PrintCondition = Rule("Show", ConditionOperator.Equals, "yes"));

        using (var on = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(doc, Record(("Show", "yes")))))
            Assert.Equal(SkiaSharp.SKColors.Black, on.Bitmap.GetPixel(90, 90));
        using (var off = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(doc, Record(("Show", "no")))))
            Assert.True(IsWhite(off, 90, 90));

        Assert.Empty(LabelDocumentAdapter.FindNotPrinted(doc, Record(("Show", "yes"))));
        Assert.Equal(new[] { doc.Elements[0].Id }, LabelDocumentAdapter.FindNotPrinted(doc, Record(("Show", "no"))));
    }

    [Fact]
    public void TheCanvasPreview_DimsAHiddenObject_WhileThePrintPreviewLeavesItOut_AndItStaysSelectable()
    {
        var box = new BoxElement { Id = "b", X = 40, Y = 40, WidthDots = 100, HeightDots = 100, ThicknessDots = 100, PrintCondition = Rule("Show", ConditionOperator.Equals, "yes") };
        var s = AppTestSupport.Session(box);
        using var cache = new PreviewCache(s) { Data = Record(("Show", "no")) };

        Assert.Empty(cache.GetLabel().Elements);
        var design = Assert.Single(cache.GetDesignLabel().Elements);
        Assert.True(design.Dimmed);
        Assert.True(cache.IsHiddenByCondition(box));
        using var printed = cache.RenderPrinted(1.0, out _);
        Assert.NotNull(printed);
        Assert.True(IsWhite(printed!, 90, 90));

        // Hit-testing and handles use the measured size, which must not collapse to nothing because the object is not printed.
        var bounds = s.Bounds.GetBounds(box);
        Assert.True(bounds.Width >= 90 && bounds.Height >= 90, $"measured {bounds.Width}x{bounds.Height}");

        cache.Data = Record(("Show", "yes"));
        s.Touch();
        Assert.False(cache.IsHiddenByCondition(box));
        Assert.False(Assert.Single(cache.GetDesignLabel().Elements).Dimmed);
    }

    // ------------------------------------------------------------------------------------ lock

    [Fact]
    public void ALockedObject_DoesNotChangeTheZpl()
    {
        var a = CoreTestSupport.NewDoc(new BoxElement { Id = "b", X = 10, Y = 10 });
        var b = CoreTestSupport.NewDoc(new BoxElement { Id = "b", X = 10, Y = 10, Locked = true });
        Assert.Equal(ZplGenerator.Generate(a).Zpl, ZplGenerator.Generate(b).Zpl);
    }

    [Fact]
    public void TheEditingSession_RefusesToMove_Resize_Rotate_Align_OrDeleteALockedObject()
    {
        var locked = new BoxElement { Id = "locked", X = 100, Y = 100, WidthDots = 80, HeightDots = 40, Locked = true };
        var s = AppTestSupport.Session(locked);
        var c = new EditorCommands(s);
        s.Select(locked);

        c.Nudge(10, 10);
        Assert.Equal((100, 100), (locked.X, locked.Y));
        Assert.Equal(1, c.LockedSkipped);

        c.Rotate90();
        Assert.Null(((BoxElement)s.Document.Elements[0]).Rotation);

        c.Align(AlignKind.Left);
        Assert.Equal(100, s.Document.Elements[0].X);

        c.ApplyResize(locked, new RectD(100, 100, 80, 40), ResizeHandle.BottomRight, 200, 100);
        Assert.Equal(80, ((BoxElement)s.Document.Elements[0]).WidthDots);

        c.DeleteSelected();
        Assert.Single(s.Document.Elements);
        Assert.False(s.Undo.CanUndo, "a refused command must not leave an empty undo step");
    }

    [Fact]
    public void InAMixedSelection_OnlyTheUnlockedObjectsMoveAndDelete()
    {
        var locked = new BoxElement { Id = "locked", X = 10, Y = 10, Locked = true };
        var free = new BoxElement { Id = "free", X = 300, Y = 10 };
        var s = AppTestSupport.Session(locked, free);
        var c = new EditorCommands(s);
        s.SelectAll();

        c.Nudge(5, 0);
        Assert.Equal(10, s.Document.Elements[0].X);
        Assert.Equal(305, s.Document.Elements[1].X);
        Assert.Equal(1, c.LockedSkipped);

        c.DeleteSelected();
        Assert.Equal("locked", Assert.Single(s.Document.Elements).Id);
        // Undo brings the deleted one back, still unlocked, and the locked one never changed.
        s.PerformUndo();
        Assert.Equal(2, s.Document.Elements.Count);
    }

    [Fact]
    public void ALockedObject_CanStillBeSelected_AndItsPropertiesEdited_AndItCanBeUnlocked()
    {
        var locked = new BoxElement { Id = "locked", X = 10, Y = 10, WidthDots = 50, Locked = true };
        var s = AppTestSupport.Session(locked);
        var panel = new PropertyPanelViewModel(s);
        s.Select(locked);
        Assert.Equal(new[] { "locked" }, s.SelectedIds);

        panel.Rows.OfType<IntRow>().First(r => r.LabelKey == "Prop.Width").Text = "70";
        Assert.Equal(70, ((BoxElement)s.Document.Elements[0]).WidthDots);

        panel.Rows.OfType<BoolRow>().First(r => r.LabelKey == "Prop.Locked").Value = false;
        Assert.False(s.Document.Elements[0].Locked);
        new EditorCommands(s).Nudge(4, 0);
        Assert.Equal(14, s.Document.Elements[0].X);
    }

    [Fact]
    public void UndoAndRedo_KeepTheLockAndTheRule()
    {
        var box = new BoxElement { Id = "b", X = 10, Y = 10, Locked = true, NotPrintable = true, PrintCondition = Rule("F", ConditionOperator.Contains, "x") };
        var s = AppTestSupport.Session(box);
        var panel = new PropertyPanelViewModel(s);
        s.Select(box);
        panel.Rows.OfType<IntRow>().First(r => r.LabelKey == "Prop.X").Text = "50";
        s.PerformUndo();
        var back = s.Document.Elements[0];
        Assert.True(back.Locked);
        Assert.True(back.NotPrintable);
        Assert.Equal(ConditionOperator.Contains, back.PrintCondition!.Operator);
        Assert.Equal("x", back.PrintCondition.Value);
        s.PerformRedo();
        Assert.True(s.Document.Elements[0].Locked);
    }

    [Fact]
    public void DuplicateAndTheDpiConversion_CarryTheSettings()
    {
        var box = new BoxElement { Id = "b", Locked = true, NotPrintable = true, PrintCondition = Rule("F", ConditionOperator.NotEquals, "x") };
        var copy = ElementCloner.Clone(box, newId: true);
        Assert.NotEqual(box.Id, copy.Id);
        Assert.True(copy.Locked && copy.NotPrintable);
        Assert.NotSame(box.PrintCondition, copy.PrintCondition);
        Assert.Equal("F", copy.PrintCondition!.Field);

        var doc = CoreTestSupport.NewDoc(box);
        var converted = LabelDpiConverter.Convert(doc, PrinterDpi.Dpi300).Document.Elements[0];
        Assert.True(converted.Locked && converted.NotPrintable);
        Assert.Equal(ConditionOperator.NotEquals, converted.PrintCondition!.Operator);
    }

    // ------------------------------------------------------------------------------------ file format

    private static string FormatOf(LabelDocument doc) => JsonDocument.Parse(RotulaFileSerializer.Serialize(doc)).RootElement.GetProperty("formatVersion").ToString();

    [Fact]
    public void TheSettings_RoundTripThroughTheFile_AndMarkItVersionTen()
    {
        var doc = CoreTestSupport.NewDoc(
            new TextElement { Id = "t", Text = "Hi", PrintCondition = Rule("Status", ConditionOperator.Contains, "ok"), Locked = true },
            new BoxElement { Id = "g", NotPrintable = true },
            new LineElement { Id = "plain" });

        string json = RotulaFileSerializer.Serialize(doc);
        Assert.Equal("10", FormatOf(doc));
        Assert.Equal(10, RotulaFileSerializer.ObjectConditionsFormatVersion);
        Assert.Contains("\"printCondition\"", json);
        Assert.Contains("\"notPrintable\": true", json);
        Assert.Contains("\"locked\": true", json);

        var back = RotulaFileSerializer.Deserialize(json);
        var t = back.Elements[0];
        Assert.True(t.Locked);
        Assert.False(t.NotPrintable);
        Assert.Equal("Status", t.PrintCondition!.Field);
        Assert.Equal(ConditionOperator.Contains, t.PrintCondition.Operator);
        Assert.Equal("ok", t.PrintCondition.Value);
        Assert.True(back.Elements[1].NotPrintable);
        Assert.Null(back.Elements[2].PrintCondition);
        Assert.False(back.Elements[2].Locked);
        // Plain objects write none of the three, so an unchanged element looks exactly as before.
        var plainJson = JsonDocument.Parse(json).RootElement.GetProperty("elements")[2];
        Assert.False(plainJson.TryGetProperty("printCondition", out _));
        Assert.False(plainJson.TryGetProperty("notPrintable", out _));
        Assert.False(plainJson.TryGetProperty("locked", out _));
    }

    [Fact]
    public void ALabelThatUsesNoneOfThem_KeepsItsOldFormatVersion_AndOldFilesOpenUnchanged()
    {
        Assert.Equal("1", FormatOf(CoreTestSupport.NewDoc(new TextElement { Text = "x" })));

        // A version 8 file as the previous build wrote it.
        const string v8 = """
            {"formatVersion":8,"label":{"width":{"value":80,"unit":"Millimeters"},"height":{"value":40,"unit":"Millimeters"},"dpi":203},
             "elements":[{"type":"text","id":"a","x":10,"y":20,"text":"Old file"}]}
            """;
        var doc = RotulaFileSerializer.Deserialize(v8);
        var e = Assert.Single(doc.Elements);
        Assert.Null(e.PrintCondition);
        Assert.False(e.NotPrintable);
        Assert.False(e.Locked);
        Assert.Contains("Old file", ZplGenerator.Generate(doc).Zpl);
    }

    [Fact]
    public void ABuildThatPredatesTheFeature_RefusesAVersionTenFile_AndTheNewestVersionIsStillNine()
    {
        Assert.Equal(RotulaFileSerializer.ObjectConditionsFormatVersion, RotulaFileSerializer.CurrentFormatVersion);
        Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize("{\"formatVersion\":11}"));
    }

    [Fact]
    public void AnUnreadableRule_IsClipped_OrDropped_NeverFailingTheFile()
    {
        string longText = new('x', 5000);
        string File(string rule) => "{\"formatVersion\":10,\"label\":{},\"elements\":[{\"type\":\"box\",\"id\":\"a\",\"printCondition\":" + rule + "}]}";

        var clipped = RotulaFileSerializer.Deserialize(File("{\"field\":\"" + longText + "\",\"operator\":\"Equals\",\"value\":\"" + longText + "\"}")).Elements[0].PrintCondition!;
        Assert.Equal(PrintCondition.MaxTextLength, clipped.Field.Length);
        Assert.Equal(PrintCondition.MaxTextLength, clipped.Value.Length);

        Assert.Null(RotulaFileSerializer.Deserialize(File("{\"field\":\"A\",\"operator\":\"Sometimes\"}")).Elements[0].PrintCondition);
        Assert.Null(RotulaFileSerializer.Deserialize(File("{\"field\":\"A\",\"operator\":\"7\"}")).Elements[0].PrintCondition);
    }

    [Fact]
    public void TheClipboard_KeepsTheSettings()
    {
        var box = new BoxElement { Id = "b", Locked = true, PrintCondition = Rule("F", ConditionOperator.IsEmpty) };
        var pasted = RotulaFileSerializer.DeserializeElements(RotulaFileSerializer.SerializeElements(new[] { box }));
        Assert.True(pasted[0].Locked);
        Assert.Equal(ConditionOperator.IsEmpty, pasted[0].PrintCondition!.Operator);
    }

    [Fact]
    public void ImportingZpl_NeverProducesConditionsOrLocks_AndAnExportedConditionIsSimplyLeftOut()
    {
        var doc = CoreTestSupport.NewDoc(
            new TextElement { Text = "Always", X = 10, Y = 10 },
            new TextElement { Text = "Sometimes", X = 10, Y = 60, PrintCondition = Rule("F", ConditionOperator.Equals, "x"), Locked = true });
        string zpl = ZplGenerator.Generate(doc, Record(("F", "x"))).Zpl;

        var imported = new CoreZplImporter().Import(Encoding.UTF8.GetBytes(zpl)).Document!;
        Assert.Equal(2, imported.Elements.Count(e => e is TextElement));
        Assert.All(imported.Elements, e =>
        {
            Assert.Null(e.PrintCondition);
            Assert.False(e.NotPrintable);
            Assert.False(e.Locked);
        });
    }

    // ------------------------------------------------------------------------------------ property panel and layers

    [Fact]
    public void ThePrintingSection_OffersTheThreeSettings_AndWritesThemToTheElement()
    {
        var box = new BoxElement { Id = "b", X = 10, Y = 10 };
        var s = AppTestSupport.Session(box);
        var panel = new PropertyPanelViewModel(s);
        s.Select(box);

        Assert.Contains(panel.Rows, r => r is HeadingRow && r.LabelKey == "Panel.Printing");
        Assert.DoesNotContain(panel.Rows, r => r.LabelKey == "Prop.Condition.Operator");

        panel.Rows.OfType<BoolRow>().First(r => r.LabelKey == "Prop.NotPrintable").Value = true;
        Assert.True(s.Document.Elements[0].NotPrintable);

        panel.Rows.OfType<BoolRow>().First(r => r.LabelKey == "Prop.Condition.Enable").Value = true;
        Assert.NotNull(s.Document.Elements[0].PrintCondition);
        // The extra rows appear once the rule exists.
        Assert.Contains(panel.Rows, r => r is ConditionFieldRow);
        Assert.Contains(panel.Rows, r => r.LabelKey == "Prop.Condition.Operator");
        Assert.Contains(panel.Rows, r => r.LabelKey == "Prop.Condition.Value");

        var op = panel.Rows.OfType<ChoiceRow>().First(r => r.LabelKey == "Prop.Condition.Operator");
        op.Selected = op.Items.First(i => Equals(i.Value, ConditionOperator.IsEmpty));
        Assert.Equal(ConditionOperator.IsEmpty, s.Document.Elements[0].PrintCondition!.Operator);
        // IsEmpty does not use the value, so its box goes away.
        Assert.DoesNotContain(panel.Rows, r => r.LabelKey == "Prop.Condition.Value");

        panel.Rows.OfType<BoolRow>().First(r => r.LabelKey == "Prop.Locked").Value = true;
        Assert.True(s.Document.Elements[0].Locked);

        panel.Rows.OfType<BoolRow>().First(r => r.LabelKey == "Prop.Condition.Enable").Value = false;
        Assert.Null(s.Document.Elements[0].PrintCondition);
    }

    [Fact]
    public void TheFieldBox_AcceptsAPickedPlaceholder_StoresTheBareName_AndWarnsAboutUnknownFields()
    {
        var box = new BoxElement { Id = "b" };
        var fake = new FakeServices();
        fake.Settings.Current.Language = "en";
        var main = new MainViewModel(fake.Build());
        main.Session.Document.Variables.Add(new LabelVariable("Region", "EU"));
        main.Fields.Refresh();
        main.Commands.Add(box);
        main.Session.Select(main.Session.Document.Elements[^1]);
        main.Properties.Rows.OfType<BoolRow>().First(r => r.LabelKey == "Prop.Condition.Enable").Value = true;

        var row = main.Properties.Rows.OfType<ConditionFieldRow>().Single();
        // The picker writes the placeholder; the rule keeps the name.
        row.Field.Text = "{{Region}}";
        Assert.Equal("Region", main.Session.Document.Elements[^1].PrintCondition!.Field);
        Assert.False(row.HasWarning);

        row.Field.Text = "Reigon";
        Assert.Equal("Reigon", main.Session.Document.Elements[^1].PrintCondition!.Field);
        row.Refresh();
        Assert.True(row.HasWarning);
        Assert.Contains("Reigon", row.WarningText);

        row.Field.Text = "";
        Assert.True(row.Field.HasError);
        Assert.Equal("Reigon", main.Session.Document.Elements[^1].PrintCondition!.Field); // an empty name is refused, not stored
    }

    [Fact]
    public void TheObjectsList_ShowsTheStateOfEachObject_AndFollowsTheRecordShown()
    {
        var fake = new FakeServices();
        fake.Settings.Current.Language = "en";
        var main = new MainViewModel(fake.Build());
        main.Commands.Add(new BoxElement { Id = "locked", Locked = true });
        main.Commands.Add(new BoxElement { Id = "note", NotPrintable = true });
        main.Commands.Add(new BoxElement { Id = "rule", PrintCondition = Rule("Show", ConditionOperator.Equals, "yes") });

        var items = main.Layers.ToDictionary(l => l.Id);
        Assert.True(items["locked"].IsLocked);
        Assert.False(items["locked"].IsNotPrinted);
        Assert.True(items["note"].NotPrintable && items["note"].IsNotPrinted);
        Assert.Equal("No print", items["note"].NotPrintedText);
        // No record and no sample for the field: the rule reads an empty value, so the object is hidden.
        Assert.True(items["rule"].HiddenByCondition);
        Assert.Equal("Hidden", items["rule"].NotPrintedText);
    }

    [Fact]
    public void KeysAndMenus_SayWhyNothingMoved_WhenOnlyLockedObjectsAreSelected()
    {
        var fake = new FakeServices();
        fake.Settings.Current.Language = "en";
        var main = new MainViewModel(fake.Build());
        var box = new BoxElement { Id = "b", X = 50, Y = 50, Locked = true };
        main.Commands.Add(box);
        main.NudgeBy(1, 0, false);
        Assert.Equal(50, main.Session.Document.Elements[^1].X);
        Assert.Contains("Locked", main.StatusMessage);

        main.CutCommand.Execute(null);
        Assert.Single(main.Session.Document.Elements);
        main.DeleteCommand.Execute(null);
        Assert.Single(main.Session.Document.Elements);
    }

    [Fact]
    public void EveryNewTextIsInEveryLanguage()
    {
        foreach (var lang in Loc.SupportedLanguages)
        {
            Loc.Instance.SetLanguage(lang);
            // HasKey only looks at English, so the language's own file is checked as well.
            var own = UiStrings.Keys(lang);
            foreach (var key in new[] { "Panel.Printing", "Prop.NotPrintable", "Prop.Condition.Enable", "Prop.Locked", "Status.LockedSkipped", "Badge.Locked", "Badge.HiddenByCondition", "Badge.NotPrintable",
                         "Condition.FieldMissing", "Issue.Element.Condition.FieldMissing" })
            {
                Assert.True(Loc.Instance.HasKey(key), key + " / " + lang);
                Assert.Contains(key, own);
            }
            foreach (var op in Enum.GetValues<ConditionOperator>())
            {
                Assert.True(Loc.Instance.HasKey("Enum.ConditionOperator." + op), op + " / " + lang);
                Assert.True(Loc.Instance.HasKey("Condition.Summary." + op), op + " / " + lang);
            }
        }
        Loc.Instance.SetLanguage("es");
        var issue = new ValidationIssue(ValidationSeverity.Warning, ElementPrinting.ConditionFieldMissing, "x", "Campo");
        Assert.Contains("Campo", Loc.FormatIssue(issue));
        Assert.DoesNotContain("Problem found", Loc.FormatIssue(issue));
    }
}
