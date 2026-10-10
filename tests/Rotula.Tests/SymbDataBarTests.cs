using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Rendering;
using Rotula.Rendering.Barcodes;
using Rotula.Rendering.Input;
using ZXing;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>
/// GS1 DataBar preview encoder. Proof of correctness: the figures and examples of ISO/IEC 24724:2011 and the GS1 General
/// Specifications (reference modules from libzint's test suite), the printed sample of the Zebra ZPL II Programming Guide, and
/// decoding the rendered image with ZXing.Net's RSS-14 and RSS Expanded readers.
/// </summary>
public class SymbDataBarTests
{
    private static string RowString(BarcodeMatrix m, int row)
    {
        var chars = new char[m.Width];
        for (int x = 0; x < m.Width; x++) chars[x] = m.IsDark(x, row) ? '1' : '0';
        return new string(chars);
    }

    private static BarcodeMatrix Encode(string type, string data, int cols)
    {
        var options = type switch
        {
            "OMN" => new BarcodeEncodeOptions { DataBarType = DataBarType.Omnidirectional },
            "LTD" => new BarcodeEncodeOptions { DataBarType = DataBarType.Limited },
            "STK" => new BarcodeEncodeOptions { DataBarType = DataBarType.Stacked },
            "OMNSTK" => new BarcodeEncodeOptions { DataBarType = DataBarType.StackedOmnidirectional },
            "EXP" => new BarcodeEncodeOptions { DataBarType = DataBarType.Expanded, DataBarSegmentsPerRow = 22 },
            // The reference encoder counts blocks of two symbol characters per row; ZPL counts the symbol characters.
            "EXPSTK" => new BarcodeEncodeOptions { DataBarType = DataBarType.Expanded, DataBarSegmentsPerRow = cols * 2 },
            _ => throw new ArgumentException(type),
        };
        return BarcodeEncoder.Encode(BarcodeSymbology.DataBar, data, options);
    }

    [Fact]
    public void Figures_of_the_standard_are_reproduced_module_for_module()
    {
        Assert.NotEmpty(SymbDataBarVectors.Figures);
        foreach (var (type, data, cols, comment, rows) in SymbDataBarVectors.Figures)
        {
            var m = Encode(type, data, cols);
            Assert.True(m.Height == rows.Length, $"{comment} ({type} {data}): expected {rows.Length} rows, got {m.Height}");
            for (int r = 0; r < rows.Length; r++)
                Assert.True(rows[r] == RowString(m, r), $"{comment} ({type} {data}) row {r}:\nexpected {rows[r]}\nactual   {RowString(m, r)}");
        }
    }

    [Fact]
    public void Value_group_boundaries_are_reproduced()
    {
        Assert.NotEmpty(SymbDataBarVectors.Boundaries);
        foreach (var (type, data, row) in SymbDataBarVectors.Boundaries)
        {
            var m = Encode(type, data, 0);
            Assert.True(row == RowString(m, 0), $"{type} {data}:\nexpected {row}\nactual   {RowString(m, 0)}");
        }
    }

    [Fact]
    public void Zebra_sample_symbol_has_the_encoding_of_a_left_padded_value_with_the_composite_linkage_flag()
    {
        // ZPL guide page 136: ^BRN,1,5,2,100 ^FD12345678901|... prints a DataBar Omnidirectional linked to a composite component.
        // The measured elements of the linear part (after the leading light element) equal the value 0012345678901 plus the linkage
        // flag 10^13: the guide's printer pads the item number on the left with zeros.
        int[] sample = { 1, 2, 1, 1, 4, 1, 1, 4, 2, 2, 5, 6, 1, 1, 2, 4, 1, 3, 1, 1, 2, 1, 1, 1, 1, 1, 2, 7, 1, 1, 1, 1, 8, 3, 2, 1, 1, 2, 4, 2, 1, 1, 4, 1, 1 };
        var m = DataBarEncoder.EncodeOmniValue(10000000000000L + 12345678901L);
        var runs = new List<int>();
        bool cur = m.IsDark(0, 0);
        int n = 0;
        for (int x = 0; x < m.Width; x++)
        {
            if (m.IsDark(x, 0) == cur) n++; else { runs.Add(n); cur = !cur; n = 1; }
        }
        runs.Add(n);
        Assert.False(m.IsDark(0, 0));
        Assert.Equal(sample, runs.Skip(1));
    }

    [Fact]
    public void Short_item_numbers_are_padded_with_zeros_and_a_check_digit_is_ignored()
    {
        string padded = RowString(Encode("OMN", "0012345678901", 0), 0);
        Assert.Equal(padded, RowString(Encode("OMN", "12345678901", 0), 0));
        Assert.Equal(RowString(Encode("OMN", "1234567890123", 0), 0), RowString(Encode("OMN", "12345678901231", 0), 0));
    }

    [Fact]
    public void Truncated_has_the_bars_of_omnidirectional()
    {
        var omni = BarcodeEncoder.Encode(BarcodeSymbology.DataBar, "1234567890123", new BarcodeEncodeOptions { DataBarType = DataBarType.Omnidirectional });
        var truncated = BarcodeEncoder.Encode(BarcodeSymbology.DataBar, "1234567890123", new BarcodeEncodeOptions { DataBarType = DataBarType.Truncated });
        Assert.Equal(RowString(omni, 0), RowString(truncated, 0));
        Assert.Equal(96, omni.Width);
        Assert.True(omni.IsLinear);
    }

    [Fact]
    public void Layouts_and_row_weights_of_the_stacked_types()
    {
        var stacked = Encode("STK", "1234567890123", 0);
        Assert.Equal((50, 3, BarcodeRowLayout.Proportional), (stacked.Width, stacked.Height, stacked.Layout));
        Assert.Equal(new[] { 5, 1, 7 }, stacked.RowHeights);
        var omniStacked = Encode("OMNSTK", "1234567890123", 0);
        Assert.Equal(new[] { 33, 1, 1, 1, 33 }, omniStacked.RowHeights);
        var separatorTwo = BarcodeEncoder.Encode(BarcodeSymbology.DataBar, "1234567890123", new BarcodeEncodeOptions { DataBarType = DataBarType.Stacked, DataBarSeparatorHeight = 2 });
        Assert.Equal(new[] { 5, 2, 7 }, separatorTwo.RowHeights);
        // Expanded: a full row of segments stays on one line; fewer segments per row stack the symbol.
        string data = "(01)90614141999999(3103)001750(10)ABC123";
        var oneRow = BarcodeEncoder.Encode(BarcodeSymbology.DataBar, data, new BarcodeEncodeOptions { DataBarType = DataBarType.Expanded, DataBarSegmentsPerRow = 22 });
        var twoRows = BarcodeEncoder.Encode(BarcodeSymbology.DataBar, data, new BarcodeEncodeOptions { DataBarType = DataBarType.Expanded, DataBarSegmentsPerRow = 6 });
        Assert.True(oneRow.IsLinear);
        Assert.True(twoRows.Height >= 5 && twoRows.Height % 4 == 1, "stacked rows: " + twoRows.Height);
    }

    [Theory]
    [InlineData("(01)90614141999999")]
    [InlineData("(01)90614141999999(3103)001750")]
    [InlineData("(01)90614141999999(10)ABC123")]
    [InlineData("(10)12A")]
    [InlineData("(21)Ab-9.z(240)LONG-ish value 42")]
    public void Expanded_is_valid_for_a_range_of_data(string data)
    {
        var m = BarcodeEncoder.Encode(BarcodeSymbology.DataBar, data, new BarcodeEncodeOptions { DataBarType = DataBarType.Expanded });
        Assert.True(m.Width > 100);
    }

    [Theory]
    [InlineData("0190614141999999")]            // no parentheses
    [InlineData("(01)")]                        // no value
    [InlineData("(01)9061414199999é")]     // character outside the compaction sets
    [InlineData("(10)")]
    public void Expanded_rejects_malformed_element_strings(string data)
    {
        Assert.Null(BarcodeEncoder.TryEncode(BarcodeSymbology.DataBar, data, new BarcodeEncodeOptions { DataBarType = DataBarType.Expanded }, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Limited_rejects_values_above_the_limit()
    {
        Assert.Null(BarcodeEncoder.TryEncode(BarcodeSymbology.DataBar, "2000000000000", new BarcodeEncodeOptions { DataBarType = DataBarType.Limited }, out _));
        Assert.NotNull(BarcodeEncoder.TryEncode(BarcodeSymbology.DataBar, "1999999999999", new BarcodeEncodeOptions { DataBarType = DataBarType.Limited }, out _));
    }

    // ---- decoding the rendered symbol ------------------------------------------------------------------

    private static RenderBarcode Make(DataBarType type, string data, int segments = 22) => new()
    {
        Symbology = BarcodeSymbology.DataBar, Data = data, DataBarType = type, DataBarSegmentsPerRow = segments, ModuleDots = 3,
        HeightDots = type is DataBarType.Stacked or DataBarType.StackedOmnidirectional ? 120 : 100, ShowText = false, X = 40, Y = 40,
    };

    [Theory]
    [InlineData(DataBarType.Omnidirectional, "0950110153001", "(01)09501101530010")]
    [InlineData(DataBarType.Omnidirectional, "1234567890123", "(01)12345678901231")]
    [InlineData(DataBarType.Truncated, "0001234567890", "(01)00012345678905")]
    public void Rendered_omnidirectional_symbol_decodes_with_zxing(DataBarType type, string data, string expected)
    {
        var decoded = RenderAndDecode(Label(700, 300, Make(type, data)), BarcodeFormat.RSS_14);
        Assert.NotNull(decoded);
        // ZXing returns the GTIN-14 without the application identifier.
        Assert.Equal(expected.Replace("(01)", string.Empty), decoded!.Text);
    }

    [Theory]
    [InlineData("(01)90614141999999(3103)001750", "(01)90614141999999(3103)001750")]
    [InlineData("(01)90614141999999(10)ABC123", "(01)90614141999999(10)ABC123")]
    [InlineData("(01)98898765432106(3202)012345(15)991231", "(01)98898765432106(3202)012345(15)991231")]
    [InlineData("(10)12A", "(10)12A")]
    [InlineData("(01)09501101530003(17)140704(10)AB-123", "(01)09501101530003(17)140704(10)AB-123")]
    public void Rendered_expanded_symbol_decodes_with_zxing(string data, string expected)
    {
        var decoded = RenderAndDecode(Label(1400, 300, Make(DataBarType.Expanded, data)), BarcodeFormat.RSS_EXPANDED);
        Assert.NotNull(decoded);
        Assert.Equal(expected, decoded!.Text);
    }

    [Fact]
    public void Data_bar_placeholder_is_gone_and_the_element_renders_with_ink()
    {
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(300) };
        foreach (var type in Enum.GetValues<DataBarType>())
        {
            var e = SymbSupport.Default(BarcodeSymbology.DataBar);
            e.DataBarType = type;
            if (type == DataBarType.Expanded) e.Data = "(01)90614141999999(10)ABC";
            e.Y = 10 + 40 * (int)type;
            e.Rotation = FieldRotation.Normal;
            e.Height = 30;
            e.ModuleSize = 2;
            doc.Elements.Add(e);
        }
        var label = LabelDocumentAdapter.ToRenderLabel(doc);
        Assert.All(label.Elements, e => Assert.IsType<RenderBarcode>(e));
        using var result = LabelRenderer.RenderBitmap(label);
        Assert.Empty(result.Issues);
    }
}
