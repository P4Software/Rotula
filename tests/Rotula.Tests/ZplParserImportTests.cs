using System.Text;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using static Rotula.Tests.ZplParserTestSupport;

namespace Rotula.Tests;

/// <summary>Importing real-world ZPL: fixtures, raw passthrough, recalls, encodings and malformed input.</summary>
public class ZplParserImportTests
{
    private static ZplParseResult ParseFixture(string name) => ZplParser.ParseFile(ImportFixture(name));

    private static List<string> Keys(ZplParseResult r) => r.Warnings.Select(w => w.MessageKey).ToList();

    // ---- fixture 1: bars drawn with ^GB --------------------------------------------------

    [Fact]
    public void Legacy_label_imports_settings_text_blocks_and_28_bars()
    {
        var r = ParseFixture("legacy_gb_bars_code128");
        var doc = r.Document;

        Assert.Equal(812, doc.WidthDots);
        Assert.Equal(609, doc.HeightDots);
        Assert.Equal(ZplEncoding.Cp850, doc.Encoding);
        Assert.Null(doc.DefaultFont);

        var bars = doc.Elements.OfType<LineElement>().ToList();
        Assert.Equal(28, bars.Count);
        Assert.All(bars, b =>
        {
            Assert.Equal(LineDirection.Vertical, b.Direction);
            Assert.Equal(301, b.Y);
            Assert.Equal(216, b.LengthDots);
            Assert.Equal(GraphicColor.Black, b.Color);
        });
        // ^FO45,301^GB,216,14,B : the omitted width becomes the thickness, so this is a 14x216 bar.
        Assert.Equal((45, 14), (bars[0].X, bars[0].ThicknessDots));

        var texts = doc.Elements.OfType<TextElement>().ToList();
        Assert.Equal(4, texts.Count);
        Assert.Equal("DISTRIBUIDORA DEL PACIFICO", texts[0].Text);
        Assert.Equal('0', texts[0].FontId);
        Assert.Equal(FieldRotation.Normal, texts[0].Rotation);
        Assert.Equal((48, 48), (texts[0].FontHeight, texts[0].FontWidth));
        var block = Assert.IsType<TextBlockOptions>(texts[0].Block);
        Assert.Equal((749, 1, 13, TextAlignment.Left, 0), (block.WidthDots, block.MaxLines, block.LineSpacing, block.Alignment, block.HangingIndent));
        // ^CI13 is code page 850: _A4 is the n-tilde and _A0 the a-acute; \& is the block line break.
        Assert.Equal("Av. España 1234\nPanamá, Panamá", texts[1].Text);
        Assert.Equal(TextAlignment.Right, texts[2].Block!.Alignment);
        Assert.Equal("1000AAC", texts[3].Text);

        Assert.Equal(new[] { "Import.BarPattern.Detected" }, Keys(r));
        Assert.Equal(new[] { "28", "1000AAC" }, r.Warnings[0].Args);
    }

    [Fact]
    public void Legacy_label_is_the_same_whether_parsed_from_text_or_bytes()
    {
        string path = ImportFixture("legacy_gb_bars_code128");
        AssertSame(ZplParser.Parse(File.ReadAllText(path)).Document, ZplParser.Parse(File.ReadAllBytes(path)).Document);
    }

    // ---- fixture 2: printer setup block ---------------------------------------------------

    [Fact]
    public void Printer_setup_block_imports_as_settings_without_visual_elements()
    {
        var r = ParseFixture("printer_setup");
        var doc = r.Document;

        Assert.Equal(812, doc.WidthDots);
        Assert.Equal(LabelOrientation.Normal, doc.Orientation);
        Assert.Equal(MediaTracking.Web, doc.MediaTracking);
        Assert.Equal(0, doc.Margins.Left.ToDots(doc.Dpi));
        Assert.Equal(0, doc.Margins.Top.ToDots(doc.Dpi));

        // The only element is the preserved, invisible ^IS command.
        var raw = Assert.IsType<RawZplElement>(Assert.Single(doc.Elements));
        Assert.False(raw.IsVisual);
        Assert.Equal("^ISR:FORMAT.GRF,Y", raw.Zpl);

        var save = Assert.Single(r.Warnings, w => w.MessageKey == "Import.ImageSave.Unsupported");
        Assert.Equal(new[] { "R:FORMAT.GRF" }, save.Args);
        Assert.Equal((8, 1), (save.Line, save.Column));
        // No ^LL in the block: the default height is kept and the user is told.
        Assert.Contains("Import.Label.NoLength", Keys(r));
        Assert.DoesNotContain("Import.Command.Unknown", Keys(r));
    }

    // ---- recalls --------------------------------------------------------------------------

    [Fact]
    public void Recalls_become_placeholders_with_warnings_and_survive_regeneration()
    {
        var r = ParseFixture("recalls");
        var elements = r.Document.Elements;

        var il = Assert.IsType<RecallPlaceholderElement>(elements[0]);
        Assert.Equal(("IL", "R:BACKGND.GRF", false), (il.Command, il.Reference, il.EmitAsField));
        var xf = Assert.IsType<RecallPlaceholderElement>(elements[1]);
        Assert.Equal(("XF", "R:LABEL.ZPL", false), (xf.Command, xf.Reference, xf.EmitAsField));
        var xg = Assert.IsType<RecallPlaceholderElement>(elements[2]);
        Assert.Equal(("XG", "R:LOGO.GRF", true, 20, 20), (xg.Command, xg.Reference, xg.EmitAsField, xg.X, xg.Y));
        Assert.Equal("After the recalls", Assert.IsType<TextElement>(elements[3]).Text);

        Assert.Equal(
            new[] { "Import.Recall.Image.Unavailable", "Import.Recall.Format.Unavailable", "Import.Recall.Graphic.Unavailable" },
            Keys(r));
        Assert.Equal(il.Id, r.Warnings[0].ElementId);

        string again = ZplGenerator.Generate(r.Document).Zpl;
        Assert.Contains("^ILR:BACKGND.GRF", again);
        Assert.Contains("^XFR:LABEL.ZPL", again);
        Assert.Contains("^FO20,20^XGR:LOGO.GRF,1,1^FS", again);
    }

    [Fact]
    public void Stored_format_definition_warns_and_keeps_the_command()
    {
        var r = ZplParser.Parse("^XA^DFR:FMT.ZPL^FS^FO10,10^A0N,30,30^FDHi^FS^XZ");
        Assert.Contains("Import.StoredFormat.Unsupported", Keys(r));
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { Zpl: "^DFR:FMT.ZPL" });
        Assert.Contains(r.Document.Elements, e => e is TextElement { Text: "Hi" });
    }

    // ---- unknown commands -----------------------------------------------------------------

    [Fact]
    public void Unknown_commands_are_kept_raw_and_listed_with_their_position()
    {
        var r = ParseFixture("unknown_commands");
        var raws = r.Document.Elements.OfType<RawZplElement>().ToList();

        // ^LS20 standalone, ^FX comment, the ^FN field, the ^SN field, ~SD15.
        Assert.Contains(raws, e => e.Zpl == "^LS20" && !e.IsVisual);
        Assert.Contains(raws, e => e.Zpl == "^FX A comment that must survive^FS");
        Assert.Contains(raws, e => e.Zpl == "^FO20,80^FN1^A0N,30,30^FDWith a field number^FS" && e.IsVisual && e.X == 20 && e.Y == 80);
        Assert.Contains(raws, e => e.Zpl == "^FO20,140^SN001,1,Y^A0N,30,30^FDSerial^FS" && e.IsVisual);
        Assert.Contains(raws, e => e.Zpl == "~SD15");
        Assert.Contains(r.Document.Elements, e => e is TextElement { Text: "Plain text" });

        var unknown = r.Warnings.Where(w => w.MessageKey == "Import.Command.Unknown").ToList();
        Assert.Equal(new[] { "^LS", "^FN", "^SN", "~SD" }, unknown.Select(w => w.Args[0]).ToArray());
        var ls = unknown[0];
        Assert.Equal((5, 1), (ls.Line, ls.Column));
        Assert.Equal(ImportFixtureText("unknown_commands").IndexOf("^LS20", StringComparison.Ordinal), ls.Position);

        // Nothing is lost: every raw block is written back.
        string again = ZplGenerator.Generate(r.Document).Zpl;
        foreach (var raw in raws) Assert.Contains(raw.Zpl, again);
    }

    private static string ImportFixtureText(string name) => File.ReadAllText(ImportFixture(name)).Replace("\r\n", "\n");

    [Fact]
    public void Unsupported_mirroring_is_preserved_but_pmn_is_silent()
    {
        var r = ZplParser.Parse("^XA^PMY^PW400^LL200^XZ");
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { Zpl: "^PMY" });
        Assert.Contains("Import.Setting.Unsupported", Keys(r));
        Assert.DoesNotContain("Import.Setting.Unsupported", Keys(ZplParser.Parse("^XA^PMN^PW400^LL200^XZ")));
    }

    // ---- graphics -------------------------------------------------------------------------

    [Fact]
    public void Compressed_graphic_field_is_decoded()
    {
        var r = ParseFixture("graphic_compressed");
        var image = Assert.IsType<ImageElement>(Assert.Single(r.Document.Elements));
        var mono = ImageProcessor.ToMonochrome(image);
        Assert.Equal((16, 8, 2), (mono.Width, mono.Height, mono.BytesPerRow));
        Assert.Equal("FF00FF000000FFFFFFF00F0F0000F000", mono.ToHex());
        Assert.Equal((10, 10), (image.X, image.Y));
    }

    [Fact]
    public void Z64_graphic_field_is_decoded()
    {
        byte[] hexText = Encoding.ASCII.GetBytes("FF00FF00FF00FF00");
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(hexText);
        string z64 = ":Z64:" + Convert.ToBase64String(ms.ToArray()) + ":ABCD";
        var r = ZplParser.Parse($"^XA^FO0,0^GFA,8,8,2,{z64}^FS^XZ");
        var image = Assert.IsType<ImageElement>(Assert.Single(r.Document.Elements));
        Assert.Equal("FF00FF00FF00FF00", ImageProcessor.ToMonochrome(image).ToHex());
    }

    [Fact]
    public void Binary_graphic_formats_are_kept_raw_with_a_warning()
    {
        var r = ZplParser.Parse("^XA^FO0,0^GFB,8,8,2,xxxxxxxx^FS^XZ");
        Assert.Contains("Import.Graphic.Format.Unsupported", Keys(r));
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { IsVisual: true });
    }

    [Theory]
    [InlineData("^FO10,10^GB,216,14,B^FS", LineDirection.Vertical, 216, 14)]
    [InlineData("^FO10,10^GB100,3,3^FS", LineDirection.Horizontal, 100, 3)]
    [InlineData("^FO10,10^GB100,30,30,B^FS", LineDirection.Horizontal, 100, 30)]
    public void Gb_without_rounding_and_a_side_equal_to_thickness_is_a_line(string field, LineDirection dir, int length, int thickness)
    {
        var line = Assert.IsType<LineElement>(Assert.Single(ZplParser.Parse("^XA" + field + "^XZ").Document.Elements));
        Assert.Equal((dir, length, thickness), (line.Direction, line.LengthDots, line.ThicknessDots));
    }

    [Fact]
    public void Gb_with_all_sides_larger_than_thickness_or_a_rounding_value_is_a_box()
    {
        var els = ZplParser.Parse("^XA^FO1,1^GB400,300,3^FS^FO1,1^GB600,3,3,B,0^FS^FO1,1^GB,,50^FS^XZ").Document.Elements;
        var box = Assert.IsType<BoxElement>(els[0]);
        Assert.Equal((400, 300, 3), (box.WidthDots, box.HeightDots, box.ThicknessDots));
        Assert.IsType<BoxElement>(els[1]);
        // ^GB with only a thickness is a solid square of that size.
        var solid = Assert.IsType<LineElement>(els[2]);
        Assert.Equal(50, solid.ThicknessDots);
    }

    // ---- sticky state and defaults --------------------------------------------------------

    [Fact]
    public void By_fw_and_cf_are_sticky_and_mid_label_changes_are_written_into_elements()
    {
        const string zpl = "^XA^PW800^LL400^FWN^CF0,30,30^BY3,2.5,60" +
            "^FO10,10^BCN,,Y,N^FD123^FS" +          // height from ^BY, ratio from ^BY
            "^FO10,100^B3,N,50,N,N^FDABC^FS" +
            "^FWR^CFA,20,10" +
            "^FO10,200^FDRotated, new font^FS" +    // inherits the changed ^FW and ^CF
            "^FO10,250^A0N,40,40^FDExplicit^FS^XZ";
        var doc = ZplParser.Parse(zpl).Document;
        Assert.Equal('0', doc.DefaultFont!.FontId);
        Assert.Equal(FieldRotation.Normal, doc.DefaultRotation);

        var code128 = Assert.IsType<BarcodeElement>(doc.Elements[0]);
        Assert.Equal((3, 2.5, 60, Code128Mode.None), (code128.ModuleWidth, code128.WideBarRatio, code128.Height, code128.Code128Mode));
        Assert.True(code128.HumanReadable);
        var code39 = Assert.IsType<BarcodeElement>(doc.Elements[1]);
        Assert.Equal((50, false), (code39.Height, code39.HumanReadable));

        var inherited = Assert.IsType<TextElement>(doc.Elements[2]);
        Assert.Equal(('A', 20, 10, FieldRotation.Rotated90), (inherited.FontId, inherited.FontHeight, inherited.FontWidth, inherited.Rotation));
        var explicitText = Assert.IsType<TextElement>(doc.Elements[3]);
        Assert.Equal(('0', 40, 40, FieldRotation.Normal), (explicitText.FontId, explicitText.FontHeight, explicitText.FontWidth, explicitText.Rotation));
    }

    [Fact]
    public void Text_without_any_cf_gets_the_printer_default_font_explicitly()
    {
        var doc = ZplParser.Parse("^XA^PW400^LL200^FO5,5^FDDefault font^FS^XZ").Document;
        Assert.Null(doc.DefaultFont);
        var t = Assert.IsType<TextElement>(Assert.Single(doc.Elements));
        Assert.Equal(('A', 9, 5), (t.FontId, t.FontHeight, t.FontWidth));
    }

    [Fact]
    public void Units_in_millimetres_are_converted_to_dots()
    {
        var r = ZplParser.Parse("^XA^MUm^PW100^LL50^FO10,5^GB20,0.5,0.5^FS^XZ");
        // 203 dpi is 7.992 dots per mm.
        Assert.Equal(799, r.Document.WidthDots);
        var line = Assert.IsType<LineElement>(Assert.Single(r.Document.Elements));
        Assert.Equal((80, 40), (line.X, line.Y));
        Assert.Equal(160, line.LengthDots);
        Assert.Contains("Import.Units.Converted", Keys(r));
    }

    [Fact]
    public void Omitted_pq_replicates_and_missing_settings_use_model_defaults_with_warnings()
    {
        var r = ZplParser.Parse("^XA^PQ5,0,0,N^XZ");
        Assert.Equal((5, 1), (r.Document.Print.Quantity, r.Document.Print.Replicates));
        Assert.Contains("Import.Label.NoWidth", Keys(r));
        Assert.Contains("Import.Label.NoLength", Keys(r));
    }

    // ---- encodings ------------------------------------------------------------------------

    [Fact]
    public void Ci13_is_code_page_850_and_ci27_is_code_page_1252()
    {
        Assert.True(ZplEncoding.TryFromCiCode(13, out var e13, out _));
        Assert.Equal(850, e13.DotNetCodePage);
        Assert.True(ZplEncoding.TryFromCiCode(27, out var e27, out _));
        Assert.Equal(1252, e27.DotNetCodePage);
        Assert.True(ZplEncoding.TryFromCiCode(28, out var e28, out _));
        Assert.True(e28.IsUtf8);
        Assert.False(ZplEncoding.TryFromCiCode(30, out _, out _));
    }

    [Fact]
    public void Hex_escapes_are_decoded_with_the_active_ci()
    {
        // The n-tilde is 0xA4 in code page 850, 0xF1 in 1252 and two bytes in UTF-8.
        Assert.Equal("Niño", Text("^CI13^FH_^FDNi_A4o^FS"));
        Assert.Equal("Niño", Text("^CI27^FH_^FDNi_F1o^FS"));
        Assert.Equal("Niño", Text("^CI28^FH_^FDNi_C3_B1o^FS"));
        Assert.Equal("Niño", Text("^CI28^FH\\^FDNi\\C3\\B1o^FS"));
        Assert.Equal("a_b", Text("^CI28^FDa_b^FS"));
    }

    [Fact]
    public void Raw_bytes_are_decoded_with_the_active_ci_when_parsing_bytes()
    {
        byte[] cp850 = Concat("^XA^CI13^FO0,0^FDNi", new byte[] { 0xA4 }, "o^FS^XZ");
        Assert.Equal("Niño", Assert.IsType<TextElement>(Assert.Single(ZplParser.Parse(cp850).Document.Elements)).Text);

        byte[] utf8 = Concat("^XA^CI28^FO0,0^FDNi", new byte[] { 0xC3, 0xB1 }, "o^FS^XZ");
        Assert.Equal("Niño", Assert.IsType<TextElement>(Assert.Single(ZplParser.Parse(utf8).Document.Elements)).Text);

        // Invalid UTF-8 never loses the text: it is shown as Latin-1 and the user is told.
        byte[] broken = Concat("^XA^CI28^FO0,0^FDNi", new byte[] { 0xF1 }, "o^FS^XZ");
        var r = ZplParser.Parse(broken);
        Assert.Equal("Niño", Assert.IsType<TextElement>(Assert.Single(r.Document.Elements)).Text);
        Assert.Contains("Import.Text.DecodeFailed", Keys(r));
    }

    [Fact]
    public void Files_without_ci_use_the_printer_power_up_code_page_850()
    {
        // ^CI0 is the power-up state and 0-12 are Zebra code page 850, so a legacy file that never sets ^CI means 850.
        var hex = ZplParser.Parse("^XA^FO0,0^FH_^FDNi_A4o^FS^XZ");
        Assert.Equal("Niño", Assert.IsType<TextElement>(Assert.Single(hex.Document.Elements)).Text);
        Assert.DoesNotContain("Import.Text.DecodeFailed", Keys(hex));

        var raw = ZplParser.Parse(Concat("^XA^FO0,0^FDNi", new byte[] { 0xA4 }, "o^FS^XZ"));
        Assert.Equal("Niño", Assert.IsType<TextElement>(Assert.Single(raw.Document.Elements)).Text);
        Assert.DoesNotContain("Import.Text.DecodeFailed", Keys(raw));

        // Valid UTF-8 is taken as deliberate even without ^CI.
        var utf8 = ZplParser.Parse(Concat("^XA^FO0,0^FDNi", new byte[] { 0xC3, 0xB1 }, "o^FS^XZ"));
        Assert.Equal("Niño", Assert.IsType<TextElement>(Assert.Single(utf8.Document.Elements)).Text);
    }

    [Fact]
    public void Raw_blocks_holding_high_bytes_are_flagged()
    {
        var r = ZplParser.Parse(Concat("^XA^FO0,0^FN1^FDNi", new byte[] { 0xA4 }, "o^FS^XZ"));
        Assert.Contains("Import.Raw.NonAscii", Keys(r));
        Assert.DoesNotContain("Import.Raw.NonAscii", Keys(ZplParser.Parse("^XA^FO0,0^FN1^FDplain^FS^XZ")));
    }

    [Fact]
    public void Unsupported_and_approximated_ci_values_warn()
    {
        Assert.Contains("Import.Encoding.Unsupported", Keys(ZplParser.Parse("^XA^CI30^XZ")));
        Assert.Contains("Import.Encoding.NationalSet", Keys(ZplParser.Parse("^XA^CI7^XZ")));
        Assert.Contains("Import.Encoding.RemapIgnored", Keys(ZplParser.Parse("^XA^CI13,21,36^XZ")));
    }

    private static string Text(string field)
    {
        var el = ZplParser.Parse("^XA^PW400^LL200^FO0,0" + field + "^XZ").Document.Elements.OfType<TextElement>().Single();
        return el.Text;
    }

    private static byte[] Concat(string head, byte[] middle, string tail) =>
        Encoding.ASCII.GetBytes(head).Concat(middle).Concat(Encoding.ASCII.GetBytes(tail)).ToArray();

    // ---- barcodes -------------------------------------------------------------------------

    [Fact]
    public void Qr_header_and_code128_invocation_are_interpreted()
    {
        var els = ZplParser.Parse("^XA^FO0,0^BQN,2,6^FDHM,https://x.y/?a=1^FS^FO0,0^BCN,50,N,N,N^FD>;1234^FS^XZ").Document.Elements;
        var qr = Assert.IsType<BarcodeElement>(els[0]);
        Assert.Equal(("https://x.y/?a=1", QrErrorCorrection.High, 6, 2), (qr.Data, qr.QrErrorCorrection, qr.ModuleSize, qr.QrModel));
        var c128 = Assert.IsType<BarcodeElement>(els[1]);
        Assert.Equal(("1234", Code128Mode.Automatic), (c128.Data, c128.Code128Mode));
    }

    // ---- multiple labels, malformed input -------------------------------------------------

    [Fact]
    public void Several_labels_are_all_returned()
    {
        var r = ParseFixture("multiple_labels");
        Assert.Equal(2, r.Documents.Count);
        Assert.Equal("First", Assert.IsType<TextElement>(Assert.Single(r.Documents[0].Elements)).Text);
        Assert.Equal("Second", Assert.IsType<TextElement>(Assert.Single(r.Documents[1].Elements)).Text);
        // The printer keeps ^PW/^LL between labels, so the second label has the same size.
        Assert.Equal(800, r.Documents[1].WidthDots);
        Assert.Contains("Import.Label.Multiple", Keys(r));
    }

    [Fact]
    public void Malformed_file_is_reported_not_thrown()
    {
        var r = ParseFixture("malformed_truncated");
        Assert.NotNull(r.Document);
        var keys = Keys(r);
        Assert.Contains("Import.Field.MissingEnd", keys);
        Assert.Contains("Import.Label.NoEnd", keys);
        Assert.Contains(r.Document.Elements, e => e is TextElement { Text: "No terminator" });
        Assert.Contains(r.Document.Elements, e => e is BarcodeElement { Data: "12345" });
        // Whatever was imported must still generate without crashing.
        _ = ZplGenerator.Generate(r.Document);
    }

    public static TheoryData<string> Garbage => new()
    {
        "", " ", "^", "~", "^^^", "^XA", "^XZ", "^XA^XA^XZ^XZ", "^FO", "^FD", "^FS", "^A", "^A0", "^GF", "^GFA", "^GFA,,,,",
        "^XA^GFA,99999999999,99999999999,1,FF^FS^XZ", "^XA^GFA,-5,-5,-1,FF^FS^XZ", "^XA^GFA,8,8,2,:Z64:%%%^FS^XZ",
        "^XA^PW99999999999999999999^LL-5^LH1e999,^PQ-1,-1,-1^BY99999999999^XZ", "^XA^FO1e400,NaN^FDx^FS^XZ",
        "^XA^BQN,2,0^FD^FS^XZ", "^XA^B7,,,,^FD^FS^XZ", "^XA^FB^FD\\&\\&^FS^XZ", "^XA^FH^FD_^FS^XZ", "^XA^FH^FD_G1_^FS^XZ",
        "\0\0\0^XA\u0001\u0002^XZ", "^XA^MU^MUx,a,b^MUm,0,0^XZ", "^XA^CI^CIx^CI99999999999^XZ", "^XA^A@N,10,10,E:FONT.TTF^FDhi^FS^XZ",
        "~DGR:LOGO.GRF,10,2,FFFF\n^XA^FO0,0^XGR:LOGO.GRF,1,1^FS^XZ",
    };

    [Theory]
    [MemberData(nameof(Garbage))]
    public void Hostile_input_never_throws(string input)
    {
        var r = ZplParser.Parse(input);
        Assert.NotNull(r.Document);
        Assert.NotEmpty(r.Documents);
        _ = ZplGenerator.Generate(r.Document);
        Assert.NotNull(ZplParser.Parse(Encoding.Latin1.GetBytes(input)).Document);
    }

    [Fact]
    public void Every_prefix_of_a_real_file_parses_without_throwing()
    {
        string text = ImportFixtureText("legacy_gb_bars_code128");
        for (int len = 0; len <= text.Length; len += 3)
        {
            var r = ZplParser.Parse(text[..len]);
            Assert.NotNull(r.Document);
            _ = ZplGenerator.Generate(r.Document);
        }
    }

    [Fact]
    public void Missing_file_returns_a_warning_instead_of_throwing()
    {
        var r = ZplParser.ParseFile(Path.Combine(FixturesDir(), "import", "does-not-exist.zpl"));
        Assert.Contains("Import.File.Unreadable", Keys(r));
        Assert.NotNull(r.Document);
    }

    [Fact]
    public void Warnings_convert_to_validation_issues()
    {
        var w = Assert.Single(ZplParser.Parse("^XA^PW10^LL10^LS5^XZ").Warnings);
        var issue = w.ToValidationIssue();
        Assert.Equal(Rotula.Core.Validation.ValidationSeverity.Warning, issue.Severity);
        Assert.Equal("Import.Command.Unknown", issue.MessageKey);
        Assert.Equal(new[] { "^LS" }, issue.Args);
    }
}
