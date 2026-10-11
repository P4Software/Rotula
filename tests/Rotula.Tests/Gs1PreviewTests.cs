using Rotula.Core.Gs1;
using Rotula.Core.Model;
using Rotula.Rendering.Barcodes;
using ZXing;
using static Rotula.Tests.Gs1DecodeSupport;

namespace Rotula.Tests;

/// <summary>
/// The designer's preview of GS1 symbols, checked by reading them back: the data comes out as the element string and the reader reports
/// the GS1 symbology identifier (AIM: ]C1 for GS1-128, ]d2 for GS1 DataMatrix, ]Q3 for GS1 QR Code).
/// </summary>
public class Gs1PreviewTests
{
    private const char GS = Gs1Message.GroupSeparator;
    private static readonly BarcodeEncodeOptions Gs1 = new() { Gs1 = true };

    private static string ElementString(string bracketed) => Gs1Message.ParseBracketed(bracketed, out _)!.ToElementString();

    [Theory]
    [InlineData("(01)09506000134352(17)251231(10)ABC123")]
    [InlineData("(01)09506000134352(10)LOT1(21)SER9(17)251231")]
    [InlineData("(00)376104250021234569")]
    [InlineData("(01)09521234500001(3103)001234(10)A>B/1")]
    public void Gs128_IsReadAsGs1128(string bracketed)
    {
        var read = RoundTrip(BarcodeSymbology.Code128, bracketed, BarcodeFormat.CODE_128, Gs1);
        Assert.Equal("]C1", read.SymbologyIdentifier);
        // ZXing.Net puts the identifier in front of the text for FNC1 symbols; the rest is the element string with the group separator.
        Assert.StartsWith("]C1", read.Text, StringComparison.Ordinal);
        Assert.Equal(ElementString(bracketed), read.Text[3..]);
    }

    [Fact]
    public void PlainCode128_IsNotReportedAsGs1()
    {
        var read = RoundTrip(BarcodeSymbology.Code128, "ROTULA-12345", BarcodeFormat.CODE_128);
        Assert.Equal("]C0", read.SymbologyIdentifier);
        Assert.Equal("ROTULA-12345", read.Text);
    }

    [Theory]
    [InlineData("(01)09506000134352(10)ABC123")]
    [InlineData("(01)09506000134352(10)LOT1(21)SER9(17)251231")]
    [InlineData("(00)376104250021234569(37)12")]
    public void Gs1DataMatrix_IsReadAsFnc1First(string bracketed)
    {
        var read = RoundTrip(BarcodeSymbology.DataMatrix, bracketed, BarcodeFormat.DATA_MATRIX, Gs1);
        Assert.Equal("]d2", read.SymbologyIdentifier);
        // Depending on its version the reader may show the leading FNC1 as a group separator; the data after it is the element string.
        Assert.Equal(ElementString(bracketed), read.Text.TrimStart(GS));
    }

    [Fact]
    public void Gs1DataMatrix_CodewordsAreFnc1ThenAsciiWithDigitPairs()
    {
        var words = DataMatrixGs1Encoder.DataCodewords(Gs1Message.ParseBracketed("(01)09506000134352(10)AB(17)251231", out _)!);
        // ISO/IEC 16022 ASCII encodation: FNC1 = 232, a pair of digits = 130 + the two-digit number, any other character = its ASCII value + 1.
        // 0109506000134352 -> 01 09 50 60 00 13 43 52, then "10" = 10, A = 66, B = 67; AI 10 has a variable length, so FNC1 follows; then 17 25 12 31.
        Assert.Equal(new[] { 232, 131, 139, 180, 190, 130, 143, 173, 182, 140, 66, 67, 232, 147, 155, 142, 161 }, words);
    }

    [Fact]
    public void Gs1QrCode_IsReadAsFnc1InFirstPosition_ButThePrinterCannotMakeOne()
    {
        // The encoder can draw it (so the capability is tested), yet no ZPL command of the guide produces it: Rotula's GS1 QR is a Digital Link.
        var read = RoundTrip(BarcodeSymbology.QrCode, "(01)09506000134352(10)ABC123(17)251231", BarcodeFormat.QR_CODE, Gs1);
        Assert.Equal("]Q3", read.SymbologyIdentifier);
        Assert.Equal(ElementString("(01)09506000134352(10)ABC123(17)251231"), read.Text);
    }

    [Fact]
    public void DigitalLinkQrCode_IsAnOrdinaryQrCode()
    {
        const string uri = "https://id.gs1.org/01/09506000134352/10/ABC123?17=251231";
        var read = RoundTrip(BarcodeSymbology.QrCode, uri, BarcodeFormat.QR_CODE);
        Assert.Equal("]Q1", read.SymbologyIdentifier);
        Assert.Equal(uri, read.Text);
    }

    [Fact]
    public void Itf14_RoundTripsFourteenDigits_WithoutAFifteenth()
    {
        string data = Gs1Itf14.Data(1, "9521234500001");
        var read = RoundTrip(BarcodeSymbology.Interleaved2Of5, data, BarcodeFormat.ITF);
        Assert.Equal(data, read.Text);
        Assert.Equal(14, read.Text.Length);
    }

    [Fact]
    public void DataBarExpanded_IsReadBack()
    {
        const string element = "(01)90614141000015(3202)000150";
        var read = RoundTrip(BarcodeSymbology.DataBar, element, BarcodeFormat.RSS_EXPANDED, new BarcodeEncodeOptions { DataBarType = DataBarType.Expanded });
        Assert.Equal(element, read.Text);
    }

    [Fact]
    public void TheHumanReadableLineOfGs128_ShowsTheParentheses()
    {
        const string data = "(01)09506000134352(10)LOT1(17)251231";
        Assert.Equal(data, BarcodeEncoder.HumanReadableText(BarcodeSymbology.Code128, data, Gs1));
    }

    [Theory]
    [InlineData(BarcodeSymbology.Code128, "01095060001343521")]       // no parentheses
    [InlineData(BarcodeSymbology.DataMatrix, "01095060001343")]
    [InlineData(BarcodeSymbology.QrCode, "")]
    public void BadGs1Data_IsRefusedByThePreview(BarcodeSymbology symbology, string data)
    {
        Assert.Null(BarcodeEncoder.TryEncode(symbology, data, Gs1, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TheAdapterMarksGs1Barcodes_ForTheRenderer()
    {
        var doc = new Rotula.Core.Model.LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(300) };
        doc.Elements.Add(new Rotula.Core.Elements.BarcodeElement
        {
            Symbology = BarcodeSymbology.Code128, Code128Mode = Code128Mode.Ean, Data = "(01)09506000134352(10)LOT1", X = 20, Y = 20, Height = 120,
        });
        doc.Elements.Add(new Rotula.Core.Elements.BarcodeElement
        {
            Symbology = BarcodeSymbology.DataMatrix, DataMatrixGs1 = true, Data = "(01)09506000134352(10)LOT1", X = 20, Y = 180, ModuleSize = 4,
        });
        var model = Rotula.Rendering.Input.LabelDocumentAdapter.ToRenderLabel(doc);
        Assert.Equal(2, model.Elements.OfType<Rotula.Rendering.Input.RenderBarcode>().Count(b => b.Gs1));
    }
}
