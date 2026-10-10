using Rotula.Core.Model;
using Rotula.Rendering.Barcodes;
using Rotula.Rendering.Input;
using ZXing;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>Proves that rendered preview barcodes actually scan: each image is decoded back with ZXing.Net.</summary>
public class RenderingBarcodeScanTests
{
    public static TheoryData<BarcodeSymbology, BarcodeFormat, string, string> Cases => new()
    {
        { BarcodeSymbology.Code128, BarcodeFormat.CODE_128, "ROTULA-12345", "ROTULA-12345" },
        { BarcodeSymbology.Code39, BarcodeFormat.CODE_39, "ABC-123", "ABC-123" },
        { BarcodeSymbology.Code93, BarcodeFormat.CODE_93, "TEST93", "TEST93" },
        { BarcodeSymbology.Ean13, BarcodeFormat.EAN_13, "590123412345", "5901234123457" },
        { BarcodeSymbology.UpcA, BarcodeFormat.UPC_A, "03600029145", "036000291452" },
        { BarcodeSymbology.Interleaved2Of5, BarcodeFormat.ITF, "1234567890", "1234567890" },
        { BarcodeSymbology.QrCode, BarcodeFormat.QR_CODE, "https://example.com/rotula?id=42", "https://example.com/rotula?id=42" },
        { BarcodeSymbology.DataMatrix, BarcodeFormat.DATA_MATRIX, "ROTULA DM 123", "ROTULA DM 123" },
        { BarcodeSymbology.Pdf417, BarcodeFormat.PDF_417, "PDF417 sample payload 0123456789", "PDF417 sample payload 0123456789" },
    };

    private static RenderBarcode Make(BarcodeSymbology s, string data, RenderRotation rotation = RenderRotation.None) => new()
    {
        Symbology = s,
        Data = data,
        ModuleDots = s is BarcodeSymbology.QrCode or BarcodeSymbology.DataMatrix ? 5 : 3,
        HeightDots = s == BarcodeSymbology.Pdf417 ? 12 : 90,
        ShowText = true,
        X = 40, // quiet zone: the label background is white around the symbol
        Y = 40,
        Rotation = rotation,
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rendered_barcode_decodes_at_printer_resolution(BarcodeSymbology s, BarcodeFormat f, string data, string expected)
    {
        var decoded = RenderAndDecode(Label(900, 500, Make(s, data)), f);
        Assert.NotNull(decoded);
        Assert.Equal(expected, decoded!.Text);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rendered_barcode_decodes_when_rotated_90(BarcodeSymbology s, BarcodeFormat f, string data, string expected)
    {
        var decoded = RenderAndDecode(Label(900, 900, Make(s, data, RenderRotation.Rotate90)), f);
        Assert.NotNull(decoded);
        Assert.Equal(expected, decoded!.Text);
    }

    [Theory]
    [InlineData(BarcodeSymbology.QrCode, BarcodeFormat.QR_CODE, "ZOOM-TEST-QR")]
    [InlineData(BarcodeSymbology.Code128, BarcodeFormat.CODE_128, "ZOOM-TEST-128")]
    [InlineData(BarcodeSymbology.DataMatrix, BarcodeFormat.DATA_MATRIX, "ZOOMDM")]
    public void Rendered_barcode_still_decodes_when_zoomed_in(BarcodeSymbology s, BarcodeFormat f, string data)
    {
        var decoded = RenderAndDecode(Label(600, 400, Make(s, data)), f, scale: 2.0);
        Assert.NotNull(decoded);
        Assert.Equal(data, decoded!.Text);
    }

    [Fact]
    public void Invalid_data_reports_an_issue_instead_of_throwing()
    {
        // EAN-13 needs digits only; the preview must survive and tell the user what is wrong.
        using var result = Rotula.Rendering.LabelRenderer.RenderBitmap(
            Label(400, 200, Make(BarcodeSymbology.Ean13, "NOT-DIGITS")));
        var issue = Assert.Single(result.Issues);
        Assert.Contains("Check the barcode data", issue.Message);
    }

    [Fact]
    public void Encoder_reports_empty_data_in_plain_language()
    {
        var ex = Assert.Throws<BarcodeEncodingException>(() => BarcodeEncoder.Encode(BarcodeSymbology.Code128, ""));
        Assert.Contains("no data", ex.Message);
    }

    [Fact]
    public void Qr_module_size_matches_the_requested_dots()
    {
        var matrix = BarcodeEncoder.Encode(BarcodeSymbology.QrCode, "A");
        using var result = Rotula.Rendering.LabelRenderer.RenderBitmap(Label(300, 300,
            new RenderBarcode { Symbology = BarcodeSymbology.QrCode, Data = "A", ModuleDots = 4, X = 10, Y = 10 }));
        var bounds = DarkBounds(result.Bitmap)!.Value;
        Assert.Equal(matrix.Width * 4, bounds.Width);
        Assert.Equal(matrix.Height * 4, bounds.Height);
    }

    [Fact]
    public void Ean13_human_readable_text_includes_the_computed_check_digit()
    {
        Assert.Equal("5901234123457", BarcodeEncoder.HumanReadableText(BarcodeSymbology.Ean13, "590123412345"));
    }

    [Fact]
    public void Optional_check_digits_match_the_printer_algorithms()
    {
        var opts = new BarcodeEncodeOptions { CheckDigit = true };
        // ITF mod 10 and Code 39 mod 43 check digits get appended, so the symbols decode with them.
        var itf = RenderAndDecode(Label(700, 300, new RenderBarcode
        { Symbology = BarcodeSymbology.Interleaved2Of5, Data = "123456789", CheckDigit = true, ModuleDots = 3, X = 40, Y = 40 }), BarcodeFormat.ITF);
        Assert.Equal("1234567895", itf?.Text);
        Assert.Equal("CODE39W", BarcodeEncoder.HumanReadableText(BarcodeSymbology.Code39, "CODE39", opts));
    }
}
