using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Tests;

/// <summary>The barcode part of the designer: catalogue-driven property rows, grouped picker, preview notes, safe defaults, copies.</summary>
[Collection("App")]
public class AppBarcodeUiTests : IDisposable
{
    public AppBarcodeUiTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private static T Row<T>(PropertyPanelViewModel p, string labelKey) where T : PropertyRow =>
        p.Rows.OfType<T>().First(r => r.LabelKey == labelKey);

    private static (PropertyPanelViewModel Panel, EditorSession Session, BarcodeElement Element) Open(BarcodeElement element)
    {
        var s = AppTestSupport.Session(element);
        var p = new PropertyPanelViewModel(s);
        s.Select(element);
        return (p, s, element);
    }

    /// <summary>Label keys of the rows that edit barcode options (everything between the picker and the end).</summary>
    private static List<string> OptionKeys(PropertyPanelViewModel p) =>
        p.Rows.SkipWhile(r => r.LabelKey != "Prop.Data").Skip(1).Select(r => r.LabelKey).ToList();

    [Theory]
    [InlineData(BarcodeSymbology.Codabar, new[] { "Prop.BarHeight", "Prop.ModuleWidth", "Prop.WideBarRatio", "Prop.HumanReadable", "Prop.HumanReadablePosition", "Prop.CodabarStart", "Prop.CodabarStop" })]
    [InlineData(BarcodeSymbology.Msi, new[] { "Prop.BarHeight", "Prop.ModuleWidth", "Prop.WideBarRatio", "Prop.HumanReadable", "Prop.HumanReadablePosition", "Prop.MsiCheckDigit", "Prop.MsiCheckDigitInText" })]
    [InlineData(BarcodeSymbology.Ean8, new[] { "Prop.BarHeight", "Prop.ModuleWidth", "Prop.HumanReadable", "Prop.HumanReadablePosition" })]
    [InlineData(BarcodeSymbology.MaxiCode, new[] { "Prop.MaxiCodeMode", "Prop.MaxiCodeSymbolNumber", "Prop.MaxiCodeSymbolTotal" })]
    public void EachSymbology_ShowsExactlyItsOwnOptions(BarcodeSymbology symbology, string[] expected)
    {
        var (p, _, _) = Open(SymbSupport.Default(symbology));
        Assert.Equal(expected, OptionKeys(p));
    }

    [Fact]
    public void TheOptionRows_AlwaysMatchTheCatalogue_ForEverySymbology()
    {
        foreach (var info in BarcodeCatalog.All)
        {
            var (p, _, e) = Open(SymbSupport.Default(info.Symbology));
            // DataBar segments only exist for the Expanded variant, so the omnidirectional default has one row fewer.
            int expected = info.Options.Count - (info.Supports(BarcodeOption.DataBarSegmentsPerRow) && e.DataBarType != DataBarType.Expanded ? 1 : 0);
            Assert.True(OptionKeys(p).Count == expected, $"{info.Symbology}: {string.Join(",", OptionKeys(p))}");
            Assert.All(OptionKeys(p), key => Assert.True(Loc.Instance.HasKey(key), key));
        }
    }

    [Fact]
    public void DataBarSegments_AppearOnlyForTheExpandedVariant()
    {
        var (p, s, e) = Open(SymbSupport.Default(BarcodeSymbology.DataBar));
        Assert.DoesNotContain(p.Rows, r => r.LabelKey == "Prop.DataBarSegmentsPerRow");
        Row<ChoiceRow>(p, "Prop.DataBarType").Selected = Row<ChoiceRow>(p, "Prop.DataBarType").Items.First(i => Equals(i.Value, DataBarType.Expanded));
        Assert.Equal(DataBarType.Expanded, ((BarcodeElement)s.Document.Elements[0]).DataBarType);
        Assert.Contains(p.Rows, r => r.LabelKey == "Prop.DataBarSegmentsPerRow");
    }

    [Fact]
    public void ThePicker_GroupsByFamily_AndShowsTheZplCommand()
    {
        var (p, _, _) = Open(SymbSupport.Default(BarcodeSymbology.Code128));
        var picker = Row<ChoiceRow>(p, "Prop.Symbology");
        Assert.Equal(BarcodeCatalog.All.Count, picker.Items.Count);
        Assert.Equal(new[] { "Linear", "2D matrix", "Stacked", "Postal" }, picker.Items.Select(i => i.Group!).Distinct().ToArray());
        Assert.Contains(picker.Items, i => i.Display.Contains("(^BK)") && i.Display.Contains("Codabar"));
        Assert.Equal(4, picker.View.Groups!.Count);
        // Every symbology keeps its place in exactly one group.
        Assert.Equal(picker.Items.Count, picker.Items.Select(i => i.Value).Distinct().Count());
    }

    [Fact]
    public void ThePickerGroupNames_AreTranslated()
    {
        Loc.Instance.SetLanguage("es");
        var (p, _, _) = Open(SymbSupport.Default(BarcodeSymbology.Code128));
        var groups = Row<ChoiceRow>(p, "Prop.Symbology").Items.Select(i => i.Group!).Distinct().ToArray();
        Assert.Equal(new[] { "Lineales", "Matriciales 2D", "Apilados", "Postales" }, groups);
    }

    [Fact]
    public void MaxiCode_HidesRotation_ButAStaleRotationCanStillBeCleared()
    {
        var (p, s, e) = Open(SymbSupport.Default(BarcodeSymbology.MaxiCode));
        Assert.DoesNotContain(p.Rows, r => r.LabelKey is "Prop.Rotation" or "Prop.Rotation.Unsupported");

        var old = new BarcodeElement { Symbology = BarcodeSymbology.MaxiCode, Data = BarcodeCatalog.Get(BarcodeSymbology.MaxiCode).ExampleData, Rotation = FieldRotation.Rotated90 };
        var (p2, s2, _) = Open(old);
        var row = Row<ChoiceRow>(p2, "Prop.Rotation.Unsupported");
        row.Selected = row.Items.First(i => i.Value is null);
        Assert.Null(s2.Document.Elements[0].Rotation);
        Assert.DoesNotContain(p2.Rows, r => r.LabelKey == "Prop.Rotation.Unsupported");
    }

    [Fact]
    public void ChangingTheRotation_OfARotatableBarcode_DoesNotRebuildThePanel()
    {
        var (p, s, e) = Open(SymbSupport.Default(BarcodeSymbology.Code128));
        var picker = Row<ChoiceRow>(p, "Prop.Symbology");
        var rotation = Row<ChoiceRow>(p, "Prop.Rotation");
        rotation.Selected = rotation.Items.First(i => Equals(i.Value, FieldRotation.Rotated90));
        Assert.Equal(FieldRotation.Rotated90, s.Document.Elements[0].Rotation);
        // A rebuild would replace the row objects and take the keyboard focus away from the drop-down being used.
        Assert.Same(picker, Row<ChoiceRow>(p, "Prop.Symbology"));
        Assert.Same(rotation, Row<ChoiceRow>(p, "Prop.Rotation"));
    }

    [Fact]
    public void ChangingTheSymbology_ReplacesSampleData_ButNeverTheUsersText()
    {
        var (p, s, e) = Open(new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "123456789" });
        var picker = Row<ChoiceRow>(p, "Prop.Symbology");
        picker.Selected = picker.Items.First(i => Equals(i.Value, BarcodeSymbology.Ean8));
        Assert.Equal(BarcodeCatalog.Get(BarcodeSymbology.Ean8).ExampleData, ((BarcodeElement)s.Document.Elements[0]).Data);

        var (p2, s2, _) = Open(new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "{{sku}} / mine" });
        var picker2 = Row<ChoiceRow>(p2, "Prop.Symbology");
        picker2.Selected = picker2.Items.First(i => Equals(i.Value, BarcodeSymbology.Code39));
        Assert.Equal("{{sku}} / mine", ((BarcodeElement)s2.Document.Elements[0]).Data);
    }

    [Fact]
    public void ChangingTheSymbology_FixesWhatTheNewCodeCannotHonour_InOneUndo()
    {
        var (p, s, e) = Open(new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "A1", Rotation = FieldRotation.Rotated90, HumanReadable = false, Columns = 20, Rows = 50 });
        var picker = Row<ChoiceRow>(p, "Prop.Symbology");
        picker.Selected = picker.Items.First(i => Equals(i.Value, BarcodeSymbology.MaxiCode));
        var maxi = (BarcodeElement)s.Document.Elements[0];
        Assert.Null(maxi.Rotation);
        Assert.Equal(0, maxi.Columns);

        s.PerformUndo();
        var back = (BarcodeElement)s.Document.Elements[0];
        Assert.Equal(BarcodeSymbology.Code128, back.Symbology);
        Assert.Equal(FieldRotation.Rotated90, back.Rotation);
        Assert.Equal(20, back.Columns);

        var (p2, s2, _) = Open(new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "A1", HumanReadable = false });
        var picker2 = Row<ChoiceRow>(p2, "Prop.Symbology");
        picker2.Selected = picker2.Items.First(i => Equals(i.Value, BarcodeSymbology.Logmars));
        Assert.True(((BarcodeElement)s2.Document.Elements[0]).HumanReadable);
    }

    [Fact]
    public void AnOutOfRangeOption_ExplainsItselfWithTheCoreMessage()
    {
        var (p, s, e) = Open(SymbSupport.Default(BarcodeSymbology.Tlc39));
        var row = Row<IntRow>(p, "Prop.Tlc39MicroModuleWidth");
        row.Text = "40";
        Assert.True(row.HasError);
        Assert.Contains("between 1 and 10", row.Error);
        Loc.Instance.SetLanguage("es");
        var (p2, _, _) = Open(SymbSupport.Default(BarcodeSymbology.Tlc39));
        var row2 = Row<IntRow>(p2, "Prop.Tlc39MicroModuleWidth");
        row2.Text = "40";
        Assert.Contains("entre 1 y 10", row2.Error);
    }

    [Fact]
    public void PreviewNotes_AreShownForPlaceholdersAndApproximations_AndFollowTheLanguage()
    {
        var (p, _, e) = Open(SymbSupport.Default(BarcodeSymbology.MaxiCode));
        var note = Row<NoteRow>(p, "Prop.PreviewNote");
        Assert.Equal("The printer draws this barcode; the preview shows a placeholder.", note.Text);
        Assert.True(note.HasText);

        var (p2, _, _) = Open(SymbSupport.Default(BarcodeSymbology.Code128));
        Assert.False(Row<NoteRow>(p2, "Prop.PreviewNote").HasText);

        Loc.Instance.SetLanguage("es");
        var (p3, _, _) = Open(SymbSupport.Default(BarcodeSymbology.MaxiCode));
        Assert.StartsWith("La impresora dibuja", Row<NoteRow>(p3, "Prop.PreviewNote").Text);
    }

    [Fact]
    public void AnAztecRune_GivesTheSpecificNote_WithoutRebuildingThePanel()
    {
        var (p, s, e) = Open(SymbSupport.Default(BarcodeSymbology.Aztec));
        var note = Row<NoteRow>(p, "Prop.PreviewNote");
        Assert.DoesNotContain("this Aztec symbol", note.Text, StringComparison.OrdinalIgnoreCase);
        Row<IntRow>(p, "Prop.AztecErrorControl").Text = "300";
        Assert.Same(note, Row<NoteRow>(p, "Prop.PreviewNote"));
        Assert.Contains("Aztec symbol", note.Text);
    }

    [Fact]
    public void EveryNewBarcode_WithTheCatalogueExample_ValidatesAndExportsWithoutErrors()
    {
        foreach (var info in BarcodeCatalog.All)
        {
            var doc = new LabelDocument();
            doc.Elements.Add(ElementFactory.CreateBarcode(info.Symbology));
            var result = ZplGenerator.Generate(doc);
            Assert.False(result.HasErrors, $"{info.Symbology}: {string.Join("; ", result.Issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey))}");
            Assert.Contains(info.ZplCommand, result.Zpl);
        }
    }

    [Fact]
    public void Duplicate_KeepsEveryBarcodeOption()
    {
        foreach (var info in BarcodeCatalog.All)
        {
            var original = SymbSupport.Variant(info.Symbology);
            var clone = (BarcodeElement)ElementCloner.Clone(original, newId: true);
            Assert.Equal(original.ExportExtraOptions(), clone.ExportExtraOptions());
        }
    }

    [Fact]
    public void ResizingWithTheMouse_ScalesEachSymbologyThroughItsOwnKnobs()
    {
        var aztec = new BarcodeElement { Symbology = BarcodeSymbology.Aztec, Data = "x", ModuleSize = 4 };
        var codabar = new BarcodeElement { Symbology = BarcodeSymbology.Codabar, Data = "123", Height = 50, ModuleWidth = 2 };
        var s = AppTestSupport.Session(aztec, codabar);
        ElementResizer.Scale(aztec, s.Document, 2.0, 2.0);
        ElementResizer.Scale(codabar, s.Document, 1.0, 2.0);
        Assert.Equal(8, aztec.ModuleSize);
        Assert.Equal(100, codabar.Height);
    }

    [Fact]
    public void TheStatusBarNote_AppearsForASelectedPlaceholderBarcode()
    {
        var vm = new MainViewModel(new FakeServices().Build());
        var maxi = SymbSupport.Default(BarcodeSymbology.MaxiCode);
        vm.Session.Load(new LabelDocument());
        Assert.Equal(string.Empty, vm.BarcodePreviewNote);
        vm.Session.Edit("Undo.Property", d => d.Elements.Add(maxi));
        vm.Session.Select(vm.Session.Document.Elements[0]);
        Assert.Contains("placeholder", vm.BarcodePreviewNote);
    }
}
