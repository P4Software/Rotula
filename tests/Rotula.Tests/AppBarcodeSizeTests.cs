using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests;

/// <summary>The size readout, the module stepper and the quiet-zone guide of the barcode property panel.</summary>
[Collection("App")]
public class AppBarcodeSizeTests : IDisposable
{
    public AppBarcodeSizeTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private static (PropertyPanelViewModel Panel, EditorSession Session, IntRow Module) Open(BarcodeElement element, PrinterDpi dpi = PrinterDpi.Dpi203)
    {
        var s = AppTestSupport.Session(element);
        s.Document.Dpi = dpi;
        var p = new PropertyPanelViewModel(s);
        s.Select(element);
        var key = BarcodeCatalog.Get(element.Symbology).Supports(BarcodeOption.ModuleWidth) ? "Prop.ModuleWidth" : "Prop.ModuleSize";
        return (p, s, p.Rows.OfType<IntRow>().First(r => r.LabelKey == key));
    }

    private static BarcodeElement Now(EditorSession s) => (BarcodeElement)s.Document.Elements[0];

    private static BarcodeElement Code128(string data = "ABC", int module = 2) =>
        new() { Symbology = BarcodeSymbology.Code128, Data = data, ModuleWidth = module, X = 100, Y = 50 };

    [Fact]
    public void TheModuleRow_ShowsTheReadoutAndTheQuietZoneNote()
    {
        var (_, _, row) = Open(Code128());
        // 2 dots at 203 dpi is 9.9 mil = 0.25 mm; "ABC" is 68 modules = 136 dots = 17 mm; the 10X quiet zone is 20 dots = 2.5 mm.
        Assert.Equal("Module: 9.9 mil (0.25 mm) - symbol 17 mm wide" + Environment.NewLine + "Quiet zone: 2.5 mm on each side (10X)", row.Detail);
        Assert.False(row.DetailIsWarning);
        Assert.True(row.ShowStepper);
        Assert.True(row.FullWidth);
    }

    [Fact]
    public void AnEan13Readout_NamesBothQuietZones()
    {
        var (_, _, row) = Open(new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "590123412345", ModuleWidth = 3, X = 100, Y = 50 }, PrinterDpi.Dpi300);
        Assert.Equal("Module: 10 mil (0.25 mm) - symbol 24.1 mm wide" + Environment.NewLine
            + "Quiet zone: 2.8 mm before and 1.8 mm after the symbol (11X and 7X)", row.Detail);
    }

    [Fact]
    public void TheReadout_IsInSpanishInASpanishSession()
    {
        Loc.Instance.SetLanguage("es");
        var (_, _, row) = Open(Code128());
        Assert.StartsWith("Módulo: ", row.Detail);
        Assert.Contains("símbolo de ", row.Detail);
        Assert.Contains("Zona de silencio: ", row.Detail);
    }

    [Fact]
    public void TheStepper_ChangesTheModuleAndTheReadoutLive_InOneUndoStepEach()
    {
        var (_, s, row) = Open(Code128());
        row.StepUpCommand.Execute(null);
        Assert.Equal(3, Now(s).ModuleWidth);
        Assert.Equal("3", row.Text);
        // 3 dots: 68 modules = 204 dots = 25.5 mm, and the module itself 14.8 mil / 0.38 mm.
        Assert.StartsWith("Module: 14.8 mil (0.38 mm) - symbol 25.5 mm wide", row.Detail);
        Assert.Equal(1, s.Undo.UndoCount);

        row.StepDownCommand.Execute(null);
        row.StepDownCommand.Execute(null);
        Assert.Equal(1, Now(s).ModuleWidth);

        s.Undo.Undo();
        // Steps on the same property merge into one undo step, so undo returns to the start and the readout follows it.
        Assert.StartsWith("Module: 9.9 mil", row.Detail);
    }

    [Fact]
    public void TheStepper_StopsAtTheLimits()
    {
        var (_, s, row) = Open(Code128(module: 10));
        row.StepUpCommand.Execute(null);
        Assert.Equal(10, Now(s).ModuleWidth);
        var (_, s1, row1) = Open(Code128(module: 1));
        row1.StepDownCommand.Execute(null);
        Assert.Equal(1, Now(s1).ModuleWidth);
        Assert.False(row1.HasError);
    }

    [Fact]
    public void ARejectedTypedValue_IsReplacedByTheNextStep()
    {
        var (_, s, row) = Open(Code128());
        row.Text = "99";
        Assert.True(row.HasError);
        row.StepUpCommand.Execute(null);
        Assert.False(row.HasError);
        Assert.Equal(3, Now(s).ModuleWidth);
    }

    [Fact]
    public void ATooSmallModule_ColoursTheReadoutAsAWarning_AndTheProblemsListSaysWhy()
    {
        var (_, _, row) = Open(Code128(module: 1), PrinterDpi.Dpi300);
        Assert.True(row.DetailIsWarning);
        Assert.StartsWith("Module: 3.3 mil (0.08 mm)", row.Detail);

        // The wording of the complaint is in the problems list (the same ZPL issues the status bar counts), in plain language.
        var issue = Rotula.Core.Zpl.ZplGenerator.Generate(WithDpi(PrinterDpi.Dpi300, Code128(module: 1))).Issues.First(i => i.MessageKey == BarcodeSizing.OneDotKey);
        string text = Loc.FormatIssue(issue);
        Assert.Contains("1 dot (3.3 mil, 0.085 mm) at 300 dpi", text);
        Assert.DoesNotContain("Issue.", text);
    }

    private static LabelDocument WithDpi(PrinterDpi dpi, BarcodeElement b)
    {
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(400), Dpi = dpi };
        doc.Elements.Add(b);
        return doc;
    }

    [Fact]
    public void EveryNewIssueKey_IsTranslatedAndFormatsItsArguments()
    {
        foreach (var lang in new[] { "en", "es" })
        {
            Loc.Instance.SetLanguage(lang);
            foreach (string key in new[] { BarcodeSizing.OneDotKey, BarcodeSizing.ThinBarKey, BarcodeSizing.QuietZoneKey })
            {
                Assert.True(Loc.Instance.HasKey("Issue." + key), $"{lang}: {key}");
                string text = Loc.FormatIssue(new Rotula.Core.Validation.ValidationIssue(Rotula.Core.Validation.ValidationSeverity.Warning, key, null, "1", "3.3", "0.085", "300"));
                Assert.DoesNotContain("{", text);
            }
        }
    }

    [Fact]
    public void AQrRow_KeepsItsPrintedSize_AndAddsTheModuleAndTheQuietZone()
    {
        var qr = new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "HELLO", ModuleSize = 4, X = 100, Y = 50 };
        var (_, _, row) = Open(qr);
        Assert.Contains("Printed size", row.Detail);
        Assert.Contains("Module: 19.7 mil (0.5 mm)", row.Detail);
        Assert.Contains("Quiet zone: 2 mm on each side (4X)", row.Detail);
        Assert.True(row.ShowStepper);
    }

    [Fact]
    public void ADataMatrixRow_GetsAnEncoderMeasuredWidth()
    {
        var dm = new BarcodeElement { Symbology = BarcodeSymbology.DataMatrix, Data = "HELLO", ModuleSize = 4, X = 100, Y = 50 };
        var (_, _, row) = Open(dm);
        Assert.Contains("symbol ", row.Detail);
        Assert.Contains("Quiet zone: 0.5 mm on each side (1X)", row.Detail);
    }

    [Fact]
    public void ASymbologyWithoutAModule_HasNoReadout()
    {
        var maxi = new BarcodeElement { Symbology = BarcodeSymbology.MaxiCode, Data = "x", X = 10, Y = 10 };
        var s = AppTestSupport.Session(maxi);
        var p = new PropertyPanelViewModel(s);
        s.Select(maxi);
        Assert.DoesNotContain(p.Rows.OfType<IntRow>(), r => r.ShowStepper);
    }

    [Fact]
    public void OtherNumberRows_HaveNoStepper()
    {
        var (p, _, _) = Open(Code128());
        Assert.Equal(new[] { "Prop.ModuleWidth" }, p.Rows.OfType<IntRow>().Where(r => r.ShowStepper).Select(r => r.LabelKey));
    }

    [Fact]
    public void TheQuietZoneGuide_GrowsTheFrameOnTheReadingAxisOnly()
    {
        var frame = new RectD(100, 50, 190, 100);
        var ean = new BarcodeElement { Symbology = BarcodeSymbology.Ean13, ModuleWidth = 2 };
        Assert.Equal(new RectD(78, 50, 190 + 22 + 14, 100), BarcodeSizeInfo.QuietZoneFrame(frame, ean, FieldRotation.Normal));

        ean.Rotation = FieldRotation.Rotated90;   // reads top to bottom, so the margins are above and below
        Assert.Equal(new RectD(100, 28, 190, 100 + 22 + 14), BarcodeSizeInfo.QuietZoneFrame(frame, ean, FieldRotation.Normal));

        var qr = new BarcodeElement { Symbology = BarcodeSymbology.QrCode, ModuleSize = 3 };
        Assert.Equal(new RectD(88, 38, 190 + 24, 100 + 24), BarcodeSizeInfo.QuietZoneFrame(frame, qr, FieldRotation.Normal));

        Assert.Null(BarcodeSizeInfo.QuietZoneFrame(frame, new BarcodeElement { Symbology = BarcodeSymbology.MaxiCode }, FieldRotation.Normal));
    }

    [Fact]
    public void TheGuide_DefaultsToTheLabelRotation_WhenTheFieldHasNone()
    {
        var frame = new RectD(100, 50, 190, 100);
        var code = new BarcodeElement { Symbology = BarcodeSymbology.Code128, ModuleWidth = 2 };
        Assert.Equal(new RectD(100, 30, 190, 140), BarcodeSizeInfo.QuietZoneFrame(frame, code, FieldRotation.Rotated90));
    }
}
