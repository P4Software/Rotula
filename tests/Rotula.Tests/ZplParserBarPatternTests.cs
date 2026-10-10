using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using Rotula.Rendering.Input;
using ZXing;
using static Rotula.Tests.ZplParserTestSupport;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>Barcodes drawn as many ^GB bars: they must render as bars, be recognised, and convert to a real Code 128.</summary>
public class ZplParserBarPatternTests
{
    private static ZplParseResult Legacy() => ZplParser.ParseFile(ImportFixture("legacy_gb_bars_code128"));

    [Fact]
    public void Code128_table_matches_the_ZXing_encoder_symbol_for_symbol()
    {
        foreach (string data in new[] { "1000AAC", "HELLO", "1234567890", "12345678", "Hello World 42", "SKU-0042", "ab12", "A1B2C3D4E5F6", "12" })
        {
            bool[] zxing = new ZXing.OneD.Code128Writer().encode(data);
            var symbols = Code128.Encode(data);
            Assert.NotNull(symbols);
            Assert.Equal(zxing, Modules(Code128.ToRuns(symbols!)));

            // And the decoder reads what ZXing produced.
            Assert.True(Code128.TryDecode(Runs(zxing), out string value, out _), data);
            Assert.Equal(data, value);
        }
    }

    [Fact]
    public void Code128_for_1000AAC_is_start_C_10_00_switch_B_A_A_C_check_stop()
    {
        // Check symbol: (105 + 1*10 + 2*0 + 3*100 + 4*33 + 5*33 + 6*35) mod 103 = 922 mod 103 = 98.
        Assert.Equal(new[] { 105, 10, 0, 100, 33, 33, 35, 98, 106 }, Code128.Encode("1000AAC"));
    }

    [Fact]
    public void Fixture_bars_match_the_real_code128_module_pattern_for_1000AAC()
    {
        var runs = Code128.ToRuns(Code128.Encode("1000AAC")!);
        var bars = Legacy().Document.Elements.OfType<LineElement>().OrderBy(l => l.X).ToList();
        Assert.Equal(28, bars.Count);
        // Every bar is run[2k] modules wide (7 dots per module) and starts where the modules say.
        int x = 45;
        for (int k = 0; k < runs.Length; k++)
        {
            if (k % 2 == 0)
            {
                Assert.Equal(x, bars[k / 2].X);
                Assert.Equal(runs[k] * 7, bars[k / 2].ThicknessDots);
            }
            x += runs[k] * 7;
        }
    }

    [Fact]
    public void Bars_render_as_bars()
    {
        var label = LabelDocumentAdapter.ToRenderLabel(Legacy().Document);
        var boxes = label.Elements.OfType<Rotula.Rendering.Input.RenderBox>().Where(b => b.HeightDots == 216).ToList();
        Assert.Equal(28, boxes.Count);
        Assert.All(boxes, b => Assert.Equal(301, b.Y));
    }

    [Fact]
    public void Rendered_bars_scan_as_1000AAC()
    {
        var doc = Legacy().Document;
        // Only the bars: the text blocks above them would only add noise to the scan.
        doc.Elements.RemoveAll(e => e is not LineElement);
        var decoded = RenderAndDecode(LabelDocumentAdapter.ToRenderLabel(doc), BarcodeFormat.CODE_128);
        Assert.NotNull(decoded);
        Assert.Equal("1000AAC", decoded!.Text);
    }

    [Fact]
    public void Recognizer_detects_the_pattern_and_decodes_the_value()
    {
        var result = Legacy();
        var match = Assert.Single(BarPatternRecognizer.Detect(result.Document));
        Assert.Equal(28, match.Bars.Count);
        Assert.Equal("1000AAC", match.DecodedValue);
        Assert.Equal(BarcodeSymbology.Code128, match.Symbology);
        Assert.True(match.IsFaithful);
        Assert.True(match.CanConvert);
        Assert.Equal((45, 301, 216, 7), (match.X, match.Y, match.HeightDots, match.ModuleDots));
        Assert.Equal(752 - 45, match.TotalWidthDots);
        // The import result exposes the same match.
        Assert.Equal("1000AAC", Assert.Single(result.BarPatterns).DecodedValue);
    }

    [Fact]
    public void Conversion_replaces_the_bars_with_one_code128_element_that_prints_the_same_bars()
    {
        var doc = Legacy().Document;
        int before = doc.Elements.Count;
        int firstIndex = doc.Elements.FindIndex(e => e is LineElement);
        var match = Assert.Single(BarPatternRecognizer.Detect(doc));

        var barcode = BarPatternRecognizer.Convert(doc, match);

        Assert.NotNull(barcode);
        Assert.Equal(before - 28 + 1, doc.Elements.Count);
        Assert.Same(barcode, doc.Elements[firstIndex]);
        Assert.DoesNotContain(doc.Elements, e => e is LineElement);
        Assert.Equal(BarcodeSymbology.Code128, barcode!.Symbology);
        Assert.Equal("1000AAC", barcode.Data);
        Assert.Equal((45, 301, 216, 7), (barcode.X, barcode.Y, barcode.Height, barcode.ModuleWidth));
        Assert.False(barcode.HumanReadable);
        Assert.Equal(Code128Mode.Automatic, barcode.Code128Mode);

        string zpl = CoreTestSupport.Zpl(doc);
        Assert.Contains("^FO45,301^BY7,3.0^BC,216,N,N,N,A^FD1000AAC^FS", zpl);

        // The converted barcode scans, and no bars remain to be recognised again.
        doc.Elements.RemoveAll(e => e is not BarcodeElement);
        var decoded = RenderAndDecode(LabelDocumentAdapter.ToRenderLabel(doc), BarcodeFormat.CODE_128);
        Assert.Equal("1000AAC", decoded?.Text);
        Assert.Empty(BarPatternRecognizer.Detect(doc));
    }

    [Fact]
    public void Converted_barcode_modules_equal_the_original_bars()
    {
        var doc = Legacy().Document;
        var match = Assert.Single(BarPatternRecognizer.Detect(doc));
        var barcode = BarPatternRecognizer.CreateBarcode(match)!;
        bool[] zxing = new ZXing.OneD.Code128Writer().encode(barcode.Data);
        Assert.Equal(zxing, Modules(match.Runs!));
    }

    [Fact]
    public void Unrecognisable_bar_groups_are_reported_but_not_convertible()
    {
        var doc = new LabelDocument();
        // Twelve evenly spaced bars of one module each: a pattern, but not a valid Code 128 symbol.
        for (int i = 0; i < 12; i++)
            doc.Elements.Add(new LineElement { X = 10 + i * 4, Y = 20, Direction = LineDirection.Vertical, LengthDots = 50, ThicknessDots = 2 });
        var match = Assert.Single(BarPatternRecognizer.Detect(doc));
        Assert.Null(match.DecodedValue);
        Assert.False(match.CanConvert);
        Assert.Null(BarPatternRecognizer.CreateBarcode(match));
        Assert.Null(BarPatternRecognizer.Convert(doc, match));
        Assert.Equal(12, doc.Elements.Count);
    }

    [Fact]
    public void Few_bars_or_mixed_heights_are_not_a_pattern()
    {
        var doc = new LabelDocument();
        for (int i = 0; i < 6; i++)
            doc.Elements.Add(new LineElement { X = 10 + i * 4, Y = 20, Direction = LineDirection.Vertical, LengthDots = 50, ThicknessDots = 2 });
        for (int i = 0; i < 12; i++)
            doc.Elements.Add(new LineElement { X = 100 + i * 4, Y = 20 + i, Direction = LineDirection.Vertical, LengthDots = 50, ThicknessDots = 2 });
        Assert.Empty(BarPatternRecognizer.Detect(doc));
    }

    [Fact]
    public void A_code128_that_used_a_different_subset_sequence_is_not_offered_for_conversion()
    {
        // "1234" encoded entirely in subset B decodes to the same text but is not what automatic mode would print.
        int[] valuesB = { 104, '1' - 32, '2' - 32, '3' - 32, '4' - 32 };
        int sum = valuesB[0];
        for (int i = 1; i < valuesB.Length; i++) sum += i * valuesB[i];
        var all = valuesB.Append(sum % 103).Append(106).ToArray();

        var doc = new LabelDocument();
        int x = 20;
        var runs = Code128.ToRuns(all);
        for (int k = 0; k < runs.Length; k++)
        {
            if (k % 2 == 0)
                doc.Elements.Add(new LineElement { X = x, Y = 10, Direction = LineDirection.Vertical, LengthDots = 80, ThicknessDots = runs[k] * 2 });
            x += runs[k] * 2;
        }
        var match = Assert.Single(BarPatternRecognizer.Detect(doc, minimumBars: 10));
        Assert.Equal("1234", match.DecodedValue);
        Assert.False(match.IsFaithful);
        Assert.False(match.CanConvert);
    }

    private static bool[] Modules(IEnumerable<int> runs)
    {
        var list = new List<bool>();
        bool bar = true;
        foreach (int r in runs)
        {
            for (int i = 0; i < r; i++) list.Add(bar);
            bar = !bar;
        }
        return list.ToArray();
    }

    private static int[] Runs(bool[] modules)
    {
        var runs = new List<int>();
        int i = 0;
        while (i < modules.Length)
        {
            int j = i;
            while (j < modules.Length && modules[j] == modules[i]) j++;
            runs.Add(j - i);
            i = j;
        }
        return runs.ToArray();
    }
}
