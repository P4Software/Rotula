using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Rendering.Barcodes;
using SkiaSharp;
using ZXing;

namespace Rotula.Tests.Showcase;

/// <summary>
/// A small drawing kit used to author the README showcase labels. It builds ordinary <see cref="LabelDocument"/> objects (exactly what the
/// designer would save), so the resulting .rotula files open in Rotula like any other label. Everything is placed in printer dots on a
/// grid, and text is positioned by its capital-letter top edge so that rows of different font sizes line up on a common line.
/// </summary>
internal sealed class LabelBuilder
{
    private int _next;

    /// <summary>The label being built.</summary>
    public LabelDocument Doc { get; }

    /// <summary>Printer resolution in dots per inch.</summary>
    public int Dpi { get; }

    /// <summary>Label width in dots.</summary>
    public int W => Doc.WidthDots;

    /// <summary>Label height in dots.</summary>
    public int H => Doc.HeightDots;

    /// <summary>Starts a label of the given size in inches.</summary>
    /// <param name="widthInches">Label width.</param>
    /// <param name="heightInches">Label height.</param>
    /// <param name="dpi">203, 300 or 600.</param>
    /// <param name="stockSeed">Seed for the deterministic document id, so re-running the generator does not churn the files.</param>
    public LabelBuilder(double widthInches, double heightInches, int dpi, int stockSeed)
    {
        Dpi = dpi;
        Doc = new LabelDocument
        {
            Width = Measure.Inches(widthInches),
            Height = Measure.Inches(heightInches),
            Dpi = (PrinterDpi)dpi,
            Id = stockSeed.ToString("x32"),
        };
    }

    /// <summary>Points to printer dots at this label's resolution.</summary>
    public int Pt(double points) => (int)Math.Round(FontChoice.PointsToDots(points, Dpi));

    /// <summary>Millimetres to printer dots at this label's resolution.</summary>
    public int Mm(double mm) => (int)Math.Round(mm / 25.4 * Dpi);

    private string NewId() => (++_next).ToString("x32");

    private T Add<T>(T element, string? name = null, PrintCondition? condition = null) where T : LabelElement
    {
        element.Id = NewId();
        element.Name = name;
        element.PrintCondition = condition;
        Doc.Elements.Add(element);
        return element;
    }

    /// <summary>Declares a merge field with the value shown in the designer.</summary>
    public void Field(string name, string sample) => Doc.Variables.Add(new LabelVariable(name, sample));

    // ------------------------------------------------------------------------------------------------ text

    /// <summary>Distance between the top of a line box and the top of its capital letters, for a Windows font at a size in dots.</summary>
    private static int CapOffset(string family, bool bold, bool italic, int em)
    {
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        using var face = SKTypeface.FromFamilyName(family, style);
        // A missing family would silently fall back to another face and change the whole look, so fail loudly.
        if (face is null || !string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The Windows font \"{family}\" is not installed (got \"{face?.FamilyName}\"). The showcase only uses fonts every Windows install has.");
        using var font = new SKFont(face, em);
        var m = font.Metrics;
        // Not every font reports a cap height; 0.7 em is the typical value and only affects the optical alignment by a dot or two.
        float cap = m.CapHeight > 0 ? m.CapHeight : em * 0.7f;
        return (int)Math.Round(-m.Ascent - cap);
    }

    /// <summary>Width in dots that a single line of text occupies in the given font; used for centering and right-aligning without a text block.</summary>
    public int TextWidth(string text, string family, double pt, bool bold = false, bool italic = false)
    {
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        using var face = SKTypeface.FromFamilyName(family, style);
        using var font = new SKFont(face, Pt(pt)) { Subpixel = true };
        return (int)Math.Ceiling(font.MeasureText(text));
    }

    /// <summary>The largest point size, down to 4 pt in half-point steps, at which the line fits in <paramref name="maxWidth"/> dots.</summary>
    public double FitPoints(string text, string family, double maxPoints, int maxWidth, bool bold = false, bool italic = false)
    {
        double pt = maxPoints;
        while (pt > 4 && TextWidth(text, family, pt, bold, italic) > maxWidth) pt -= 0.5;
        return pt;
    }

    /// <summary>
    /// Adds a text object whose capital letters start at <paramref name="capTop"/>. With a <paramref name="width"/> the text is a block that
    /// wraps, aligns and may use several lines; without it the text is a single line starting at <paramref name="x"/>.
    /// </summary>
    public TextElement Text(int x, int capTop, string text, string family, double pt, bool bold = false, bool italic = false,
        int width = 0, TextAlignment align = TextAlignment.Left, int maxLines = 1, int lineSpacing = 0, bool reverse = false,
        FieldRotation? rotation = null, string? name = null, PrintCondition? condition = null, bool underline = false)
    {
        int em = Pt(pt);
        // A rotated text has no cap-top reference on the page, so the caller positions its bounding box directly.
        int y = rotation is null or FieldRotation.Normal ? capTop - CapOffset(family, bold, italic, em) : capTop;
        var t = new TextElement
        {
            Text = text,
            X = x,
            Y = y,
            ReverseField = reverse,
            Rotation = rotation,
            Font = FontChoice.ForWindows(family, pt, bold, italic, underline),
        };
        if (width > 0)
            t.Block = new TextBlockOptions { WidthDots = width, MaxLines = maxLines, Alignment = align, LineSpacing = lineSpacing };
        return Add(t, name, condition);
    }

    /// <summary>Single line of text horizontally centred on <paramref name="centerX"/>.</summary>
    public TextElement TextCentered(int centerX, int capTop, string text, string family, double pt, bool bold = false, bool italic = false,
        bool reverse = false, string? name = null, PrintCondition? condition = null)
    {
        int w = TextWidth(text, family, pt, bold, italic);
        return Text(centerX - w / 2, capTop, text, family, pt, bold, italic, reverse: reverse, name: name, condition: condition);
    }

    /// <summary>Single line of text whose right edge sits on <paramref name="right"/>.</summary>
    public TextElement TextRight(int right, int capTop, string text, string family, double pt, bool bold = false, bool italic = false,
        bool reverse = false, string? name = null, PrintCondition? condition = null)
    {
        int w = TextWidth(text, family, pt, bold, italic);
        return Text(right - w, capTop, text, family, pt, bold, italic, reverse: reverse, name: name, condition: condition);
    }

    // ------------------------------------------------------------------------------------------------ graphics

    /// <summary>An outlined box. <paramref name="round"/> is the ZPL corner degree, 0 (square) to 8 (fully round ends).</summary>
    public BoxElement Frame(int x, int y, int w, int h, int thickness = 4, int round = 0, bool reverse = false, bool white = false,
        string? name = null, PrintCondition? condition = null) =>
        Add(new BoxElement
        {
            X = x, Y = y, WidthDots = w, HeightDots = h, ThicknessDots = thickness, CornerRounding = round,
            ReverseField = reverse, Color = white ? GraphicColor.White : GraphicColor.Black,
        }, name, condition);

    /// <summary>A solid box (a box whose border is as thick as the box itself).</summary>
    public BoxElement Fill(int x, int y, int w, int h, int round = 0, bool white = false, string? name = null, PrintCondition? condition = null) =>
        Frame(x, y, w, h, Math.Max(w, h), round, white: white, name: name, condition: condition);

    /// <summary>A horizontal rule.</summary>
    public LineElement HRule(int x, int y, int length, int thickness = 3, bool white = false, string? name = null, PrintCondition? condition = null) =>
        Add(new LineElement
        {
            X = x, Y = y, LengthDots = length, ThicknessDots = thickness, Direction = LineDirection.Horizontal,
            Color = white ? GraphicColor.White : GraphicColor.Black,
        }, name, condition);

    /// <summary>A vertical rule.</summary>
    public LineElement VRule(int x, int y, int length, int thickness = 3, bool white = false, string? name = null, PrintCondition? condition = null) =>
        Add(new LineElement
        {
            X = x, Y = y, LengthDots = length, ThicknessDots = thickness, Direction = LineDirection.Vertical,
            Color = white ? GraphicColor.White : GraphicColor.Black,
        }, name, condition);

    /// <summary>A circle; a thickness of half the diameter or more gives a solid disc.</summary>
    public CircleElement Circle(int x, int y, int diameter, int thickness = 4, bool white = false, string? name = null, PrintCondition? condition = null) =>
        Add(new CircleElement
        {
            X = x, Y = y, DiameterDots = diameter, ThicknessDots = thickness, Color = white ? GraphicColor.White : GraphicColor.Black,
        }, name, condition);

    /// <summary>A solid disc.</summary>
    public CircleElement Disc(int x, int y, int diameter, bool white = false, string? name = null, PrintCondition? condition = null) =>
        Circle(x, y, diameter, diameter, white, name, condition);

    /// <summary>An ellipse outline.</summary>
    public EllipseElement Ellipse(int x, int y, int w, int h, int thickness = 4, bool white = false, string? name = null, PrintCondition? condition = null) =>
        Add(new EllipseElement
        {
            X = x, Y = y, WidthDots = w, HeightDots = h, ThicknessDots = thickness, Color = white ? GraphicColor.White : GraphicColor.Black,
        }, name, condition);

    /// <summary>A diagonal line inside the box (x, y, w, h).</summary>
    public DiagonalLineElement Diagonal(int x, int y, int w, int h, int thickness = 4, bool leanRight = true, bool white = false,
        string? name = null, PrintCondition? condition = null) =>
        Add(new DiagonalLineElement
        {
            X = x, Y = y, WidthDots = w, HeightDots = h, ThicknessDots = thickness,
            Lean = leanRight ? DiagonalLean.Right : DiagonalLean.Left, Color = white ? GraphicColor.White : GraphicColor.Black,
        }, name, condition);

    // ------------------------------------------------------------------------------------------------ barcodes

    /// <summary>Adds a barcode element configured by <paramref name="configure"/> (symbology and data are required).</summary>
    public BarcodeElement Barcode(int x, int y, BarcodeSymbology symbology, string data, Action<BarcodeElement>? configure = null,
        FieldRotation? rotation = null, string? name = null, PrintCondition? condition = null)
    {
        var b = new BarcodeElement { X = x, Y = y, Symbology = symbology, Data = data, Rotation = rotation };
        configure?.Invoke(b);
        return Add(b, name, condition);
    }

    /// <summary>
    /// Adds a barcode with the widest whole-dot module that keeps it within <paramref name="maxWidth"/> dots, so a linear symbol can span a
    /// column exactly. <paramref name="capModule"/> stops small data from producing absurdly fat bars.
    /// </summary>
    public BarcodeElement BarcodeFit(int x, int y, BarcodeSymbology symbology, string data, int maxWidth, Action<BarcodeElement>? configure = null,
        int capModule = 8, FieldRotation? rotation = null, string? name = null, PrintCondition? condition = null, bool center = false)
    {
        var probe = new BarcodeElement { Symbology = symbology, Data = data, ModuleWidth = 1, ModuleSize = 1 };
        configure?.Invoke(probe);
        probe.ModuleWidth = 1;
        probe.ModuleSize = 1;
        int modules = BarsSize(probe).Width;
        int module = Math.Clamp(maxWidth / Math.Max(1, modules), 1, capModule);
        var barcode = Barcode(x, y, symbology, data, e => { configure?.Invoke(e); e.ModuleWidth = module; e.ModuleSize = module; }, rotation, name, condition);
        // Whole-dot modules rarely fill the column exactly; centring the leftover keeps the symbol balanced under its heading.
        if (center) barcode.X = x + (maxWidth - BarsSize(barcode).Width) / 2;
        return barcode;
    }

    /// <summary>
    /// Splits <paramref name="total"/> dots into columns separated by <paramref name="gutter"/>: each column gets its minimum and the leftover
    /// is shared equally, so a row of cells fills its width exactly and no cell is narrower than its text.
    /// </summary>
    public static int[] Distribute(int total, int gutter, params int[] minimums)
    {
        int spare = total - gutter * (minimums.Length - 1) - minimums.Sum();
        if (spare < 0) throw new InvalidOperationException($"The cells need {-spare} more dots than the row has.");
        var widths = new int[minimums.Length];
        for (int i = 0; i < widths.Length; i++) widths[i] = minimums[i] + spare / widths.Length;
        widths[^1] += spare - spare / widths.Length * widths.Length;
        return widths;
    }

    /// <summary>
    /// Describes how a decoder must read <paramref name="barcode"/> back from the rendered picture: the symbology, the text (the element's data by
    /// default) and the area it occupies, with room for its human-readable line and a quiet zone.
    /// </summary>
    public static ExpectedScan Expect(BarcodeElement barcode, BarcodeFormat format, string? text = null)
    {
        // A merge field in the element's data cannot be encoded, so the size is measured with the record's real text.
        string saved = barcode.Data;
        barcode.Data = text ?? saved;
        (int w, int h) = BarsSize(barcode);
        barcode.Data = saved;
        bool linear = Rotula.Core.Barcodes.BarcodeCatalog.Get(barcode.Symbology).Category == Rotula.Core.Barcodes.BarcodeCategory.Linear;
        int hri = linear && barcode.HumanReadable ? 70 : 0;
        bool turned = barcode.Rotation is FieldRotation.Rotated90 or FieldRotation.Rotated270;
        int rw = turned ? h + hri : w, rh = turned ? w : h + hri;
        const int quiet = 24;
        return new ExpectedScan(format, text ?? barcode.Data, new ScanRegion(barcode.X - quiet, barcode.Y - quiet, rw + 2 * quiet, rh + 2 * quiet));
    }

    /// <summary>
    /// The size in dots of the bars of a barcode (without the human-readable line), so layouts can centre or right-align a symbol exactly.
    /// For linear codes the height is the one asked for; for 2D codes it comes from the module grid.
    /// </summary>
    public static (int Width, int Height) BarsSize(BarcodeElement b)
    {
        var info = Rotula.Core.Barcodes.BarcodeCatalog.Get(b.Symbology);
        var options = new BarcodeEncodeOptions
        {
            QrErrorCorrection = b.QrErrorCorrection,
            Pdf417SecurityLevel = b.Pdf417SecurityLevel,
            CheckDigit = b.CheckDigit,
            MaxiCodeMode = b.MaxiCodeMode,
            Gs1 = Rotula.Core.Barcodes.BarcodeValidator.IsGs128Data(b, b.Data) || Rotula.Core.Barcodes.BarcodeValidator.IsGs1DataMatrix(b),
        };
        var matrix = BarcodeEncoder.Encode(b.Symbology, b.Data, options);
        int module = info.UsesModuleSize ? b.ModuleSize : b.ModuleWidth;
        if (b.Symbology == BarcodeSymbology.MaxiCode) module = 7;
        return (matrix.Width * module, matrix.IsLinear ? b.Height : matrix.Layout == BarcodeRowLayout.Pdf417Rows ? matrix.Height * Math.Max(1, b.Height / 4) : matrix.Height * module);
    }
}

/// <summary>Condition helpers so label definitions read naturally.</summary>
internal static class Rule
{
    /// <summary>Print the object only when <paramref name="field"/> has a value.</summary>
    public static PrintCondition WhenSet(string field) => new() { Field = field, Operator = ConditionOperator.IsNotEmpty };

    /// <summary>Print the object only when <paramref name="field"/> equals <paramref name="value"/>.</summary>
    public static PrintCondition WhenEquals(string field, string value) => new() { Field = field, Operator = ConditionOperator.Equals, Value = value };

    /// <summary>Print the object only when <paramref name="field"/> is empty.</summary>
    public static PrintCondition WhenEmpty(string field) => new() { Field = field, Operator = ConditionOperator.IsEmpty };
}
