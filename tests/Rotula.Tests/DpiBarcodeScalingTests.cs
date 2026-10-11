using System.Collections;
using System.Reflection;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using ZXing;

namespace Rotula.Tests;

/// <summary>
/// DPI conversion of every barcode symbology (module widths, heights and the extra options), of the newer elements and
/// settings, and a reflection guard that fails when a dot-based property is neither scaled nor explicitly listed as not dot-based.
/// </summary>
public class DpiBarcodeScalingTests
{
    private static LabelDocument Doc(PrinterDpi dpi, params LabelElement[] elements)
    {
        var d = new LabelDocument { Width = Measure.Millimeters(100), Height = Measure.Millimeters(60), Dpi = dpi, DefaultFont = new FontSpec('0', 30, 20) };
        d.Elements.AddRange(elements);
        return d;
    }

    /// <summary>The catalogue example with modules that round cleanly at 300 and 600 dpi (no rounding warning expected).</summary>
    private static BarcodeElement Sample(BarcodeSymbology s, int moduleSize = 3)
    {
        var b = SymbSupport.Default(s);
        b.X = 60; b.Y = 60;
        b.ModuleWidth = 2; b.ModuleSize = moduleSize; b.Tlc39MicroModuleWidth = 2; b.Tlc39MicroRowHeight = 8;
        b.Height = s is BarcodeSymbology.Pdf417 or BarcodeSymbology.MicroPdf417 or BarcodeSymbology.Codablock ? 12 : 80;
        b.HumanReadable = false;
        if (s == BarcodeSymbology.Interleaved2Of5) b.CheckDigit = false;
        return b;
    }

    private static int Expected(int value, double ratio, int min, int max) =>
        Math.Clamp((int)Math.Round(value * ratio, MidpointRounding.AwayFromZero), min, max);

    public static TheoryData<BarcodeSymbology, PrinterDpi> SymbologyTargets()
    {
        var data = new TheoryData<BarcodeSymbology, PrinterDpi>();
        foreach (var s in Enum.GetValues<BarcodeSymbology>())
            foreach (var to in new[] { PrinterDpi.Dpi300, PrinterDpi.Dpi600 }) data.Add(s, to);
        return data;
    }

    [Theory]
    [MemberData(nameof(SymbologyTargets))]
    public void Every_symbology_scales_the_dot_options_the_catalogue_names(BarcodeSymbology s, PrinterDpi to)
    {
        var src = Sample(s);
        var r = LabelDpiConverter.Convert(Doc(PrinterDpi.Dpi203, src), to);
        var n = Assert.IsType<BarcodeElement>(Assert.Single(r.Document.Elements));
        double ratio = (double)(int)to / 203;

        Assert.Equal(Expected(src.ModuleWidth, ratio, 1, 10), n.ModuleWidth);
        Assert.Equal(Expected(src.ModuleSize, ratio, 1, 10), n.ModuleSize);
        Assert.Equal(Expected(src.Tlc39MicroModuleWidth, ratio, 1, 10), n.Tlc39MicroModuleWidth);
        Assert.Equal(Expected(src.Height, ratio, 1, 32000), n.Height);
        Assert.Equal(Expected(src.Tlc39MicroRowHeight, ratio, 1, 255), n.Tlc39MicroRowHeight);
        Assert.Equal(Expected(src.X, ratio, int.MinValue, int.MaxValue), n.X);
        // Options that are not measured in dots (modes, counts, flags, the DataBar separator in modules) must not move.
        Assert.Equal(src.DataBarSeparatorHeight, n.DataBarSeparatorHeight);
        Assert.Equal(src.DataBarSegmentsPerRow, n.DataBarSegmentsPerRow);
        Assert.Equal(src.Columns, n.Columns);
        Assert.Equal(src.Rows, n.Rows);
        Assert.Equal(src.MicroPdf417Mode, n.MicroPdf417Mode);
        Assert.Equal(src.Data, n.Data);
        Assert.Empty(r.Issues);
        // Informational warnings (for example LOGMARS always printing its text) are fine; the scaled values must not be errors.
        Assert.DoesNotContain(BarcodeValidator.Validate(n, n.Data), i => i.Severity == ValidationSeverity.Error);
    }

    [Theory]
    [MemberData(nameof(SymbologyTargets))]
    public void Every_symbology_comes_back_unchanged_after_203_to_target_and_back(BarcodeSymbology s, PrinterDpi via)
    {
        var src = Sample(s);
        var there = LabelDpiConverter.Convert(Doc(PrinterDpi.Dpi203, src), via).Document;
        var back = Assert.IsType<BarcodeElement>(Assert.Single(LabelDpiConverter.Convert(there, PrinterDpi.Dpi203).Document.Elements));
        Assert.Equal(src.ModuleWidth, back.ModuleWidth);
        Assert.Equal(src.ModuleSize, back.ModuleSize);
        Assert.Equal(src.Tlc39MicroModuleWidth, back.Tlc39MicroModuleWidth);
        Assert.InRange(back.Height, src.Height - 1, src.Height + 1);
        Assert.InRange(back.Tlc39MicroRowHeight, src.Tlc39MicroRowHeight - 1, src.Tlc39MicroRowHeight + 1);
        Assert.InRange(back.X, src.X - 1, src.X + 1);
    }

    [Fact]
    public void Dot_options_the_catalogue_reports_are_the_ones_the_converter_scales()
    {
        // The converter handles these five option properties; the catalogue must name exactly these as dot measures.
        var dotOptions = Enum.GetValues<BarcodeOption>().Where(o => BarcodeCatalog.GetDotKind(o) != BarcodeDotKind.NotDots).ToHashSet();
        Assert.Equal(new[]
        {
            BarcodeOption.Height, BarcodeOption.ModuleWidth, BarcodeOption.ModuleSize,
            BarcodeOption.Tlc39MicroModuleWidth, BarcodeOption.Tlc39MicroRowHeight,
        }.ToHashSet(), dotOptions);
        Assert.Equal(BarcodeDotKind.NotDots, BarcodeCatalog.GetDotKind(BarcodeOption.DataBarSeparatorHeight));
        foreach (var info in BarcodeCatalog.All)
            Assert.All(info.DotOptions, t => Assert.Contains(t.Option, info.Options));
    }

    [Fact]
    public void Module_rounding_warning_is_raised_only_for_options_the_symbology_uses()
    {
        // 1 dot at 203 dpi is 1.48 ideal at 300 dpi: rounds to 1 (32 % off) -> warning, for every kind of module option.
        var aztec = new BarcodeElement { Symbology = BarcodeSymbology.Aztec, Data = "x", ModuleSize = 1, Name = "az" };
        var issue = Assert.Single(LabelDpiConverter.Convert(Doc(PrinterDpi.Dpi203, aztec), PrinterDpi.Dpi300).Issues);
        Assert.Equal("Dpi.Barcode.ModuleRounded", issue.MessageKey);
        Assert.Equal(new[] { "az", "1", "1" }, issue.Args);

        var tlc = new BarcodeElement { Symbology = BarcodeSymbology.Tlc39, Data = "123456", ModuleWidth = 2, Tlc39MicroModuleWidth = 1, Name = "t" };
        var tIssue = Assert.Single(LabelDpiConverter.Convert(Doc(PrinterDpi.Dpi203, tlc), PrinterDpi.Dpi300).Issues);
        Assert.Equal(new[] { "t", "1", "1" }, tIssue.Args);

        // MaxiCode has no dot option, so a leftover ModuleWidth of 1 must not warn.
        var maxi = new BarcodeElement { Symbology = BarcodeSymbology.MaxiCode, Data = "x", ModuleWidth = 1 };
        Assert.Empty(LabelDpiConverter.Convert(Doc(PrinterDpi.Dpi203, maxi), PrinterDpi.Dpi300).Issues);
    }

    [Fact]
    public void Data_bar_uses_the_module_size_not_the_bar_width()
    {
        var bar = new BarcodeElement { Symbology = BarcodeSymbology.DataBar, Data = "1234567890123", ModuleSize = 3, ModuleWidth = 2, DataBarSeparatorHeight = 2 };
        var n = Assert.IsType<BarcodeElement>(Assert.Single(LabelDpiConverter.Convert(Doc(PrinterDpi.Dpi203, bar), PrinterDpi.Dpi300).Document.Elements));
        Assert.Equal(4, n.ModuleSize);
        Assert.Equal(2, n.DataBarSeparatorHeight);
    }

    // ---- physical size and scanning ---------------------------------------------------------------

    private static (int Width, int Height)? PrintedSize(LabelDocument d)
    {
        using var result = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(d));
        var b = RenderingTestSupport.DarkBounds(result.Bitmap);
        return b is null ? null : (b.Value.Width, b.Value.Height);
    }

    public static TheoryData<BarcodeSymbology, PrinterDpi> DrawnSymbologies()
    {
        var data = new TheoryData<BarcodeSymbology, PrinterDpi>();
        foreach (var info in BarcodeCatalog.All.Where(i => i.HasPreviewEncoder))
            foreach (var to in new[] { PrinterDpi.Dpi300, PrinterDpi.Dpi600 }) data.Add(info.Symbology, to);
        return data;
    }

    [Theory]
    [MemberData(nameof(DrawnSymbologies))]
    public void Printed_symbol_keeps_its_physical_size_within_the_module_rounding(BarcodeSymbology s, PrinterDpi to)
    {
        var src = Doc(PrinterDpi.Dpi203, Sample(s, 2));
        var conv = LabelDpiConverter.Convert(src, to).Document;
        var before = PrintedSize(src);
        var after = PrintedSize(conv);
        Assert.NotNull(before);
        Assert.NotNull(after);
        double inch203 = 1.0 / 203, inchTo = 1.0 / (int)to;
        // The module is rounded to whole dots (at most ~3 % here) and each edge may move by one dot: allow both, plus one module.
        double moduleInches = 3 * inch203;
        void Near(int a, int b, string what)
        {
            double physA = a * inch203, physB = b * inchTo;
            double allowed = physA * 0.04 + 2 * inchTo + moduleInches;
            Assert.True(Math.Abs(physA - physB) <= allowed, $"{s} {what}: {physA:0.000} in vs {physB:0.000} in (allowed {allowed:0.000})");
        }
        Near(before!.Value.Width, after!.Value.Width, "width");
        Near(before.Value.Height, after.Value.Height, "height");
    }

    public static TheoryData<BarcodeSymbology, BarcodeFormat, string, string, DataBarType> Scannable() => new()
    {
        { BarcodeSymbology.Code128, BarcodeFormat.CODE_128, "ROTULA-12345", "ROTULA-12345", DataBarType.Omnidirectional },
        { BarcodeSymbology.Code39, BarcodeFormat.CODE_39, "ABC-123", "ABC-123", DataBarType.Omnidirectional },
        { BarcodeSymbology.Code93, BarcodeFormat.CODE_93, "TEST93", "TEST93", DataBarType.Omnidirectional },
        { BarcodeSymbology.Ean13, BarcodeFormat.EAN_13, "590123412345", "5901234123457", DataBarType.Omnidirectional },
        { BarcodeSymbology.Ean8, BarcodeFormat.EAN_8, "9638507", "96385074", DataBarType.Omnidirectional },
        { BarcodeSymbology.Interleaved2Of5, BarcodeFormat.ITF, "1234567890", "1234567890", DataBarType.Omnidirectional },
        { BarcodeSymbology.Codabar, BarcodeFormat.CODABAR, "123456", "123456", DataBarType.Omnidirectional },
        { BarcodeSymbology.Msi, BarcodeFormat.MSI, "1234567", "12345674", DataBarType.Omnidirectional },
        { BarcodeSymbology.QrCode, BarcodeFormat.QR_CODE, "https://example.com/r?id=42", "https://example.com/r?id=42", DataBarType.Omnidirectional },
        { BarcodeSymbology.DataMatrix, BarcodeFormat.DATA_MATRIX, "ROTULA DM", "ROTULA DM", DataBarType.Omnidirectional },
        { BarcodeSymbology.Pdf417, BarcodeFormat.PDF_417, "PDF417 payload 0123", "PDF417 payload 0123", DataBarType.Omnidirectional },
        { BarcodeSymbology.Aztec, BarcodeFormat.AZTEC, "Rotula Aztec 123", "Rotula Aztec 123", DataBarType.Omnidirectional },
        { BarcodeSymbology.DataBar, BarcodeFormat.RSS_14, "1234567890123", "", DataBarType.Omnidirectional },
        { BarcodeSymbology.DataBar, BarcodeFormat.RSS_EXPANDED, "(01)90614141999999(10)ABC123", "", DataBarType.Expanded },
    };

    [Theory]
    [MemberData(nameof(Scannable))]
    public void Converted_symbol_scans_at_300_and_600_dpi(BarcodeSymbology s, BarcodeFormat f, string data, string expected, DataBarType type)
    {
        var b = Sample(s, 2);
        b.Data = data; b.DataBarType = type;
        if (s == BarcodeSymbology.Pdf417) b.Height = 8;
        if (s == BarcodeSymbology.DataBar) b.Height = 60;
        var src = Doc(PrinterDpi.Dpi203, b);
        var at300 = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300).Document;

        string? reference = null;
        foreach (var d in new[] { src, at300, LabelDpiConverter.Convert(src, PrinterDpi.Dpi600).Document, LabelDpiConverter.Convert(at300, PrinterDpi.Dpi203).Document })
        {
            using var result = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(d));
            var decoded = RenderingTestSupport.Decode(result.Bitmap, f);
            Assert.True(decoded is not null, $"{s} {f} at {d.Dpi} did not decode");
            // DataBar text carries the application identifier; compare against the 203 dpi decode instead of a literal.
            reference ??= expected.Length > 0 ? expected : decoded!.Text;
            Assert.Equal(reference, decoded!.Text);
        }
    }

    // ---- newer elements and settings ---------------------------------------------------------------

    [Fact]
    public void Diagonal_ellipse_symbol_text_box_and_label_offsets_are_scaled()
    {
        var src = Doc(PrinterDpi.Dpi203,
            new DiagonalLineElement { X = 10, Y = 20, WidthDots = 100, HeightDots = 60, ThicknessDots = 4, Lean = DiagonalLean.Left },
            new EllipseElement { X = 10, Y = 20, WidthDots = 100, HeightDots = 60, ThicknessDots = 4 },
            new SymbolElement { X = 10, Y = 20, HeightDots = 40, WidthDots = 30, Symbol = GraphicSymbol.Registered },
            new TextElement { Text = "t", X = 10, Y = 20, FontHeight = 30, TextBox = new TextBoxOptions { WidthDots = 400, HeightDots = 100 } });
        src.LabelShift = -20; src.LabelTop = 30;
        var d = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300).Document;
        Assert.Equal((-30, 44), (d.LabelShift, d.LabelTop));
        var dl = Assert.IsType<DiagonalLineElement>(d.Elements[0]);
        Assert.Equal((15, 30, 148, 89, 6, DiagonalLean.Left), (dl.X, dl.Y, dl.WidthDots, dl.HeightDots, dl.ThicknessDots, dl.Lean));
        var el = Assert.IsType<EllipseElement>(d.Elements[1]);
        Assert.Equal((148, 89, 6), (el.WidthDots, el.HeightDots, el.ThicknessDots));
        var sy = Assert.IsType<SymbolElement>(d.Elements[2]);
        Assert.Equal((59, 44, GraphicSymbol.Registered), (sy.HeightDots, sy.WidthDots, sy.Symbol));
        var tb = Assert.IsType<TextElement>(d.Elements[3]).TextBox!;
        Assert.Equal((591, 148), (tb.WidthDots, tb.HeightDots));
    }

    [Fact]
    public void Stored_graphics_are_kept_and_reported_once_each()
    {
        var src = Doc(PrinterDpi.Dpi203);
        src.StoredGraphics.Add(new StoredGraphic("R:LOGO.GRF", 2, new byte[16]));
        src.StoredGraphics.Add(new StoredGraphic("R:SEAL.GRF", 1, new byte[8]));
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300);
        Assert.Equal(2, r.Document.StoredGraphics.Count);
        Assert.Equal(src.StoredGraphics[0].Data, r.Document.StoredGraphics[0].Data);
        Assert.Equal(2, r.Issues.Count);
        Assert.All(r.Issues, i =>
        {
            Assert.Equal("Dpi.Graphic.NotScaled", i.MessageKey);
            Assert.Equal(ValidationSeverity.Warning, i.Severity);
            Assert.Null(i.ElementId);
        });
        Assert.Equal(new[] { "R:LOGO.GRF" }, r.Issues[0].Args);
        Assert.Equal(new[] { "R:SEAL.GRF" }, r.Issues[1].Args);
        Assert.Empty(LabelDpiConverter.Convert(src, PrinterDpi.Dpi203).Issues);
    }

    [Fact]
    public void Raw_zpl_still_warns_and_keeps_its_coordinates()
    {
        var raw = new RawZplElement { Zpl = "^FO10,10^GB5,5,1^FS", X = 10, Y = 10, IsVisual = true, Name = "raw" };
        var r = LabelDpiConverter.Convert(Doc(PrinterDpi.Dpi203, raw), PrinterDpi.Dpi600);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("Dpi.Raw.NotScaled", issue.MessageKey);
        Assert.Equal(new[] { "raw" }, issue.Args);
        var n = Assert.IsType<RawZplElement>(Assert.Single(r.Document.Elements));
        Assert.Equal((10, 10, raw.Zpl), (n.X, n.Y, n.Zpl));
    }

    // ---- guard: every dot-based property is scaled or explicitly not dot-based ---------------------------

    /// <summary>
    /// Properties that hold numbers but are not measured in printer dots (or that the converter deliberately keeps),
    /// with the reason. Anything numeric that is missing here MUST change when the resolution changes.
    /// </summary>
    private static readonly Dictionary<string, string> NotDotBased = new()
    {
        // barcode: counts, modes, levels, ratios and the DataBar separator (measured in modules)
        ["BarcodeElement.WideBarRatio"] = "ratio",
        ["BarcodeElement.QrModel"] = "model number",
        ["BarcodeElement.QrMask"] = "mask pattern 0-7 or -1",
        ["BarcodeElement.Columns"] = "count",
        ["BarcodeElement.Rows"] = "count",
        ["BarcodeElement.Pdf417SecurityLevel"] = "level",
        ["BarcodeElement.Code11CheckDigitCount"] = "count",
        ["BarcodeElement.DataBarSeparatorHeight"] = "modules, not dots",
        ["BarcodeElement.DataBarSegmentsPerRow"] = "count",
        ["BarcodeElement.AztecErrorControl"] = "percent / layer code",
        ["BarcodeElement.AztecStructuredAppendCount"] = "count",
        ["BarcodeElement.MicroPdf417Mode"] = "mode",
        ["BarcodeElement.MaxiCodeMode"] = "mode",
        ["BarcodeElement.MaxiCodeSymbolNumber"] = "count",
        ["BarcodeElement.MaxiCodeSymbolTotal"] = "count",
        // text
        ["TextBlockOptions.MaxLines"] = "count",
        // shapes / images
        ["BoxElement.CornerRounding"] = "ZPL rounding degree 0-8",
        ["ImageElement.Threshold"] = "luminance 0-255",
        // raw ZPL and recalls cannot be rewritten; they keep their dots and raise Dpi.Raw.NotScaled
        ["RawZplElement.X"] = "verbatim, warned",
        ["RawZplElement.Y"] = "verbatim, warned",
        ["RecallPlaceholderElement.X"] = "verbatim, warned",
        ["RecallPlaceholderElement.Y"] = "verbatim, warned",
        ["RecallPlaceholderElement.WidthDots"] = "placeholder of an unknown stored object, warned",
        ["RecallPlaceholderElement.HeightDots"] = "placeholder of an unknown stored object, warned",
        // document
        ["ZplEncoding.CiCode"] = "^CI code page number",
        ["PrintRateOptions.Speed"] = "inches per second",
        ["PrintRateOptions.SlewSpeed"] = "inches per second",
        ["PrintRateOptions.BackfeedSpeed"] = "inches per second",
        ["LabelDocument.MediaDarkness"] = "darkness setting",
        ["TextElement.FieldNumber"] = "field number",
        ["DataFieldElement.FieldNumber"] = "field number",
        ["BarcodeElement.FieldNumber"] = "field number",
        ["PrintOptions.Quantity"] = "count",
        ["PrintOptions.PauseEvery"] = "count",
        ["PrintOptions.Replicates"] = "count",
    };

    private static bool IsNumeric(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u == typeof(int) || u == typeof(double) || u == typeof(decimal) || u == typeof(Measure);
    }

    private static decimal ToNumber(object v) =>
        v is Measure m ? (decimal)m.Value : System.Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsNestedOptions(Type t) =>
        t.IsClass && t != typeof(string) && t.Assembly == typeof(LabelDocument).Assembly && !typeof(LabelElement).IsAssignableFrom(t)
        && !typeof(IEnumerable).IsAssignableFrom(t) && t.Namespace != "Rotula.Core.Imaging";

    /// <summary>Gives every settable numeric property a distinctive value and instantiates nested option objects.</summary>
    private static void Populate(object o)
    {
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            var t = p.PropertyType; var u = Nullable.GetUnderlyingType(t) ?? t;
            if (p.CanWrite && IsNumeric(t))
            {
                if (u == typeof(int)) p.SetValue(o, 40);
                else if (u == typeof(double)) p.SetValue(o, 40.0);
                else if (u == typeof(decimal)) p.SetValue(o, 40m);
                else p.SetValue(o, Measure.Dots(40));
            }
            else if (IsNestedOptions(u))
            {
                var value = p.GetValue(o);
                if (value is null && p.CanWrite && u.GetConstructor(Type.EmptyTypes) is not null) { value = Activator.CreateInstance(u); p.SetValue(o, value); }
                if (value is not null) Populate(value);
            }
        }
    }

    private static void Collect(object o, Dictionary<string, decimal> into)
    {
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            var t = p.PropertyType; var u = Nullable.GetUnderlyingType(t) ?? t;
            if (IsNumeric(t))
            {
                if (p.GetValue(o) is { } v) into[$"{o.GetType().Name}.{p.Name}"] = ToNumber(v);
            }
            else if (IsNestedOptions(u) && p.GetValue(o) is { } nested) Collect(nested, into);
        }
    }

    [Fact]
    public void Guard_every_dot_based_property_is_scaled_or_listed_as_not_dot_based()
    {
        var doc = new LabelDocument { Dpi = PrinterDpi.Dpi203 };
        Populate(doc);
        var elementTypes = typeof(LabelElement).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(LabelElement).IsAssignableFrom(t)).ToList();
        Assert.Contains(typeof(DiagonalLineElement), elementTypes);
        foreach (var t in elementTypes)
        {
            var e = (LabelElement)Activator.CreateInstance(t)!;
            Populate(e);
            doc.Elements.Add(e);
        }

        var before = new Dictionary<string, decimal>();
        Collect(doc, before);
        foreach (var e in doc.Elements) Collect(e, before);

        var converted = LabelDpiConverter.Convert(doc, PrinterDpi.Dpi600).Document;
        var after = new Dictionary<string, decimal>();
        Collect(converted, after);
        foreach (var e in converted.Elements) Collect(e, after);

        var problems = new List<string>();
        foreach (var (key, was) in before)
        {
            bool scaled = after.TryGetValue(key, out var now) && now != was;
            bool allowed = NotDotBased.ContainsKey(key);
            if (!scaled && !allowed) problems.Add($"{key} is not scaled by LabelDpiConverter and is not on the not-dot-based list");
            if (scaled && allowed) problems.Add($"{key} is on the not-dot-based list but the converter scales it");
        }
        foreach (var key in NotDotBased.Keys)
            if (!before.ContainsKey(key)) problems.Add($"{key} is on the not-dot-based list but no such property exists (stale entry)");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
