using Rotula.Core.Elements;
using Rotula.Core.Model;
using static Rotula.Tests.CoreTestSupport;

namespace Rotula.Tests;

/// <summary>Golden files for the design commands added to the generator (one fixture per command family).</summary>
public class ZplGoldenNewCommandsTests
{
    [Fact]
    public void DiagonalLines() =>
        AssertGolden("graphics_diagonal", Zpl(NewDoc(
            new DiagonalLineElement { X = 20, Y = 20, WidthDots = 200, HeightDots = 120, ThicknessDots = 6 },
            new DiagonalLineElement { X = 260, Y = 20, WidthDots = 200, HeightDots = 120, ThicknessDots = 6, Lean = DiagonalLean.Left, Color = GraphicColor.White })));

    [Fact]
    public void Ellipse() =>
        AssertGolden("graphics_ellipse", Zpl(NewDoc(new EllipseElement { X = 20, Y = 20, WidthDots = 300, HeightDots = 140, ThicknessDots = 6 })));

    [Fact]
    public void Symbols() =>
        AssertGolden("graphics_symbol", Zpl(NewDoc(
            new SymbolElement { X = 20, Y = 20, Symbol = GraphicSymbol.Registered, HeightDots = 50, WidthDots = 50 },
            new SymbolElement { X = 100, Y = 20, Symbol = GraphicSymbol.Copyright, HeightDots = 50, WidthDots = 50, Rotation = FieldRotation.Rotated90 },
            new SymbolElement { X = 180, Y = 20, Symbol = GraphicSymbol.CanadianStandards, HeightDots = 50, WidthDots = 0 })));

    [Fact]
    public void TextBox() =>
        AssertGolden("text_box", Zpl(NewDoc(new TextElement
        {
            X = 50, Y = 50, Text = "A long paragraph with a < sign that must wrap inside the box.", FontId = '0', FontHeight = 30, FontWidth = 30,
            Rotation = FieldRotation.Normal, TextBox = new TextBoxOptions { WidthDots = 400, HeightDots = 120 },
        })));

    [Fact]
    public void PrinterSettings()
    {
        var doc = NewDoc(new TextElement { X = 10, Y = 10, Text = "Settings" });
        doc.LabelShift = 20;
        doc.LabelTop = -8;
        doc.MirrorImage = true;
        doc.PrintMode = PrintMode.Peel;
        doc.PrePeel = true;
        doc.PrintRate = new PrintRateOptions { Speed = 4, SlewSpeed = 6, BackfeedSpeed = 2 };
        doc.MediaDarkness = 8.5m;
        doc.FontAliases.Add(new FontAlias('Q', "E:ARIAL.TTF"));
        AssertGolden("label_printer_settings", Zpl(doc));
    }

    [Fact]
    public void FieldOptions() =>
        AssertGolden("field_options", Zpl(NewDoc(
            new TextElement { X = 20, Y = 20, Text = "Name", FontId = '0', FontHeight = 30, FontWidth = 30, FieldNumber = 3, FieldPrompt = "Customer name" },
            new TextElement { X = 20, Y = 70, Text = "Ref #3# on %d/%m/%Y", FontId = '0', FontHeight = 30, FontWidth = 30, EmbedCharacter = '#', Clock = new FieldClockOptions { Primary = '%' } },
            new TextElement { X = 20, Y = 120, Text = "tall", FontId = '0', FontHeight = 30, FontWidth = 30, Direction = FieldDirection.Vertical, InterCharacterGap = 3 },
            new BarcodeElement { X = 20, Y = 200, Symbology = BarcodeSymbology.Code128, Data = "12345", FieldNumber = 4 })));

    [Fact]
    public void PrinterSideSerials() =>
        AssertGolden("field_serials", Zpl(NewDoc(
            new TextElement { X = 20, Y = 20, Text = "000100", FontId = '0', FontHeight = 40, FontWidth = 40, Serial = new PrinterSerialOptions { Increment = 10, LeadingZeros = true } },
            new TextElement { X = 20, Y = 80, Text = "AB0001", FontId = '0', FontHeight = 40, FontWidth = 40, Serial = new PrinterSerialOptions { Kind = PrinterSerialKind.Mask, Mask = "AADDDD", IncrementText = "AA0001" } },
            new BarcodeElement { X = 20, Y = 150, Symbology = BarcodeSymbology.Code128, Data = "5000", Serial = new PrinterSerialOptions { Increment = -1 } })));

    [Fact]
    public void DownloadedGraphicWithRecall()
    {
        var doc = NewDoc(
            new RecallPlaceholderElement { X = 30, Y = 30, Command = "XG", Reference = "R:LOGO.GRF", Zpl = "^XGR:LOGO.GRF,2,2", EmitAsField = true },
            new RecallPlaceholderElement { X = 130, Y = 30, Command = "IM", Reference = "R:LOGO.GRF", Zpl = "^IMR:LOGO.GRF", EmitAsField = true });
        doc.StoredGraphics.Add(new StoredGraphic("R:LOGO.GRF", 1, Convert.FromHexString("FF818181818181FF")));
        AssertGolden("graphics_stored_download", Zpl(doc));
    }
}
