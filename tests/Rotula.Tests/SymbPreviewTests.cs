using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Rendering;
using Rotula.Rendering.Barcodes;
using Rotula.Rendering.Input;
using SkiaSharp;
using ZXing;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>
/// Preview encoders of the symbologies added after the first nine. Correctness is shown two ways: by decoding the rendered
/// image with ZXing.Net where a decoder exists, and by comparing the module patterns with the printed example symbols of the
/// Zebra ZPL II Programming Guide (bar and space widths measured from the sample images, in modules of the ^BY3 samples).
/// </summary>
public class SymbPreviewTests
{
    /// <summary>Run lengths (bar first) of the single row of a linear matrix.</summary>
    private static int[] Runs(BarcodeMatrix m)
    {
        var runs = new List<int>();
        bool current = m.IsDark(0, 0);
        int n = 0;
        for (int x = 0; x < m.Width; x++)
        {
            if (m.IsDark(x, 0) == current) n++;
            else { runs.Add(n); current = !current; n = 1; }
        }
        runs.Add(n);
        return runs.ToArray();
    }

    private static int[] Ref(string s) => s.Split(' ').Select(int.Parse).ToArray();

    private static BarcodeMatrix Encode(BarcodeSymbology s, string data, BarcodeEncodeOptions? o = null) => BarcodeEncoder.Encode(s, data, o);

    // ---- patterns against the Zebra sample symbols -----------------------------------------------

    public static TheoryData<string, BarcodeSymbology, string, string> ZebraSamples => new()
    {
        // ZPL guide ^B1 sample, page 67: ^B1N,N,150,Y,N ^FD123456 (two check digits, "11")
        { "Code 11", BarcodeSymbology.Code11, "123456", "1 1 3 3 1 1 3 1 1 1 3 1 1 3 1 1 3 1 3 3 1 1 1 1 1 1 3 1 3 1 3 1 3 1 1 1 1 3 3 1 1 1 3 1 1 1 3 1 3 1 1 1 3 1 1 1 3 3 1" },
        // ^BJ sample, page 117: ^BJN,150,Y,N ^FD123456
        { "Standard 2 of 5", BarcodeSymbology.Standard2Of5, "123456", "1 1 1 1 3 1 1 1 1 1 1 1 3 1 1 1 3 1 1 1 1 1 3 1 3 1 3 1 1 1 1 1 1 1 1 1 1 1 3 1 1 1 3 1 3 1 1 1 3 1 1 1 1 1 1 1 3 1 3 1 1 1 1 1 3 1 1" },
        // ^BM sample, page 123: ^BMN,B,100,Y,N,N ^FD123456 (one mod 10 check digit, 6)
        { "MSI", BarcodeSymbology.Msi, "123456", "3 1 1 3 1 3 1 3 3 1 1 3 1 3 3 1 1 3 1 3 1 3 3 1 3 1 1 3 3 1 1 3 1 3 1 3 3 1 1 3 3 1 1 3 3 1 3 1 1 3 1 3 3 1 3 1 1 3 1 3 1" },
        // ^BP sample, page 127: ^BPN,N,100,Y,N ^FD12345 (the CRC is in the symbol even with the check digit option off)
        { "Plessey", BarcodeSymbology.Plessey, "12345", "3 1 3 1 1 3 3 1 3 1 1 3 1 3 1 3 1 3 3 1 1 3 1 3 3 1 3 1 1 3 1 3 1 3 1 3 3 1 1 3 3 1 1 3 3 1 1 3 1 3 3 1 3 1 1 3 1 3 3 1 3 1 3 1 4 3 1 3 1 1 3 1 3" },
        // ^B8 sample, page 84: ^B8N,100,Y,N ^FD1234567
        { "EAN-8", BarcodeSymbology.Ean8, "1234567", "1 1 1 2 2 2 1 2 1 2 2 1 4 1 1 1 1 3 2 1 1 1 1 1 1 2 3 1 1 1 1 4 1 3 1 2 3 2 1 1 1 1 1" },
        // ^B9 sample, page 86: ^B9N,100,Y,N,Y ^FD1230000045 (zero-suppressed to 123453)
        { "UPC-E", BarcodeSymbology.UpcE, "1230000045", "1 1 1 1 2 2 2 2 2 1 2 1 4 1 1 2 3 1 1 1 2 3 1 1 4 1 1 1 1 1 1 1 1" },
        // ^BS samples, page 138
        { "Add-on 2", BarcodeSymbology.UpcEanExtension, "12", "1 1 2 2 2 2 1 1 1 2 1 2 2" },
        { "Add-on 5", BarcodeSymbology.UpcEanExtension, "12345", "1 1 2 1 2 2 2 1 1 2 1 2 2 1 1 1 1 4 1 1 1 1 1 3 2 1 1 1 2 3 1" },
    };

    [Theory]
    [MemberData(nameof(ZebraSamples))]
    public void Module_pattern_equals_the_printed_zebra_sample(string name, BarcodeSymbology s, string data, string expected)
    {
        var options = new BarcodeEncodeOptions { Code11CheckDigits = 2, MsiCheckDigit = MsiCheckDigitMode.Mod10 };
        // ZXing's EAN-8 and UPC-E writers use 1-module and 2-module bars, so those two compare exactly; the wide/narrow codes use 3:1
        // like the ^BY3 samples.
        var actual = Runs(Encode(s, data, options));
        Assert.True(Ref(expected).SequenceEqual(actual), $"{name}: expected {expected} but got {string.Join(' ', actual)}");
    }

    [Fact]
    public void Codabar_pattern_equals_the_printed_zebra_sample()
    {
        // ^BKN,N,150,Y,N,A,A ^FD123456 on page 119 (start A, stop A)
        var runs = Runs(Encode(BarcodeSymbology.Codabar, "123456"));
        Assert.Equal(Ref("1 1 3 3 1 3 1 1 1 1 1 1 3 3 1 1 1 1 1 3 1 1 3 1 3 3 1 1 1 1 1 1 1 1 3 1 1 3 1 1 3 1 1 1 1 3 1 1 1 3 1 1 1 1 3 1 1 1 3 3 1 3 1"), runs);
    }

    /// <summary>Tall (1) and short (0) bars of a postal symbol, read from the rows of the matrix.</summary>
    private static string PostalBars(BarcodeMatrix m)
    {
        var sb = new System.Text.StringBuilder();
        for (int x = 0; x < m.Width; x++)
            if (m.IsDark(x, 1)) sb.Append(m.IsDark(x, 0) ? '1' : '0');
        return sb.ToString();
    }

    [Fact]
    public void Postnet_bars_equal_the_printed_zebra_sample()
    {
        // ^BZN,40,Y,N ^FD12345 on page 151: frame, 1 2 3 4 5, check digit 5, frame (1 = tall bar)
        Assert.Equal("10001100101001100100101010010101", PostalBars(Encode(BarcodeSymbology.Postnet, "12345")));
    }

    [Fact]
    public void Planet_bars_equal_the_printed_zebra_sample()
    {
        // ^B5N,100,Y,N ^FD12345678901 on page 78: the digit patterns are the inverse of POSTNET and the check digit 4 is appended
        Assert.Equal("11110011010110011011010101100110111001101010110011111100101101", PostalBars(Encode(BarcodeSymbology.Planet, "12345678901")));
    }

    [Fact]
    public void Postal_short_bars_are_forty_percent_of_the_tall_ones()
    {
        var m = Encode(BarcodeSymbology.Postnet, "12345");
        Assert.Equal(BarcodeRowLayout.Proportional, m.Layout);
        Assert.Equal(new[] { 3, 2 }, m.RowHeights);
        // Rendered: the tallest bar is 40 dots, the short ones 16.
        var label = Label(600, 120, new RenderBarcode { Symbology = BarcodeSymbology.Postnet, Data = "12345", ModuleDots = 3, HeightDots = 40, ShowText = false, X = 10, Y = 10 });
        using var result = LabelRenderer.RenderBitmap(label);
        int tallest = 0, shortest = int.MaxValue;
        for (int x = 0; x < result.Bitmap.Width; x++)
        {
            int run = 0;
            for (int y = 0; y < result.Bitmap.Height; y++) if (IsDark(result.Bitmap, x, y)) run++;
            if (run == 0) continue;
            tallest = Math.Max(tallest, run);
            shortest = Math.Min(shortest, run);
        }
        Assert.Equal((40, 16), (tallest, shortest));
    }

    [Fact]
    public void Industrial_2_of_5_uses_the_published_start_and_stop_with_two_wide_bars_per_digit()
    {
        // The guide has no printed sample of ^BI, so this checks the structure of the published definition instead:
        // start wide-wide-narrow, stop wide-narrow-wide, five bars per digit with exactly two wide, all spaces narrow.
        var runs = Runs(Encode(BarcodeSymbology.Industrial2Of5, "0123456789"));
        var bars = runs.Where((_, i) => i % 2 == 0).ToArray();
        var spaces = runs.Where((_, i) => i % 2 == 1).ToArray();
        Assert.Equal(3 + 50 + 3, bars.Length);
        Assert.All(spaces, w => Assert.Equal(1, w));
        Assert.Equal(new[] { 3, 3, 1 }, bars.Take(3));
        Assert.Equal(new[] { 3, 1, 3 }, bars.TakeLast(3));
        var digits = bars.Skip(3).Take(50).Chunk(5).Select(c => string.Concat(c.Select(w => w == 3 ? 'w' : 'n'))).ToList();
        Assert.All(digits, d => Assert.Equal(2, d.Count(c => c == 'w')));
        Assert.Equal(10, digits.Distinct().Count());
        // The digits use the same weights as Standard 2 of 5, whose digit table is confirmed by the printed sample.
        Assert.Equal("nnwwn", digits[0]);
        Assert.Equal("wnnnw", digits[1]);
    }

    // ---- decoding the rendered image ----------------------------------------------------------------

    private static RenderBarcode Make(BarcodeSymbology s, string data, int module = 3, int height = 90, bool text = false) => new()
    {
        Symbology = s, Data = data, ModuleDots = module, HeightDots = height, ShowText = text, X = 40, Y = 40,
    };

    [Theory]
    [InlineData(BarcodeSymbology.Ean8, BarcodeFormat.EAN_8, "9638507", "96385074")]
    [InlineData(BarcodeSymbology.Ean8, BarcodeFormat.EAN_8, "1234567", "12345670")]
    [InlineData(BarcodeSymbology.UpcE, BarcodeFormat.UPC_E, "1230000045", "01234531")]
    [InlineData(BarcodeSymbology.UpcE, BarcodeFormat.UPC_E, "1234500005", "01234558")]
    [InlineData(BarcodeSymbology.Logmars, BarcodeFormat.CODE_39, "12AB", "12ABO")]
    [InlineData(BarcodeSymbology.Logmars, BarcodeFormat.CODE_39, "lower-9", "LOWER-9Y")]
    [InlineData(BarcodeSymbology.Codabar, BarcodeFormat.CODABAR, "123456", "123456")]
    [InlineData(BarcodeSymbology.Codabar, BarcodeFormat.CODABAR, "40-$:/.+7", "40-$:/.+7")]
    [InlineData(BarcodeSymbology.Aztec, BarcodeFormat.AZTEC, "Rotula Aztec 123", "Rotula Aztec 123")]
    public void Rendered_symbol_decodes_with_zxing(BarcodeSymbology s, BarcodeFormat f, string data, string expected)
    {
        var decoded = RenderAndDecode(Label(900, 400, Make(s, data, module: s == BarcodeSymbology.Aztec ? 5 : 3)), f);
        Assert.NotNull(decoded);
        Assert.Equal(expected, decoded!.Text);
    }

    [Fact]
    public void Aztec_symbol_equals_the_printed_zebra_sample_bit_for_bit()
    {
        // ZPL guide page 125: ^BOR,7,N,0,N,1,0 ^FD 7. This is testing label 7 prints a 19 x 19 compact symbol. The sample was
        // printed rotated (R) and its image is stored mirrored, which is the anti-transpose of the unrotated symbol. The match shows
        // that the default error correction and the layer choice are the same as the printer's.
        string[] sample =
        {
            "0100010001111101101",
            "0111011100000011000",
            "0011100011110001100",
            "0010011010011000100",
            "0111011111010110101",
            "0101011111111100111",
            "0110110000000101110",
            "0101110111110101100",
            "0101110100010111010",
            "0101010101010110100",
            "0010010100010100100",
            "0010110111110111111",
            "0110010000000101100",
            "0100011111111111111",
            "0111001001001111000",
            "0100010001101001111",
            "0100010111101001110",
            "1001111110001010011",
            "0011000110111010011",
        };
        var m = Encode(BarcodeSymbology.Aztec, " 7. This is testing label 7");
        Assert.Equal((19, 19), (m.Width, m.Height));
        int differences = 0;
        for (int y = 0; y < 19; y++)
            for (int x = 0; x < 19; x++)
                if ((sample[y][x] == '1') != m.IsDark(18 - y, 18 - x)) differences++;
        Assert.Equal(0, differences);
    }

    [Fact]
    public void Codabar_start_and_stop_letters_are_in_the_symbol()
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.CODABAR], ReturnCodabarStartEnd = true },
        };
        using var result = LabelRenderer.RenderBitmap(Label(900, 300, Make(BarcodeSymbology.Codabar, "123456")));
        var gray = new byte[result.Bitmap.Width * result.Bitmap.Height];
        for (int y = 0; y < result.Bitmap.Height; y++)
            for (int x = 0; x < result.Bitmap.Width; x++) gray[y * result.Bitmap.Width + x] = result.Bitmap.GetPixel(x, y).Red;
        var luminance = new RGBLuminanceSource(gray, result.Bitmap.Width, result.Bitmap.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        Assert.Equal("A123456A", reader.Decode(luminance)?.Text);
    }

    [Theory]
    [InlineData(MsiCheckDigitMode.None, "1234567")]
    [InlineData(MsiCheckDigitMode.Mod10, "12345674")]
    public void Msi_decodes_with_zxing_including_the_check_digit_the_printer_adds(MsiCheckDigitMode mode, string expected)
    {
        var bc = Make(BarcodeSymbology.Msi, "1234567") with { MsiCheckDigit = mode };
        var decoded = RenderAndDecode(Label(900, 300, bc), BarcodeFormat.MSI);
        Assert.NotNull(decoded);
        Assert.Equal(expected, decoded!.Text);
    }

    [Fact]
    public void Msi_double_mod10_and_mod11_mod10_append_the_documented_digits()
    {
        // 1234567: IBM mod 10 gives 4, and a second mod 10 over 12345674 gives 1;
        // mod 11 (weights 2-7 from the right) also gives 4, followed by the same mod 10 over 12345674.
        Assert.Equal("41", MsiChecks("1234567", MsiCheckDigitMode.DoubleMod10));
        Assert.Equal("41", MsiChecks("1234567", MsiCheckDigitMode.Mod11Mod10));
    }

    private static string MsiChecks(string digits, MsiCheckDigitMode mode)
    {
        // The check digits are visible through the text-line option.
        string text = BarcodeEncoder.HumanReadableText(BarcodeSymbology.Msi, digits, new BarcodeEncodeOptions { MsiCheckDigit = mode, MsiCheckDigitInText = true });
        return text[digits.Length..];
    }

    [Fact]
    public void Plessey_bit_sequence_follows_zxing_writer_including_the_crc()
    {
        // ZXing.Net's PlesseyWriter is an independent implementation; compare the bit sequence (bar width classifies each bit).
        var theirs = new ZXing.OneD.PlesseyWriter().encode("12AB", BarcodeFormat.PLESSEY, 0, 0);
        var theirBits = new List<int>();
        int x = 100; // their symbol starts after a 100-unit quiet zone
        while (x < theirs.Width)
        {
            int bar = 0;
            while (x < theirs.Width && theirs[x, 0]) { bar++; x++; }
            while (x < theirs.Width && !theirs[x, 0]) x++;
            if (bar > 0) theirBits.Add(bar >= 12 ? 1 : 0);
        }
        var mine = Runs(Encode(BarcodeSymbology.Plessey, "12AB"));
        var myBits = new List<int>();
        // start (4 bits) + data (16) + CRC (8)
        for (int i = 0; i < (4 + 16 + 8) * 2; i += 2) myBits.Add(mine[i] == 3 ? 1 : 0);
        Assert.Equal(string.Concat(myBits), string.Concat(theirBits.Take(myBits.Count)));
    }

    // ---- text line ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(BarcodeSymbology.Ean8, "1234567", "1234 5670")]            // guide sample: 1234 5670
    [InlineData(BarcodeSymbology.Ean8, "12345670", "1234 5670")]
    [InlineData(BarcodeSymbology.UpcE, "1230000045", "0 123453 1")]         // guide sample: 0 123453 1
    [InlineData(BarcodeSymbology.Code11, "123456", "12345611")]             // guide sample: 12345611 between the start/stop marks
    [InlineData(BarcodeSymbology.Codabar, "123456", "A123456A")]            // guide sample
    [InlineData(BarcodeSymbology.Logmars, "12AB", "12ABO")]                 // guide sample: 12ABO
    [InlineData(BarcodeSymbology.Standard2Of5, "123456", "123456")]
    [InlineData(BarcodeSymbology.Msi, "123456", "123456")]
    [InlineData(BarcodeSymbology.Postnet, "12345", "12345")]                // the printed text has no check digit
    [InlineData(BarcodeSymbology.Planet, "12345678901", "12345678901")]
    public void Human_readable_text_matches_the_guide_samples(BarcodeSymbology s, string data, string expected)
    {
        var options = new BarcodeEncodeOptions { CheckDigit = true };
        Assert.Equal(expected, BarcodeEncoder.HumanReadableText(s, data, options));
    }

    [Fact]
    public void Upc_e_text_hides_the_check_digit_unless_asked()
    {
        Assert.Equal("0 123453", BarcodeEncoder.HumanReadableText(BarcodeSymbology.UpcE, "1230000045", new BarcodeEncodeOptions { CheckDigit = false }));
    }

    [Fact]
    public void Plessey_and_msi_show_the_check_characters_only_when_asked()
    {
        Assert.Equal("12345", BarcodeEncoder.HumanReadableText(BarcodeSymbology.Plessey, "12345", new BarcodeEncodeOptions { CheckDigit = false }));
        string withCrc = BarcodeEncoder.HumanReadableText(BarcodeSymbology.Plessey, "12345", new BarcodeEncodeOptions { CheckDigit = true });
        Assert.Equal(7, withCrc.Length);
        Assert.Equal("1234566", BarcodeEncoder.HumanReadableText(BarcodeSymbology.Msi, "123456", new BarcodeEncodeOptions { MsiCheckDigitInText = true }));
    }

    // ---- rendering integration ------------------------------------------------------------------------

    [Fact]
    public void Symbologies_without_an_encoder_become_a_labelled_placeholder()
    {
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(400) };
        foreach (var s in BarcodeCatalog.All.Where(i => !i.HasPreviewEncoder).Select(i => i.Symbology))
            doc.Elements.Add(SymbSupport.Default(s));
        var label = LabelDocumentAdapter.ToRenderLabel(doc);
        Assert.Equal(doc.Elements.Count, label.Elements.Count);
        for (int i = 0; i < label.Elements.Count; i++)
        {
            var p = Assert.IsType<RenderPlaceholder>(label.Elements[i]);
            Assert.Contains("printer draws it", p.Caption);
            Assert.Contains(BarcodeCatalog.Get(((BarcodeElement)doc.Elements[i]).Symbology).DisplayName, p.Caption);
        }
        using var rendered = LabelRenderer.RenderBitmap(label);
        Assert.Empty(rendered.Issues);
    }

    [Fact]
    public void Aztec_options_the_preview_cannot_draw_fall_back_to_the_placeholder()
    {
        foreach (var tweak in new Action<BarcodeElement>[] { e => e.AztecErrorControl = 300, e => e.AztecMenuSymbol = true, e => e.AztecStructuredAppendCount = 2, e => e.AztecEci = true })
        {
            var e = SymbSupport.Default(BarcodeSymbology.Aztec);
            tweak(e);
            Assert.NotNull(BarcodeCatalog.GetPreviewLimitation(e));
            var doc = new LabelDocument { Width = Measure.Dots(400), Height = Measure.Dots(300) };
            doc.Elements.Add(e);
            Assert.IsType<RenderPlaceholder>(Assert.Single(LabelDocumentAdapter.ToRenderLabel(doc).Elements));
        }
        Assert.Null(BarcodeCatalog.GetPreviewLimitation(SymbSupport.Default(BarcodeSymbology.Aztec)));
    }

    [Fact]
    public void Catalogue_preview_flags_agree_with_the_encoder()
    {
        foreach (var info in BarcodeCatalog.All)
        {
            var e = SymbSupport.Default(info.Symbology);
            var matrix = BarcodeEncoder.TryEncode(info.Symbology, e.Data, new BarcodeEncodeOptions(), out string? error);
            Assert.Equal(info.HasPreviewEncoder, matrix is not null);
            if (!info.HasPreviewEncoder) Assert.Contains("no preview", error);
            Assert.Equal(info.HasPreviewEncoder, BarcodeEncoder.HasPreviewEncoder(info.Symbology));
        }
    }

    [Fact]
    public void Every_previewable_symbology_renders_without_issues_and_with_ink()
    {
        foreach (var info in BarcodeCatalog.All.Where(i => i.HasPreviewEncoder))
        {
            var e = SymbSupport.Default(info.Symbology);
            e.Rotation = FieldRotation.Normal;
            var doc = new LabelDocument { Width = Measure.Dots(1200), Height = Measure.Dots(500) };
            doc.Elements.Add(e);
            using var result = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(doc));
            Assert.True(result.Issues.Count == 0, info.DisplayName + ": " + string.Join("; ", result.Issues.Select(i => i.Message)));
            Assert.NotNull(DarkBounds(result.Bitmap));
        }
    }

    [Fact]
    public void Invalid_data_for_the_new_symbologies_reports_an_issue_instead_of_throwing()
    {
        foreach (var (s, bad) in new[]
        {
            (BarcodeSymbology.Code11, "12A"), (BarcodeSymbology.Standard2Of5, "12A"), (BarcodeSymbology.Industrial2Of5, "1 2"), (BarcodeSymbology.Codabar, "A1B"),
            (BarcodeSymbology.Msi, "12x"), (BarcodeSymbology.Plessey, "XYZ"), (BarcodeSymbology.Ean8, "12345"), (BarcodeSymbology.UpcE, "1234500004"),
            (BarcodeSymbology.UpcEanExtension, "123"), (BarcodeSymbology.Postnet, "12a"), (BarcodeSymbology.Logmars, "ab_c"),
        })
        {
            Assert.Null(BarcodeEncoder.TryEncode(s, bad, null, out string? error));
            Assert.False(string.IsNullOrEmpty(error), s.ToString());
        }
    }

    [Fact]
    public void Code_11_one_check_digit_option_changes_the_symbol_and_the_text()
    {
        var one = new BarcodeEncodeOptions { Code11CheckDigits = 1 };
        Assert.Equal("1234561", BarcodeEncoder.HumanReadableText(BarcodeSymbology.Code11, "123456", one));
        Assert.True(Runs(Encode(BarcodeSymbology.Code11, "123456", one)).Length < Runs(Encode(BarcodeSymbology.Code11, "123456")).Length);
    }
}
