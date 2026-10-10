using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>
/// The design commands added to the ZPL engine: diagonal lines, ellipses, symbols, text boxes, label shift and top, reverse, print mode,
/// mirror, print rate, darkness, font ids, field options, printer-side serials and downloaded graphics. Each one is checked three
/// ways: the exact ZPL it writes, what the parser reads, and that a round trip keeps everything.
/// </summary>
public class ZplNewCommandsTests
{
    private static string Wrap(string body) => "^XA^PW800^LL600" + body + "^XZ";

    private static ZplParseResult Parse(string body) => ZplParser.Parse(Wrap(body));

    private static string[] Keys(ZplParseResult r) => r.Warnings.Select(w => w.MessageKey).ToArray();

    /// <summary>Generates the ZPL lines between the fixed header and ^PQ, so tests can compare just the interesting part.</summary>
    private static string Body(LabelDocument doc)
    {
        var result = ZplGenerator.Generate(doc);
        Assert.False(result.HasErrors, string.Join(", ", result.Issues.Select(i => i.MessageKey)));
        var lines = result.Zpl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int fw = Array.FindIndex(lines, l => l.StartsWith("^FW", StringComparison.Ordinal));
        int pq = Array.FindIndex(lines, l => l.StartsWith("^PQ", StringComparison.Ordinal));
        // Skip ^FW and the optional ^CF/^CW lines that follow it.
        int start = fw + 1;
        while (start < pq && (lines[start].StartsWith("^CF", StringComparison.Ordinal) || lines[start].StartsWith("^CW", StringComparison.Ordinal))) start++;
        return string.Join("\n", lines[start..pq]);
    }

    private static LabelDocument RoundTrip(LabelDocument doc)
    {
        var result = ZplGenerator.Generate(doc);
        Assert.False(result.HasErrors, string.Join(", ", result.Issues.Select(i => i.MessageKey)));
        var back = ZplParser.Parse(result.Zpl);
        ZplParserTestSupport.AssertSame(doc, back.Document);
        // And once more: generating from the re-read document gives the same text (a fixpoint).
        Assert.Equal(result.Zpl, ZplGenerator.Generate(back.Document).Zpl);
        return back.Document;
    }

    private static LabelDocument Doc(params LabelElement[] elements)
    {
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(600), Dpi = PrinterDpi.Dpi203 };
        doc.Elements.AddRange(elements);
        return doc;
    }

    // ---- ^GD ------------------------------------------------------------------------------------

    [Fact]
    public void Diagonal_line_is_written_with_every_parameter_and_read_back()
    {
        var doc = Doc(new DiagonalLineElement { X = 10, Y = 20, WidthDots = 150, HeightDots = 100, ThicknessDots = 4, Color = GraphicColor.White, Lean = DiagonalLean.Left });
        Assert.Equal("^FO10,20^GD150,100,4,W,L^FS", Body(doc));
        var back = Assert.IsType<DiagonalLineElement>(Assert.Single(RoundTrip(doc).Elements));
        Assert.Equal((150, 100, 4, GraphicColor.White, DiagonalLean.Left), (back.WidthDots, back.HeightDots, back.ThicknessDots, back.Color, back.Lean));
    }

    [Theory]
    [InlineData("^GD50,40,3", DiagonalLean.Right)]
    [InlineData("^GD50,40,3,B,R", DiagonalLean.Right)]
    [InlineData("^GD50,40,3,B,/", DiagonalLean.Right)]
    [InlineData("^GD50,40,3,B,L", DiagonalLean.Left)]
    [InlineData("^GD50,40,3,B,\\", DiagonalLean.Left)]
    public void Diagonal_line_orientation_defaults_to_right_and_accepts_the_slash_forms(string command, DiagonalLean lean)
    {
        var r = Parse("^FO0,0" + command + "^FS");
        Assert.Equal(lean, Assert.IsType<DiagonalLineElement>(Assert.Single(r.Document.Elements)).Lean);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Diagonal_line_sizes_default_to_the_thickness_and_stay_in_range()
    {
        var d = Assert.IsType<DiagonalLineElement>(Assert.Single(Parse("^FO0,0^GD,,7^FS").Document.Elements));
        Assert.Equal((7, 7, 7), (d.WidthDots, d.HeightDots, d.ThicknessDots));
        var tiny = Assert.IsType<DiagonalLineElement>(Assert.Single(Parse("^FO0,0^GD1,1,1^FS").Document.Elements));
        Assert.Equal((3, 3), (tiny.WidthDots, tiny.HeightDots)); // the guide's minimum is 3
    }

    [Fact]
    public void Diagonal_line_with_out_of_range_size_is_an_error_when_generating()
    {
        var result = ZplGenerator.Generate(Doc(new DiagonalLineElement { WidthDots = 2 }));
        Assert.Contains(result.Issues, i => i.MessageKey == "Graphic.Diagonal.Range");
    }

    // ---- ^GE ------------------------------------------------------------------------------------

    [Fact]
    public void Ellipse_is_written_and_read_back_including_a_thickness_of_one()
    {
        var doc = Doc(new EllipseElement { X = 5, Y = 6, WidthDots = 200, HeightDots = 100, ThicknessDots = 1, Color = GraphicColor.White });
        Assert.Equal("^FO5,6^GE200,100,1,W^FS", Body(doc));
        var back = Assert.IsType<EllipseElement>(Assert.Single(RoundTrip(doc).Elements));
        Assert.Equal(1, back.ThicknessDots); // the guide lists 2 as the smallest value but 1 as the default, so 1 must survive
    }

    [Fact]
    public void Ellipse_sizes_default_to_the_thickness_and_larger_values_are_limited_to_4095()
    {
        var e = Assert.IsType<EllipseElement>(Assert.Single(Parse("^FO0,0^GE,,9^FS").Document.Elements));
        Assert.Equal((9, 9, 9), (e.WidthDots, e.HeightDots, e.ThicknessDots));
        var big = Assert.IsType<EllipseElement>(Assert.Single(Parse("^FO0,0^GE9000,9000,5^FS").Document.Elements));
        Assert.Equal((4095, 4095), (big.WidthDots, big.HeightDots));
    }

    // ---- ^GS ------------------------------------------------------------------------------------

    [Fact]
    public void Symbol_is_written_with_explicit_height_and_width_and_read_back()
    {
        var doc = Doc(new SymbolElement { X = 10, Y = 10, Symbol = GraphicSymbol.Trademark, HeightDots = 40, WidthDots = 30, Rotation = FieldRotation.Rotated90 });
        Assert.Equal("^FO10,10^GSR,40,30^FDC^FS", Body(doc));
        var back = Assert.IsType<SymbolElement>(Assert.Single(RoundTrip(doc).Elements));
        Assert.Equal((GraphicSymbol.Trademark, 40, 30, FieldRotation.Rotated90), (back.Symbol, back.HeightDots, back.WidthDots, back.Rotation));
    }

    [Theory]
    [InlineData('A', GraphicSymbol.Registered)]
    [InlineData('B', GraphicSymbol.Copyright)]
    [InlineData('C', GraphicSymbol.Trademark)]
    [InlineData('D', GraphicSymbol.UnderwritersLaboratories)]
    [InlineData('E', GraphicSymbol.CanadianStandards)]
    public void Symbol_letters_map_to_the_five_symbols(char letter, GraphicSymbol expected)
    {
        var s = Assert.IsType<SymbolElement>(Assert.Single(Parse($"^CFA,22,11^FO0,0^GS^FD{letter}^FS").Document.Elements));
        Assert.Equal(expected, s.Symbol);
        // Without parameters the sizes are the last ^CF values.
        Assert.Equal((22, 11), (s.HeightDots, s.WidthDots));
    }

    [Fact]
    public void Symbol_with_an_unknown_letter_is_kept_raw_with_a_warning()
    {
        var r = Parse("^FO0,0^GSN,30,30^FDZ^FS");
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { IsVisual: true, Zpl: "^FO0,0^GSN,30,30^FDZ^FS" });
        Assert.Contains("Import.Parameter.Invalid", Keys(r));
    }

    // ---- ^TB ------------------------------------------------------------------------------------

    [Fact]
    public void Text_box_is_written_with_its_rotation_and_read_back()
    {
        var doc = Doc(new TextElement { X = 10, Y = 10, Text = "Hello world", FontId = '0', FontHeight = 30, FontWidth = 30, Rotation = FieldRotation.Normal, TextBox = new TextBoxOptions { WidthDots = 300, HeightDots = 90 } });
        Assert.Equal("^FO10,10^A0N,30,30^TBN,300,90^FDHello world^FS", Body(doc));
        var back = Assert.IsType<TextElement>(Assert.Single(RoundTrip(doc).Elements));
        Assert.Equal((300, 90), (back.TextBox!.WidthDots, back.TextBox.HeightDots));
        Assert.Null(back.Block);
    }

    [Fact]
    public void Text_box_escapes_a_less_than_sign_and_reads_it_back()
    {
        var doc = Doc(new TextElement { X = 0, Y = 0, Text = "a < b", FontId = '0', FontHeight = 30, FontWidth = 30, TextBox = new TextBoxOptions { WidthDots = 200, HeightDots = 60 } });
        Assert.Contains("^FDa <<> b^FS", Body(doc));
        Assert.Equal("a < b", Assert.IsType<TextElement>(Assert.Single(RoundTrip(doc).Elements)).Text);
    }

    [Fact]
    public void Text_box_with_an_unreadable_escape_sequence_is_kept_raw()
    {
        var r = Parse("^FO0,0^A0N,30,30^TBN,200,60^FDsoft<hyphen>here^FS");
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { IsVisual: true });
        Assert.Contains("Import.Field.OptionNotApplicable", Keys(r));
    }

    [Fact]
    public void Text_box_together_with_a_block_is_an_error_and_a_conflict_on_import()
    {
        var result = ZplGenerator.Generate(Doc(new TextElement { Text = "x", Block = new TextBlockOptions(), TextBox = new TextBoxOptions() }));
        Assert.Contains(result.Issues, i => i.MessageKey == "Text.Box.WithBlock");
        Assert.Contains("Import.Field.OptionNotApplicable", Keys(Parse("^FO0,0^A0N,30,30^FB200,2^TBN,200,60^FDx^FS")));
    }

    [Fact]
    public void Text_box_size_defaults_to_one_dot_and_takes_its_rotation_from_the_command()
    {
        var t = Assert.IsType<TextElement>(Assert.Single(Parse("^FO0,0^A0N,30,30^TBR^FDx^FS").Document.Elements));
        Assert.Equal((1, 1), (t.TextBox!.WidthDots, t.TextBox.HeightDots));
        Assert.Equal(FieldRotation.Rotated90, t.Rotation);
    }

    // ---- ^LS ^LT ^LR ----------------------------------------------------------------------------

    [Fact]
    public void Label_shift_and_top_are_header_settings_that_round_trip_and_stay_out_when_zero()
    {
        var plain = ZplGenerator.Generate(Doc()).Zpl;
        Assert.DoesNotContain("^LS", plain);
        Assert.DoesNotContain("^LT", plain);

        var doc = Doc(new TextElement { Text = "x", FontId = '0', FontHeight = 30, FontWidth = 30 });
        doc.LabelShift = -15;
        doc.LabelTop = 12;
        string zpl = ZplGenerator.Generate(doc).Zpl;
        Assert.Contains("\n^LS-15\n^LT12\n", zpl);
        Assert.True(zpl.IndexOf("^LS", StringComparison.Ordinal) < zpl.IndexOf("^FO", StringComparison.Ordinal), "^LS must come before the first field");
        var back = RoundTrip(doc);
        Assert.Equal((-15, 12), (back.LabelShift, back.LabelTop));
    }

    [Fact]
    public void Label_shift_and_top_out_of_range_are_warned_and_ignored()
    {
        var r = Parse("^LS20000^LT500^PW100^LL100");
        Assert.Equal((0, 0), (r.Document.LabelShift, r.Document.LabelTop));
        Assert.Equal(2, Keys(r).Count(k => k == "Import.Parameter.OutOfRange"));
        Assert.Contains(ZplGenerator.Generate(new LabelDocument { LabelShift = 10000 }).Issues, i => i.MessageKey == "Label.Shift.Range");
        Assert.Contains(ZplGenerator.Generate(new LabelDocument { LabelTop = 300 }).Issues, i => i.MessageKey == "Label.Top.Range");
    }

    [Fact]
    public void Label_shift_and_top_carry_over_to_the_next_label_of_a_file()
    {
        var r = ZplParser.Parse("^XA^LS30^LT5^PW400^LL200^XZ^XA^FO0,0^A0N,30,30^FDx^FS^XZ");
        Assert.Equal(2, r.Documents.Count);
        Assert.All(r.Documents, d => Assert.Equal((30, 5), (d.LabelShift, d.LabelTop)));
    }

    [Fact]
    public void Label_reverse_marks_only_the_fields_that_follow_it()
    {
        var r = Parse("^FO0,0^GB50,50,50^FS^LRY^FO10,10^A0N,30,30^FDa^FS^FO10,60^GB40,40,4^FS^LRN^FO10,120^A0N,30,30^FDb^FS");
        Assert.Equal(new[] { false, true, true, false }, r.Document.Elements.Select(e => e.ReverseField).ToArray());
        Assert.Empty(r.Warnings);
        // The generator writes ^FR per field, so the printed label is identical.
        string zpl = ZplGenerator.Generate(r.Document).Zpl;
        Assert.Equal(2, zpl.Split("^FR").Length - 1);
        ZplParserTestSupport.AssertSame(r.Document, ZplParser.Parse(zpl).Document);
    }

    [Fact]
    public void Label_reverse_reaches_a_field_that_is_kept_raw()
    {
        var r = Parse("^LRY^FO10,10^FMx^A0N,30,30^FDa^FS");
        var raw = Assert.IsType<RawZplElement>(Assert.Single(r.Document.Elements));
        Assert.Equal("^FO10,10^FR^FMx^A0N,30,30^FDa^FS", raw.Zpl);
    }

    [Fact]
    public void Label_reverse_state_carries_into_the_next_label_like_a_printer_would()
    {
        var r = ZplParser.Parse("^XA^LRY^XZ^XA^FO0,0^A0N,30,30^FDx^FS^XZ");
        Assert.True(r.Documents[1].Elements[0].ReverseField);
    }

    // ---- ^MM ^PM ^PR ^MD --------------------------------------------------------------------------

    [Theory]
    [InlineData("^MMT", PrintMode.Tear, false, "^MMT")]
    [InlineData("^MMP,Y", PrintMode.Peel, true, "^MMP,Y")]
    [InlineData("^MMc,n", PrintMode.Cutter, false, "^MMC")]
    [InlineData("^MMD", PrintMode.DelayedCutter, false, "^MMD")]
    [InlineData("^MMK", PrintMode.Kiosk, false, "^MMK")]
    [InlineData("^MMF", PrintMode.Rfid, false, "^MMF")]
    public void Print_mode_round_trips(string input, PrintMode mode, bool prePeel, string written)
    {
        var doc = Parse(input).Document;
        Assert.Equal((mode, prePeel), (doc.PrintMode, doc.PrePeel));
        string zpl = ZplGenerator.Generate(doc).Zpl;
        Assert.Contains("\n" + written + "\n", zpl);
        ZplParserTestSupport.AssertSame(doc, ZplParser.Parse(zpl).Document);
    }

    [Fact]
    public void Print_mode_is_not_written_unless_it_was_chosen_and_the_reserved_modes_stay_raw()
    {
        Assert.DoesNotContain("^MM", ZplGenerator.Generate(Doc()).Zpl);
        var r = Parse("^MMU");
        Assert.Null(r.Document.PrintMode);
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { Zpl: "^MMU" });
        Assert.Contains("Import.Setting.Unsupported", Keys(r));
    }

    [Fact]
    public void Mirror_is_written_only_when_on_and_round_trips()
    {
        Assert.DoesNotContain("^PM", ZplGenerator.Generate(Doc()).Zpl);
        var doc = Doc();
        doc.MirrorImage = true;
        Assert.Contains("\n^PMY\n", ZplGenerator.Generate(doc).Zpl);
        Assert.True(RoundTrip(doc).MirrorImage);
    }

    [Theory]
    [InlineData("^PRC,D,A", 4, 6, 2, "^PR4,6,2")]
    [InlineData("^PR3", 3, null, null, "^PR3")]
    [InlineData("^PR,8", null, 8, null, "^PR,8")]
    [InlineData("^PR,,E", null, null, 8, "^PR,,8")]
    [InlineData("^PR12,1,14", 12, 1, 14, "^PR12,1,14")]
    public void Print_rate_reads_letters_and_numbers_and_writes_numbers(string input, int? speed, int? slew, int? back, string written)
    {
        var doc = Parse(input).Document;
        Assert.Equal((speed, slew, back), (doc.PrintRate!.Speed, doc.PrintRate.SlewSpeed, doc.PrintRate.BackfeedSpeed));
        string zpl = ZplGenerator.Generate(doc).Zpl;
        Assert.Contains("\n" + written + "\n", zpl);
        ZplParserTestSupport.AssertSame(doc, ZplParser.Parse(zpl).Document);
    }

    [Fact]
    public void Print_rate_rejects_values_outside_1_to_14()
    {
        var r = Parse("^PR20,0,F");
        Assert.Null(r.Document.PrintRate);
        Assert.Equal(3, Keys(r).Count(k => k == "Import.Parameter.Invalid"));
        Assert.Contains(ZplGenerator.Generate(new LabelDocument { PrintRate = new PrintRateOptions { Speed = 15 } }).Issues, i => i.MessageKey == "Print.Rate.Range");
        Assert.DoesNotContain("^PR", ZplGenerator.Generate(new LabelDocument { PrintRate = new PrintRateOptions() }).Zpl);
    }

    [Fact]
    public void Print_rate_keeps_the_speeds_a_later_command_does_not_mention()
    {
        var doc = Parse("^PR4,6,2^PR,,3").Document;
        Assert.Equal((4, 6, 3), (doc.PrintRate!.Speed, doc.PrintRate.SlewSpeed, doc.PrintRate.BackfeedSpeed));
    }

    [Theory]
    [InlineData("^MD-5", "-5")]
    [InlineData("^MD15", "15")]
    [InlineData("^MD8.3", "8.3")]
    [InlineData("^MD0", "0")]
    public void Media_darkness_accepts_decimals_and_negative_values(string input, string expected)
    {
        var doc = Parse(input).Document;
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), doc.MediaDarkness);
        string zpl = ZplGenerator.Generate(doc).Zpl;
        Assert.Contains("\n^MD" + expected + "\n", zpl);
        Assert.Equal(doc.MediaDarkness, ZplParser.Parse(zpl).Document.MediaDarkness);
    }

    [Fact]
    public void Media_darkness_out_of_range_is_warned_and_the_command_without_a_value_is_ignored()
    {
        var r = Parse("^MD40");
        Assert.Null(r.Document.MediaDarkness);
        Assert.Contains("Import.Parameter.OutOfRange", Keys(r));
        Assert.Empty(Parse("^MD").Warnings);
        Assert.Contains(ZplGenerator.Generate(new LabelDocument { MediaDarkness = 31 }).Issues, i => i.MessageKey == "Print.Darkness.Range");
    }

    [Fact]
    public void Media_darkness_is_written_the_same_way_in_every_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            Assert.Contains("\n^MD8.3\n", ZplGenerator.Generate(new LabelDocument { MediaDarkness = 8.3m }).Zpl);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    // ---- ^CW ------------------------------------------------------------------------------------

    [Fact]
    public void Font_alias_is_a_document_setting_written_after_the_default_font()
    {
        var doc = Parse("^CWq,e:arial.ttf^CWA,R:MYFONT.FNT").Document;
        Assert.Equal(new[] { "Q=E:ARIAL.TTF", "A=R:MYFONT.FNT" }, doc.FontAliases.Select(a => a.Letter + "=" + a.Path).ToArray());
        string zpl = ZplGenerator.Generate(doc).Zpl;
        Assert.Contains("\n^CWQ,E:ARIAL.TTF\n^CWA,R:MYFONT.FNT\n", zpl);
        ZplParserTestSupport.AssertSame(doc, ZplParser.Parse(zpl).Document);
    }

    [Fact]
    public void Font_alias_assigned_twice_keeps_the_last_and_a_late_or_invalid_one_stays_raw()
    {
        Assert.Equal("R:B.TTF", Assert.Single(Parse("^CWQ,R:A.TTF^CWQ,R:B.TTF").Document.FontAliases).Path);

        var late = Parse("^FO0,0^A0N,30,30^FDx^FS^CWQ,R:A.TTF");
        Assert.Empty(late.Document.FontAliases);
        Assert.Contains(late.Document.Elements, e => e is RawZplElement { Zpl: "^CWQ,R:A.TTF" });
        Assert.Contains("Import.Font.AliasLate", Keys(late));

        var bad = Parse("^CWQ,not a path");
        Assert.Empty(bad.Document.FontAliases);
        Assert.Contains("Import.Parameter.Invalid", Keys(bad));
    }

    // ---- field options ----------------------------------------------------------------------------

    [Fact]
    public void Field_number_with_a_prompt_round_trips_on_text_and_barcodes()
    {
        var doc = Doc(
            new TextElement { X = 10, Y = 10, Text = "Sample", FontId = '0', FontHeight = 30, FontWidth = 30, FieldNumber = 7, FieldPrompt = "Customer name" },
            new BarcodeElement { X = 10, Y = 100, Symbology = BarcodeSymbology.Code128, Data = "123", FieldNumber = 8 });
        string body = Body(doc);
        Assert.Contains("^FN7\"Customer name\"^FDSample^FS", body);
        Assert.Contains("^FN8^FD123^FS", body);
        var back = RoundTrip(doc);
        Assert.Equal((7, "Customer name"), ((FieldDataElement)back.Elements[0]).FieldNumber is { } n ? (n, ((FieldDataElement)back.Elements[0]).FieldPrompt) : (-1, null));
    }

    [Fact]
    public void A_numbered_field_without_data_only_refers_to_another_field()
    {
        var r = Parse("^FO10,10^A0N,30,30^FN3^FS");
        var t = Assert.IsType<TextElement>(Assert.Single(r.Document.Elements));
        Assert.Equal((3, ""), (t.FieldNumber, t.Text));
        Assert.Contains("^FN3^FS", Body(r.Document));
        Assert.DoesNotContain("^FD", Body(r.Document));
    }

    [Fact]
    public void Field_number_on_a_graphic_keeps_the_field_raw()
    {
        var r = Parse("^FO10,10^FN1^GB50,50,3^FS");
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { IsVisual: true });
        Assert.Contains("Import.Field.OptionNotApplicable", Keys(r));
    }

    [Fact]
    public void Embed_character_and_clock_indicators_round_trip()
    {
        var doc = Doc(new TextElement { X = 10, Y = 10, Text = "Ref #1# at %H:%M", FontId = '0', FontHeight = 30, FontWidth = 30, EmbedCharacter = '#', Clock = new FieldClockOptions { Primary = '%', Third = '@' } });
        Assert.Contains("^FE#^FC%,,@^FDRef #1# at %H:%M^FS", Body(doc));
        var t = Assert.IsType<TextElement>(Assert.Single(RoundTrip(doc).Elements));
        Assert.Equal(('#', '%', null, '@'), (t.EmbedCharacter!.Value, t.Clock!.Primary, t.Clock.Secondary, t.Clock.Third));
    }

    [Fact]
    public void Clock_indicators_that_clash_are_not_modelled()
    {
        var r = Parse("^FO0,0^A0N,30,30^FC%,%^FD%d^FS");
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { IsVisual: true });
        Assert.Contains("Import.Parameter.Invalid", Keys(r));
        Assert.Contains(ZplGenerator.Generate(Doc(new TextElement { Text = "x", Clock = new FieldClockOptions { Primary = '%', Secondary = '%' } })).Issues, i => i.MessageKey == "Field.Clock.Invalid");
    }

    [Fact]
    public void Field_direction_and_gap_round_trip_for_text_only()
    {
        var doc = Doc(new TextElement { X = 10, Y = 10, Text = "tall", FontId = '0', FontHeight = 30, FontWidth = 30, Direction = FieldDirection.Vertical, InterCharacterGap = 4 });
        Assert.Equal("^FO10,10^FPV,4^A0,30,30^FDtall^FS", Body(doc));
        var back = Assert.IsType<TextElement>(Assert.Single(RoundTrip(doc).Elements));
        Assert.Equal((FieldDirection.Vertical, 4), (back.Direction, back.InterCharacterGap));
        Assert.Contains("^FPR^", Body(Doc(new TextElement { Text = "x", Direction = FieldDirection.Reverse })));
        Assert.Contains("Import.Field.OptionNotApplicable", Keys(Parse("^FO0,0^FPV^BCN,50^FD123^FS")));
    }

    // ---- ^SN / ^SF ----------------------------------------------------------------------------------

    [Fact]
    public void Serial_number_replaces_the_field_data_and_round_trips_on_text_and_barcodes()
    {
        var doc = Doc(
            new TextElement { X = 10, Y = 10, Text = "0001", FontId = '0', FontHeight = 30, FontWidth = 30, Serial = new PrinterSerialOptions { Increment = 5, LeadingZeros = true } },
            new BarcodeElement { X = 10, Y = 100, Symbology = BarcodeSymbology.Code128, Data = "1000", Serial = new PrinterSerialOptions { Increment = -2 } });
        string body = Body(doc);
        Assert.Contains("^FO10,10^A0,30,30^SN0001,5,Y^FS", body);
        Assert.Contains("^SN1000,-2,N^FS", body);
        Assert.DoesNotContain("^FD", body);
        var back = RoundTrip(doc);
        Assert.Equal(("0001", 5L, true), (((TextElement)back.Elements[0]).Text, ((TextElement)back.Elements[0]).Serial!.Increment, ((TextElement)back.Elements[0]).Serial!.LeadingZeros));
    }

    [Fact]
    public void Serial_number_defaults_are_one_and_no_leading_zeros()
    {
        var t = Assert.IsType<TextElement>(Assert.Single(Parse("^FO0,0^A0N,30,30^SN42^FS").Document.Elements));
        Assert.Equal(("42", PrinterSerialKind.Number, 1L, false), (t.Text, t.Serial!.Kind, t.Serial.Increment, t.Serial.LeadingZeros));
    }

    [Fact]
    public void Serial_number_next_to_field_data_is_ambiguous_and_kept_raw()
    {
        var r = Parse("^FO0,0^A0N,30,30^FDabc^SN1,1,N^FS");
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { IsVisual: true });
        Assert.Contains("Import.Serial.Conflict", Keys(r));
    }

    [Fact]
    public void Serial_mask_follows_the_data_and_round_trips()
    {
        var doc = Doc(new TextElement { X = 10, Y = 10, Text = "AB0001", FontId = '0', FontHeight = 30, FontWidth = 30, Serial = new PrinterSerialOptions { Kind = PrinterSerialKind.Mask, Mask = "AADDDD", IncrementText = "AA0001" } });
        Assert.Equal("^FO10,10^A0,30,30^FDAB0001^SFAADDDD,AA0001^FS", Body(doc));
        var t = Assert.IsType<TextElement>(Assert.Single(RoundTrip(doc).Elements));
        Assert.Equal(("AB0001", "AADDDD", "AA0001"), (t.Text, t.Serial!.Mask, t.Serial.IncrementText));
    }

    [Fact]
    public void Serial_values_that_cannot_be_written_are_errors()
    {
        Assert.Contains(ZplGenerator.Generate(Doc(new TextElement { Text = "abc", Serial = new PrinterSerialOptions() })).Issues, i => i.MessageKey == "Field.Serial.Value");
        Assert.Contains(ZplGenerator.Generate(Doc(new TextElement { Text = "1", Serial = new PrinterSerialOptions { Kind = PrinterSerialKind.Mask, Mask = "Q" } })).Issues, i => i.MessageKey == "Field.Serial.Mask");
        // A QR code needs its "QA," prefix in the data, which a counting number cannot carry.
        Assert.Contains(ZplGenerator.Generate(Doc(new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "123", Serial = new PrinterSerialOptions() })).Issues, i => i.MessageKey == "Field.Serial.Barcode");
    }

    // ---- ~DG / ~DY and recalls ----------------------------------------------------------------------

    private const string Checker = "FF818181818181FF"; // an 8 x 8 box

    [Fact]
    public void Downloaded_graphic_is_stored_resolved_for_its_recall_and_written_before_the_label()
    {
        var r = ZplParser.Parse("~DGR:LOGO.GRF,8,1," + Checker + "\n^XA^PW200^LL100^FO10,10^XGR:LOGO.GRF,2,3^FS^XZ");
        var g = Assert.Single(r.Document.StoredGraphics);
        Assert.Equal(("R:LOGO.GRF", 1, 8, 8), (g.Path, g.BytesPerRow, g.WidthDots, g.HeightDots));
        Assert.Equal(Checker, Convert.ToHexString(g.Data));
        Assert.DoesNotContain("Import.Recall.Graphic.Unavailable", Keys(r));
        Assert.Contains(r.Document.Elements, e => e is RecallPlaceholderElement { Command: "XG", Reference: "R:LOGO.GRF" });

        string zpl = ZplGenerator.Generate(r.Document).Zpl;
        Assert.StartsWith("~DGR:LOGO.GRF,8,1," + Checker + "\n^XA\n", zpl);
        Assert.Contains("^FO10,10^XGR:LOGO.GRF,2,3^FS", zpl);
        ZplParserTestSupport.AssertSame(r.Document, ZplParser.Parse(zpl).Document);
    }

    [Fact]
    public void A_recall_of_something_not_in_the_file_still_warns()
    {
        var r = Parse("^FO0,0^XGR:OTHER.GRF,1,1^FS");
        Assert.Contains("Import.Recall.Graphic.Unavailable", Keys(r));
        // A graphic downloaded AFTER the recall does not count: the printer does not know it yet.
        var late = ZplParser.Parse("^XA^FO0,0^XGR:LATE.GRF,1,1^FS^XZ\n~DGR:LATE.GRF,8,1," + Checker);
        Assert.Contains("Import.Recall.Graphic.Unavailable", Keys(late));
    }

    [Fact]
    public void Image_move_recall_is_a_field_recall_too()
    {
        var r = ZplParser.Parse("~DGR:LOGO.GRF,8,1," + Checker + "\n^XA^FO0,0^IMR:LOGO.GRF^FS^XZ");
        Assert.Contains(r.Document.Elements, e => e is RecallPlaceholderElement { Command: "IM", EmitAsField: true });
        Assert.DoesNotContain("Import.Recall.Graphic.Unavailable", Keys(r));
        Assert.Contains("^FO0,0^IMR:LOGO.GRF^FS", ZplGenerator.Generate(r.Document).Zpl);
    }

    [Fact]
    public void Download_defaults_follow_the_guide_device_r_and_extension_grf()
    {
        var r = ZplParser.Parse("~DGlogo,8,1," + Checker + "\n^XA^XZ");
        Assert.Equal("R:LOGO.GRF", Assert.Single(r.Document.StoredGraphics).Path);
        Assert.NotNull(StoredGraphic.Find(r.Document.StoredGraphics, "LOGO"));
        Assert.Null(StoredGraphic.Find(r.Document.StoredGraphics, "E:LOGO.GRF")); // a named device must match
    }

    [Fact]
    public void A_recall_with_the_wrong_device_does_not_find_the_graphic()
    {
        var r = ZplParser.Parse("~DGR:LOGO.GRF,8,1," + Checker + "\n^XA^XZ");
        Assert.Null(StoredGraphic.Find(r.Document.StoredGraphics, "E:LOGO.GRF"));
        Assert.NotNull(StoredGraphic.Find(r.Document.StoredGraphics, "LOGO.GRF"));
    }

    [Fact]
    public void Downloads_with_run_length_compression_are_decoded()
    {
        // Two bytes per row; ",": fill the rest of the row with zeros, "!" with ones, ":" repeat the previous row.
        var r = ZplParser.Parse("~DGR:C.GRF,4,2,FFFF,\n^XA^XZ");
        var g = Assert.Single(r.Document.StoredGraphics);
        Assert.Equal("FFFF0000", Convert.ToHexString(g.Data));
    }

    [Fact]
    public void The_plain_bitmap_form_of_the_object_download_is_a_stored_graphic_too()
    {
        var r = ZplParser.Parse("~DYR:LOGO,A,G,8,1," + Checker + "\n^XA^XZ");
        var g = Assert.Single(r.Document.StoredGraphics);
        Assert.Equal("R:LOGO.GRF", g.Path);
        Assert.Equal(Checker, Convert.ToHexString(g.Data));
        Assert.DoesNotContain(r.Warnings, w => w.MessageKey == "Import.Command.Unknown");
    }

    [Fact]
    public void Other_object_downloads_are_kept_raw_and_warned()
    {
        var r = ZplParser.Parse("~DYE:ARIAL,B,T,1000,,\n^XA^XZ");
        Assert.Empty(r.Document.StoredGraphics);
        Assert.Contains("Import.Command.Unknown", Keys(r));
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { Zpl: var z } && z.StartsWith("~DYE:ARIAL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_download_stays_on_the_printer_so_every_later_label_has_it()
    {
        var r = ZplParser.Parse("^XA^FO0,0^A0N,30,30^FDone^FS^XZ\n~DGR:A.GRF,8,1," + Checker + "\n^XA^FO0,0^XGR:A.GRF,1,1^FS^XZ");
        Assert.Equal(2, r.Documents.Count);
        Assert.Empty(r.Documents[0].StoredGraphics); // downloaded after the first label ended
        Assert.Single(r.Documents[1].StoredGraphics);
    }

    [Fact]
    public void A_damaged_download_is_kept_raw_and_does_not_throw()
    {
        var r = ZplParser.Parse("~DGR:BAD.GRF,zz,1,FF\n~DGR:BAD2.GRF,8\n^XA^XZ");
        Assert.Empty(r.Document.StoredGraphics);
        Assert.Contains("Import.Graphic.Invalid", Keys(r));
    }

    [Fact]
    public void A_download_with_a_name_the_printer_would_not_accept_is_kept_raw_so_the_file_can_still_be_exported()
    {
        var r = ZplParser.Parse("~DGR:MUCH_TOO_LONG_NAME.GRF,8,1," + Checker + "\n^XA^PW100^LL100^XZ");
        Assert.Empty(r.Document.StoredGraphics);
        Assert.Contains(r.Document.Elements, e => e is RawZplElement { Zpl: var z } && z.StartsWith("~DGR:MUCH_TOO_LONG_NAME", StringComparison.Ordinal));
        Assert.False(ZplGenerator.Generate(r.Document).HasErrors);
    }

    [Fact]
    public void Downloads_beyond_the_memory_limit_are_refused_not_stored()
    {
        // Each declares nearly 4 MB without sending the data; five of them would be 20 MB.
        string file = string.Concat(Enumerable.Range(0, 6).Select(i => $"~DGR:G{i}.GRF,3999999,1,FF\n")) + "^XA^PW100^LL100^XZ";
        var r = ZplParser.Parse(file);
        Assert.True(r.Document.StoredGraphics.Count <= 4, "stored " + r.Document.StoredGraphics.Count);
        Assert.Contains("Import.Graphic.Invalid", Keys(r));
    }

    [Fact]
    public void An_invalid_stored_graphic_path_is_an_error_when_generating()
    {
        var doc = new LabelDocument();
        doc.StoredGraphics.Add(new StoredGraphic("R:WAY-TOO-LONG-NAME.GRF", 1, new byte[8]));
        Assert.Contains(ZplGenerator.Generate(doc).Issues, i => i.MessageKey == "Graphic.Stored.Invalid");
    }

    // ---- import then export must never lose an element ------------------------------------------

    [Theory]
    [InlineData("^FO0,0^GSN,0,0^FDA^FS")]
    [InlineData("^FO0,0^GS^FDE^FS")]
    [InlineData("^FO0,0^A0N,30,30^SN^FS")]
    [InlineData("^FO0,0^A0N,30,30^SNabc,1,N^FS")]
    [InlineData("^FO0,0^A0N,30,30^SN12345678901234567890123456789012345678901234567890,1,N^FS")]
    [InlineData("^FO0,0^A0N,30,30^SN00#1,1,N^FS")]
    [InlineData("^FO0,0^A0N,30,30^FDabc^SFXX,1^FS")]
    [InlineData("^FO0,0^A0N,30,30^FDabc^SFAD,A!^FS")]
    [InlineData("^FO0,0^A0N,30,30^FDabc^SF^FS")]
    [InlineData("^FO0,0^BQN,2,5^SN123,1,N^FS")]
    [InlineData("^FO0,0^A0N,30,30^FEé^FDx^FS")]
    [InlineData("^FO0,0^A0N,30,30^FCé^FDx^FS")]
    [InlineData("^FO0,0^A0N,30,30^FN12345^FS")]
    [InlineData("^FO0,0^A0N,30,30^FN1\"bad;prompt\"^FS")]
    [InlineData("^FO0,0^A0N,30,30^TB,99999,99999^FDx^FS")]
    [InlineData("^FO0,0^FP,99999^A0N,30,30^FDx^FS")]
    [InlineData("^FO0,0^GD99999,99999,99999^FS")]
    [InlineData("^FO0,0^GE99999,99999,99999^FS")]
    [InlineData("^LS-9999^LT-240^MD-30^PR14,14,14^CWZ,B:A.TTF")]
    public void Whatever_the_importer_accepts_the_generator_can_write_again(string body)
    {
        var r = Parse(body);
        var result = ZplGenerator.Generate(r.Document);
        Assert.False(result.HasErrors, string.Join(", ", result.Issues.Select(i => i.MessageKey)));
        // Nothing may disappear: every field of the input is still in the output.
        Assert.Equal(r.Document.Elements.Count, ZplParser.Parse(result.Zpl).Document.Elements.Count);
    }

    [Fact]
    public void A_file_with_only_downloads_is_one_label_with_the_graphic_once()
    {
        var r = ZplParser.Parse("~DGR:A.GRF,8,1," + Checker);
        Assert.Single(r.Documents);
        Assert.Single(r.Document.StoredGraphics);
        Assert.DoesNotContain("Import.Label.Multiple", Keys(r));
        // An empty input is one empty label, not two.
        Assert.Single(ZplParser.Parse(string.Empty).Documents);
    }

    // ---- ^B0 ------------------------------------------------------------------------------------

    [Fact]
    public void Aztec_written_with_a_zero_is_read_as_aztec()
    {
        var r = Parse("^FO0,0^B0N,4,N,0,N,1^FDAztec^FS");
        var b = Assert.IsType<BarcodeElement>(Assert.Single(r.Document.Elements));
        Assert.Equal(BarcodeSymbology.Aztec, b.Symbology);
        Assert.Empty(r.Warnings);
        Assert.Contains("^BON,4,N,0,N,1", ZplGenerator.Generate(r.Document).Zpl);
    }

    // ---- the DPI converter and the preview -----------------------------------------------------------

    [Fact]
    public void Dpi_conversion_scales_the_new_elements_and_keeps_their_options()
    {
        var doc = Doc(
            new DiagonalLineElement { WidthDots = 100, HeightDots = 50, ThicknessDots = 4 },
            new EllipseElement { WidthDots = 100, HeightDots = 60, ThicknessDots = 4 },
            new SymbolElement { HeightDots = 40, WidthDots = 30 },
            new TextElement { Text = "x", FontId = '0', FontHeight = 30, FontWidth = 30, TextBox = new TextBoxOptions { WidthDots = 200, HeightDots = 60 }, FieldNumber = 4, Serial = new PrinterSerialOptions { Increment = 3 }, InterCharacterGap = 2 },
            new BarcodeElement { Symbology = BarcodeSymbology.Aztec, Data = "x", AztecErrorControl = 101 });
        doc.LabelShift = 20;
        doc.LabelTop = 10;
        doc.MirrorImage = true;
        doc.PrintMode = PrintMode.Peel;
        doc.MediaDarkness = 4;
        doc.FontAliases.Add(new FontAlias('Q', "E:A.TTF"));

        var converted = LabelDpiConverter.Convert(doc, PrinterDpi.Dpi300).Document;
        var diagonal = Assert.IsType<DiagonalLineElement>(converted.Elements[0]);
        Assert.Equal((148, 74), (diagonal.WidthDots, diagonal.HeightDots));
        Assert.IsType<EllipseElement>(converted.Elements[1]);
        var symbol = Assert.IsType<SymbolElement>(converted.Elements[2]);
        Assert.Equal((59, 44), (symbol.HeightDots, symbol.WidthDots));
        var text = Assert.IsType<TextElement>(converted.Elements[3]);
        Assert.Equal((296, 89), (text.TextBox!.WidthDots, text.TextBox.HeightDots));
        Assert.Equal((4, 3L, 3), (text.FieldNumber, text.Serial!.Increment, text.InterCharacterGap));
        Assert.Equal(101, Assert.IsType<BarcodeElement>(converted.Elements[4]).AztecErrorControl);
        Assert.Equal((30, 15, true, PrintMode.Peel, 4m), (converted.LabelShift, converted.LabelTop, converted.MirrorImage, converted.PrintMode, converted.MediaDarkness));
        Assert.Equal("E:A.TTF", Assert.Single(converted.FontAliases).Path);
    }

    private static RenderLabel ToRender(LabelDocument doc) => LabelDocumentAdapter.ToRenderLabel(doc);

    [Fact]
    public void Preview_maps_the_new_elements_and_applies_label_shift_and_top()
    {
        var doc = Doc(
            new EllipseElement { X = 50, Y = 40, WidthDots = 120, HeightDots = 60, ThicknessDots = 3 },
            new DiagonalLineElement { X = 10, Y = 10, WidthDots = 80, HeightDots = 40, ThicknessDots = 3, Lean = DiagonalLean.Left },
            new TextElement { X = 0, Y = 0, Text = "box", FontId = '0', FontHeight = 30, FontWidth = 30, TextBox = new TextBoxOptions { WidthDots = 200, HeightDots = 70 } });
        doc.LabelShift = 15;
        doc.LabelTop = 4;
        var label = ToRender(doc);

        var e = Assert.IsType<RenderEllipse>(label.Elements[0]);
        Assert.Equal((35, 44, 120, 60), (e.X, e.Y, e.WidthDots, e.HeightDots));
        var d = Assert.IsType<RenderDiagonal>(label.Elements[1]);
        Assert.False(d.LeansRight);
        var t = Assert.IsType<RenderText>(label.Elements[2]);
        Assert.Equal((200, 70), (t.WidthDots, t.HeightDots));
    }

    [Fact]
    public void Preview_draws_an_ellipse_and_a_diagonal_where_the_printer_would()
    {
        var doc = Doc(
            new EllipseElement { X = 20, Y = 20, WidthDots = 100, HeightDots = 50, ThicknessDots = 4 },
            new DiagonalLineElement { X = 200, Y = 20, WidthDots = 60, HeightDots = 60, ThicknessDots = 4, Lean = DiagonalLean.Right },
            new DiagonalLineElement { X = 300, Y = 20, WidthDots = 60, HeightDots = 60, ThicknessDots = 4, Lean = DiagonalLean.Left });
        using var result = LabelRenderer.RenderBitmap(ToRender(doc), new RenderOptions { Scale = 1 });
        var bmp = result.Bitmap;

        // Ellipse: the left and right extremes of the outline are black, the centre and the box corner are white.
        Assert.True(IsDark(bmp, 21, 45));
        Assert.True(IsDark(bmp, 118, 45));
        Assert.False(IsDark(bmp, 70, 45));
        Assert.False(IsDark(bmp, 21, 21));
        // A right-leaning diagonal runs from the bottom-left to the top-right corner.
        Assert.True(IsDark(bmp, 202, 78));
        Assert.True(IsDark(bmp, 256, 22));
        Assert.False(IsDark(bmp, 202, 22));
        // A left-leaning diagonal runs from the top-left to the bottom-right corner.
        Assert.True(IsDark(bmp, 302, 22));
        Assert.True(IsDark(bmp, 356, 78));
        Assert.False(IsDark(bmp, 302, 78));
    }

    [Fact]
    public void Preview_text_box_shows_only_the_lines_that_fit_its_height()
    {
        string words = string.Join(" ", Enumerable.Repeat("word", 40));
        RenderLabel BoxLabel(double height) => Label(500, 400, new RenderText { Text = words, FontHeightDots = 30, WidthDots = 200, HeightDots = height });
        using var tall = LabelRenderer.RenderBitmap(BoxLabel(300), new RenderOptions { Scale = 1 });
        using var short1 = LabelRenderer.RenderBitmap(BoxLabel(70), new RenderOptions { Scale = 1 });
        var tallBounds = DarkBounds(tall.Bitmap)!.Value;
        var shortBounds = DarkBounds(short1.Bitmap)!.Value;
        Assert.True(shortBounds.Bottom < tallBounds.Bottom, "fewer lines should be drawn in a shorter box");
        Assert.True(shortBounds.Bottom <= 72, "text must not run past the box height");
    }

    [Fact]
    public void Preview_draws_a_downloaded_graphic_at_its_magnification()
    {
        var r = ZplParser.Parse("~DGR:LOGO.GRF,8,1," + Checker + "\n^XA^PW100^LL100^FO10,10^XGR:LOGO.GRF,3,2^FS^XZ");
        var label = ToRender(r.Document);
        var image = Assert.IsType<RenderImage>(Assert.Single(label.Elements));
        Assert.Equal((24, 16), (image.WidthDots, image.HeightDots));
        using var result = LabelRenderer.RenderBitmap(label, new RenderOptions { Scale = 1 });
        Assert.True(IsDark(result.Bitmap, 11, 11));  // top-left of the outline
        Assert.False(IsDark(result.Bitmap, 10 + 12, 10 + 8)); // the box is empty inside
        // An unresolved recall stays a labelled placeholder.
        var unresolved = ToRender(Parse("^FO0,0^XGR:NONE.GRF,1,1^FS").Document);
        Assert.IsType<RenderPlaceholder>(Assert.Single(unresolved.Elements));
    }

    [Fact]
    public void Preview_label_reverse_inverts_what_is_underneath()
    {
        var r = Parse("^FO0,0^GB100,100,100^FS^LRY^FO20,20^GB30,30,30^FS");
        using var result = LabelRenderer.RenderBitmap(ToRender(r.Document), new RenderOptions { Scale = 1 });
        Assert.True(IsDark(result.Bitmap, 5, 5));
        Assert.False(IsDark(result.Bitmap, 30, 30)); // reversed box turns the black below it white
    }
}
