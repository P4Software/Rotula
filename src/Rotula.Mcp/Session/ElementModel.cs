using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Mcp.Session;

/// <summary>An element as shown to the assistant. Lengths are in the label's unit; the *Dots fields repeat the position in dots.</summary>
/// <param name="Id">The id to use in update_element, move_element and the other element tools.</param>
/// <param name="Type">text, dataField, barcode, box, line, circle, image, rawZpl or recall.</param>
/// <param name="Name">Optional friendly name.</param>
/// <param name="X">Horizontal position from the label's left edge (inside the margin), in the label's unit.</param>
/// <param name="Y">Vertical position from the top edge (inside the margin), in the label's unit.</param>
/// <param name="XDots">X in printer dots.</param>
/// <param name="YDots">Y in printer dots.</param>
/// <param name="Rotation">Rotation in degrees (0, 90, 180, 270), or "inherit" to follow the label default.</param>
/// <param name="Properties">The type-specific settings; the same names are accepted by update_element.</param>
public sealed record ElementInfo(
    string Id, string Type, string? Name, double X, double Y, int XDots, int YDots, string Rotation,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>One settable property of an element type.</summary>
internal sealed record PropDef(string Name, string Description, Func<LabelElement, Ctx, object?> Get, Action<LabelElement, JsonElement, Ctx> Set);

/// <summary>Conversion context for one call: the label and the unit lengths are written in.</summary>
internal sealed record Ctx(LabelDocument Doc, string Unit)
{
    public int Dots(double v) => Units.ToDots(v, Unit, Doc.Dpi);
    public double Out(int dots) => Units.FromDots(dots, Units.OfLabel(Doc), Doc.Dpi);
}

/// <summary>Describes elements and applies property changes by name, with validation and plain-language errors.</summary>
internal static class ElementModel
{
    private static readonly Dictionary<string, FieldRotation> RotationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = FieldRotation.Normal, ["normal"] = FieldRotation.Normal, ["n"] = FieldRotation.Normal,
        ["90"] = FieldRotation.Rotated90, ["r"] = FieldRotation.Rotated90,
        ["180"] = FieldRotation.Inverted180, ["i"] = FieldRotation.Inverted180, ["inverted"] = FieldRotation.Inverted180,
        ["270"] = FieldRotation.Rotated270, ["b"] = FieldRotation.Rotated270,
    };

    public static string TypeName(LabelElement e) => e switch
    {
        DataFieldElement => "dataField",
        TextElement => "text",
        BarcodeElement => "barcode",
        BoxElement => "box",
        LineElement => "line",
        CircleElement => "circle",
        ImageElement => "image",
        DiagonalLineElement => "diagonalLine",
        EllipseElement => "ellipse",
        SymbolElement => "symbol",
        RawZplElement => "rawZpl",
        RecallPlaceholderElement => "recall",
        _ => e.GetType().Name,
    };

    public static string IdPrefix(LabelElement e) => e switch
    {
        DataFieldElement => "d",
        TextElement => "t",
        BarcodeElement => "b",
        BoxElement => "x",
        LineElement => "l",
        CircleElement => "c",
        ImageElement => "i",
        DiagonalLineElement => "g",
        EllipseElement => "o",
        SymbolElement => "s",
        _ => "e",
    };

    public static string RotationText(FieldRotation r) => r switch
    {
        FieldRotation.Rotated90 => "90",
        FieldRotation.Inverted180 => "180",
        FieldRotation.Rotated270 => "270",
        _ => "0",
    };

    public static FieldRotation ParseRotation(string? text)
    {
        if (text is not null && RotationNames.TryGetValue(text.Trim(), out var r)) return r;
        throw new McpException($"Unknown rotation \"{text}\". Use 0, 90, 180 or 270.");
    }

    /// <summary>Parses an enum by name, ignoring case, spaces, hyphens and underscores, and lists the choices on failure.</summary>
    public static T ParseEnum<T>(string? text, string what) where T : struct, Enum
    {
        string key = Squash(text);
        foreach (var value in Enum.GetValues<T>())
            if (Squash(value.ToString()) == key) return value;
        throw new McpException($"Unknown {what} \"{text}\". Choose one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    private static string Squash(string? s) =>
        new((s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static ElementInfo Describe(LabelDocument doc, LabelElement e)
    {
        var ctx = new Ctx(doc, Units.OfLabel(doc));
        var props = new Dictionary<string, object?>();
        foreach (var def in Defs(e))
            if (def.Name is not ("x" or "y" or "name" or "rotation") && def.Get(e, ctx) is { } value)
                props[def.Name] = value; // empty (null) settings are left out to keep the reply short
        if (e.ReverseField) props["reverse"] = true;
        if (e.OriginKind == FieldOriginKind.Typeset) props["originKind"] = "Typeset";
        if (e is RawZplElement raw) props["zpl"] = raw.Zpl.Length > 400 ? raw.Zpl[..400] + "..." : raw.Zpl;
        if (e is RecallPlaceholderElement rc) props["zpl"] = rc.Zpl;
        return new ElementInfo(e.Id, TypeName(e), e.Name, ctx.Out(e.X), ctx.Out(e.Y), e.X, e.Y,
            e.Rotation is { } rot ? RotationText(rot) : "inherit", props);
    }

    /// <summary>The names update_element accepts for this element.</summary>
    public static IEnumerable<string> PropertyNames(LabelElement e) => Defs(e).Select(d => d.Name).Concat(new[] { "reverse", "originKind" });

    /// <summary>Applies a property bag to an element; unknown names fail with the list of valid names.</summary>
    public static void Apply(LabelDocument doc, LabelElement e, IReadOnlyDictionary<string, JsonElement> values, string unit)
    {
        var ctx = new Ctx(doc, unit);
        var defs = Defs(e).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        // blockWidth must come before the other text settings and symbology before options, because both are read relative to the first.
        foreach (var (name, value) in values.OrderBy(kv => kv.Key.Equals("blockWidth", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("symbology", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (string.Equals(name, "reverse", StringComparison.OrdinalIgnoreCase)) { e.ReverseField = ReadBool(value, name); continue; }
            if (string.Equals(name, "originKind", StringComparison.OrdinalIgnoreCase)) { e.OriginKind = ParseEnum<FieldOriginKind>(ReadString(value, name), "origin kind"); continue; }
            if (!defs.TryGetValue(name, out var def))
                throw new McpException($"A {TypeName(e)} element has no property \"{name}\". Valid properties: {string.Join(", ", PropertyNames(e))}.");
            def.Set(e, value, ctx);
        }
    }

    // ---- property tables -------------------------------------------------------------------

    private static IEnumerable<PropDef> Defs(LabelElement e)
    {
        foreach (var d in Common()) yield return d;
        switch (e)
        {
            case TextElement:
                foreach (var d in Text()) yield return d;
                break;
            case BarcodeElement:
                foreach (var d in Barcode()) yield return d;
                break;
            case BoxElement:
                foreach (var d in Box()) yield return d;
                break;
            case LineElement:
                foreach (var d in Line()) yield return d;
                break;
            case CircleElement:
                foreach (var d in Circle()) yield return d;
                break;
            case ImageElement:
                foreach (var d in Image()) yield return d;
                break;
            case DiagonalLineElement:
                foreach (var d in Diagonal()) yield return d;
                break;
            case EllipseElement:
                foreach (var d in Ellipse()) yield return d;
                break;
            case SymbolElement:
                foreach (var d in Symbol()) yield return d;
                break;
        }
    }

    private static PropDef[] Common() =>
    [
        Len<LabelElement>("x", "Horizontal position.", v => v.X, (v, n) => v.X = n, -32000, 32000),
        Len<LabelElement>("y", "Vertical position.", v => v.Y, (v, n) => v.Y = n, -32000, 32000),
        Str<LabelElement>("name", "Friendly name.", v => v.Name, (v, s) => v.Name = string.IsNullOrWhiteSpace(s) ? null : Clip(s, 100)),
        new PropDef("rotation", "0, 90, 180 or 270 degrees, or \"inherit\".",
            (v, _) => v.Rotation is { } r ? RotationText(r) : "inherit",
            (v, j, _) =>
            {
                string s = ReadString(j, "rotation");
                v.Rotation = s.Equals("inherit", StringComparison.OrdinalIgnoreCase) ? null : ParseRotation(s);
            }),
    ];

    private static PropDef[] Text() =>
    [
        Str<TextElement>("text", "The text; {{name}} inserts a variable.", v => v.Text, (v, s) => { if (v is not DataFieldElement) v.Text = Clip(s, 4000); else throw new McpException("A dataField takes its content from its variable; change variableName instead."); }),
        Str<TextElement>("variableName", "The bound variable (dataField only).", v => (v as DataFieldElement)?.VariableName, (v, s) =>
        {
            if (v is not DataFieldElement f) throw new McpException("Only a dataField has a variableName.");
            RequireVariableName(s);
            f.VariableName = s;
        }),
        new PropDef("fontId", "Font id: 0 (scalable) or A-Z / 1-9 (printer bitmap fonts).",
            (v, _) => ((TextElement)v).FontId?.ToString(),
            (v, j, _) =>
            {
                string s = ReadString(j, "fontId");
                ((TextElement)v).FontId = s.Length == 1 && char.IsAsciiLetterOrDigit(s[0]) ? char.ToUpperInvariant(s[0]) : throw new McpException("fontId must be one letter or digit.");
            }),
        NullableLen<TextElement>("fontHeight", "Character height.", v => v.FontHeight, (v, n) => v.FontHeight = n, 1, 32000),
        NullableLen<TextElement>("fontWidth", "Character width (0 = proportional).", v => v.FontWidth, (v, n) => v.FontWidth = n, 0, 32000),
        new PropDef("blockWidth", "Width of a wrapped text block; 0 turns wrapping off.",
            (v, c) => ((TextElement)v).Block is { } b ? c.Out(b.WidthDots) : null,
            (v, j, c) =>
            {
                var t = (TextElement)v;
                int w = c.Dots(ReadNumber(j, "blockWidth"));
                if (w <= 0) { t.Block = null; return; }
                (t.Block ??= new TextBlockOptions()).WidthDots = Math.Clamp(w, 1, 9999);
            }),
        Int<TextElement>("maxLines", "Most lines in a wrapped block.", v => v.Block?.MaxLines, (v, n) => Block(v).MaxLines = n, 1, 9999),
        Len<TextElement>("lineSpacing", "Extra space between lines.", v => v.Block?.LineSpacing ?? 0, (v, n) => Block(v).LineSpacing = n, -9999, 9999),
        Len<TextElement>("hangingIndent", "Indent of the second and later lines.", v => v.Block?.HangingIndent ?? 0, (v, n) => Block(v).HangingIndent = n, 0, 9999),
        new PropDef("alignment", "Left, Center, Right or Justified (wrapped blocks).",
            (v, _) => ((TextElement)v).Block?.Alignment.ToString(),
            (v, j, _) => Block((TextElement)v).Alignment = ParseEnum<TextAlignment>(ReadString(j, "alignment"), "alignment")),
    ];

    private static TextBlockOptions Block(TextElement t) =>
        t.Block ?? throw new McpException("maxLines, lineSpacing, hangingIndent and alignment apply to wrapped text. Give blockWidth (the width of the wrapped block) as well.");

    private static PropDef[] Barcode() =>
    [
        new PropDef("symbology", "Any name from list_symbologies, for example Code128, Ean13, QrCode, DataBar, Aztec or MaxiCode.",
            (v, _) => ((BarcodeElement)v).Symbology.ToString(),
            (v, j, _) => ((BarcodeElement)v).Symbology = ParseSymbology(ReadString(j, "symbology"))),
        Str<BarcodeElement>("data", "The encoded data; {{name}} inserts a variable.", v => v.Data, (v, s) => v.Data = Clip(s, 4000)),
        Len<BarcodeElement>("height", "Bar height (linear codes) or row height (PDF417).", v => v.Height, (v, n) => v.Height = n, 1, 32000),
        Int<BarcodeElement>("moduleWidthDots", "Narrow bar width in dots (1-10), linear codes and PDF417.", v => v.ModuleWidth, (v, n) => v.ModuleWidth = n, 1, 10),
        new PropDef("wideBarRatio", "Wide to narrow bar ratio, 2.0 to 3.0.", (v, _) => ((BarcodeElement)v).WideBarRatio,
            (v, j, _) => ((BarcodeElement)v).WideBarRatio = Math.Clamp(ReadNumber(j, "wideBarRatio"), 2.0, 3.0)),
        Bool<BarcodeElement>("humanReadable", "Print the text line under or above the bars.", v => v.HumanReadable, (v, b) => v.HumanReadable = b),
        new PropDef("humanReadablePosition", "Below or Above.", (v, _) => ((BarcodeElement)v).HumanReadablePosition.ToString(),
            (v, j, _) => ((BarcodeElement)v).HumanReadablePosition = ParseEnum<HumanReadablePosition>(ReadString(j, "humanReadablePosition"), "position")),
        Bool<BarcodeElement>("checkDigit", "Ask the printer to add or print a check digit where the symbology supports it.", v => v.CheckDigit, (v, b) => v.CheckDigit = b),
        new PropDef("code128Mode", "None, Ucc, Automatic or Ean.", (v, _) => ((BarcodeElement)v).Code128Mode.ToString(),
            (v, j, _) => ((BarcodeElement)v).Code128Mode = ParseEnum<Code128Mode>(ReadString(j, "code128Mode"), "Code 128 mode")),
        new PropDef("qrErrorCorrection", "Low, Medium, Quartile or High.", (v, _) => ((BarcodeElement)v).QrErrorCorrection.ToString(),
            (v, j, _) => ((BarcodeElement)v).QrErrorCorrection = ParseEnum<QrErrorCorrection>(ReadString(j, "qrErrorCorrection"), "error correction level")),
        Int<BarcodeElement>("qrModel", "QR model, 1 or 2.", v => v.QrModel, (v, n) => v.QrModel = n, 1, 2),
        Int<BarcodeElement>("moduleSize", "QR module size or Data Matrix element height in dots (1-10).", v => v.ModuleSize, (v, n) => v.ModuleSize = n, 1, 10),
        Int<BarcodeElement>("columns", "Data Matrix / PDF417 columns, or Codablock characters per row (0 = automatic).", v => v.Columns, (v, n) => v.Columns = n, 0, 144),
        Int<BarcodeElement>("rows", "Data Matrix / PDF417 / Codablock rows (0 = automatic).", v => v.Rows, (v, n) => v.Rows = n, 0, 144),
        Int<BarcodeElement>("pdf417SecurityLevel", "PDF417 error correction level, 1-8.", v => v.Pdf417SecurityLevel, (v, n) => v.Pdf417SecurityLevel = n, 1, 8),
        Bool<BarcodeElement>("pdf417Truncate", "PDF417 compact variant.", v => v.Pdf417Truncate, (v, b) => v.Pdf417Truncate = b),
        // The options of the later symbologies (Codabar start/stop, MSI check digits, DataBar variant, Aztec, MaxiCode, ...) are
        // one validated object instead of 22 separate names; list_symbologies says which names each type accepts.
        new PropDef("options", "An object with the extra options of this barcode type, for example {\"codabarStart\":\"B\"} or {\"dataBarType\":\"Expanded\"}. list_symbologies lists them per type.",
            (v, _) =>
            {
                var b = (BarcodeElement)v;
                var current = BarcodeOptionTable.For(b.Symbology).ToDictionary(d => d.Name, d => d.Get(b));
                return current.Count == 0 ? null : current;
            },
            (v, j, _) => BarcodeOptionTable.Apply((BarcodeElement)v, j)),
    ];

    private static PropDef[] Box() =>
    [
        Len<BoxElement>("width", "Width.", v => v.WidthDots, (v, n) => v.WidthDots = n, 1, 32000),
        Len<BoxElement>("height", "Height.", v => v.HeightDots, (v, n) => v.HeightDots = n, 1, 32000),
        Len<BoxElement>("thickness", "Border thickness.", v => v.ThicknessDots, (v, n) => v.ThicknessDots = n, 1, 32000),
        new PropDef("color", "Black or White.", (v, _) => ((BoxElement)v).Color.ToString(), (v, j, _) => ((BoxElement)v).Color = ParseEnum<GraphicColor>(ReadString(j, "color"), "colour")),
        Int<BoxElement>("cornerRounding", "0 (square) to 8.", v => v.CornerRounding, (v, n) => v.CornerRounding = n, 0, 8),
    ];

    private static PropDef[] Line() =>
    [
        Len<LineElement>("length", "Length.", v => v.LengthDots, (v, n) => v.LengthDots = n, 1, 32000),
        Len<LineElement>("thickness", "Thickness.", v => v.ThicknessDots, (v, n) => v.ThicknessDots = n, 1, 32000),
        new PropDef("direction", "Horizontal or Vertical.", (v, _) => ((LineElement)v).Direction.ToString(), (v, j, _) => ((LineElement)v).Direction = ParseEnum<LineDirection>(ReadString(j, "direction"), "direction")),
        new PropDef("color", "Black or White.", (v, _) => ((LineElement)v).Color.ToString(), (v, j, _) => ((LineElement)v).Color = ParseEnum<GraphicColor>(ReadString(j, "color"), "colour")),
    ];

    private static PropDef[] Circle() =>
    [
        Len<CircleElement>("diameter", "Outer diameter.", v => v.DiameterDots, (v, n) => v.DiameterDots = n, 3, 4095),
        Len<CircleElement>("thickness", "Border thickness.", v => v.ThicknessDots, (v, n) => v.ThicknessDots = n, 1, 4095),
        new PropDef("color", "Black or White.", (v, _) => ((CircleElement)v).Color.ToString(), (v, j, _) => ((CircleElement)v).Color = ParseEnum<GraphicColor>(ReadString(j, "color"), "colour")),
    ];

    private static PropDef[] Diagonal() =>
    [
        Len<DiagonalLineElement>("width", "Width of the box the line is drawn in.", v => v.WidthDots, (v, n) => v.WidthDots = n, 3, 32000),
        Len<DiagonalLineElement>("height", "Height of the box the line is drawn in.", v => v.HeightDots, (v, n) => v.HeightDots = n, 3, 32000),
        Len<DiagonalLineElement>("thickness", "Line thickness.", v => v.ThicknessDots, (v, n) => v.ThicknessDots = n, 1, 32000),
        new PropDef("lean", "Right or Left.", (v, _) => ((DiagonalLineElement)v).Lean.ToString(), (v, j, _) => ((DiagonalLineElement)v).Lean = ParseEnum<DiagonalLean>(ReadString(j, "lean"), "lean")),
        new PropDef("color", "Black or White.", (v, _) => ((DiagonalLineElement)v).Color.ToString(), (v, j, _) => ((DiagonalLineElement)v).Color = ParseEnum<GraphicColor>(ReadString(j, "color"), "colour")),
    ];

    private static PropDef[] Ellipse() =>
    [
        Len<EllipseElement>("width", "Width.", v => v.WidthDots, (v, n) => v.WidthDots = n, 3, 4095),
        Len<EllipseElement>("height", "Height.", v => v.HeightDots, (v, n) => v.HeightDots = n, 3, 4095),
        Len<EllipseElement>("thickness", "Border thickness.", v => v.ThicknessDots, (v, n) => v.ThicknessDots = n, 1, 4095),
        new PropDef("color", "Black or White.", (v, _) => ((EllipseElement)v).Color.ToString(), (v, j, _) => ((EllipseElement)v).Color = ParseEnum<GraphicColor>(ReadString(j, "color"), "colour")),
    ];

    private static PropDef[] Symbol() =>
    [
        new PropDef("symbol", "Registered, Copyright, Trademark, UnderwritersLaboratories or CanadianStandards.", (v, _) => ((SymbolElement)v).Symbol.ToString(),
            (v, j, _) => ((SymbolElement)v).Symbol = ParseEnum<GraphicSymbol>(ReadString(j, "symbol"), "symbol")),
        Len<SymbolElement>("height", "Character height.", v => v.HeightDots, (v, n) => v.HeightDots = n, 0, 32000),
        Len<SymbolElement>("width", "Character width.", v => v.WidthDots, (v, n) => v.WidthDots = n, 0, 32000),
    ];

    private static PropDef[] Image() =>
    [
        Len<ImageElement>("width", "Printed width (0 keeps the source size or the aspect ratio).", v => v.WidthDots, (v, n) => v.WidthDots = n, 0, 9999),
        Len<ImageElement>("height", "Printed height (0 keeps the source size or the aspect ratio).", v => v.HeightDots, (v, n) => v.HeightDots = n, 0, 32000),
        new PropDef("dither", "Threshold, FloydSteinberg or Ordered.", (v, _) => ((ImageElement)v).Dither.ToString(), (v, j, _) => ((ImageElement)v).Dither = ParseEnum<ImageDitherMode>(ReadString(j, "dither"), "dither mode")),
        Int<ImageElement>("threshold", "Luminance below this prints black (0-255).", v => v.Threshold, (v, n) => v.Threshold = n, 0, 255),
        Bool<ImageElement>("invert", "Swap black and white.", v => v.Invert, (v, b) => v.Invert = b),
    ];

    public static BarcodeSymbology ParseSymbology(string? text)
    {
        string key = Squash(text);
        switch (key)
        {
            case "qr": case "qrcode": return BarcodeSymbology.QrCode;
            case "i25": case "itf": case "interleaved": case "interleaved2of5": return BarcodeSymbology.Interleaved2Of5;
            case "upc": case "upca": return BarcodeSymbology.UpcA;
            case "ean": case "ean13": return BarcodeSymbology.Ean13;
            case "datamatrix": case "dm": return BarcodeSymbology.DataMatrix;
            case "gs1databar": case "databarexpanded": case "rss14": return BarcodeSymbology.DataBar;
            case "nw7": case "ansicodabar": return BarcodeSymbology.Codabar;
            case "upcean": case "upceanextension": case "ean2": case "ean5": return BarcodeSymbology.UpcEanExtension;
        }
        foreach (var s in Enum.GetValues<BarcodeSymbology>())
            if (Squash(s.ToString()) == key) return s;
        throw new McpException($"Unknown symbology \"{text}\". Choose one of: {string.Join(", ", Enum.GetNames<BarcodeSymbology>())}. Call list_symbologies for details.");
    }

    // ---- builders ---------------------------------------------------------------------------

    private static PropDef Len<T>(string name, string desc, Func<T, int> get, Action<T, int> set, int min, int max) where T : LabelElement =>
        new(name, desc, (v, c) => c.Out(get((T)v)), (v, j, c) => set((T)v, CheckRange(name, c.Dots(ReadNumber(j, name)), min, max)));

    private static PropDef NullableLen<T>(string name, string desc, Func<T, int?> get, Action<T, int?> set, int min, int max) where T : LabelElement =>
        new(name, desc, (v, c) => get((T)v) is { } n ? c.Out(n) : null,
            (v, j, c) => set((T)v, j.ValueKind == JsonValueKind.Null ? null : CheckRange(name, c.Dots(ReadNumber(j, name)), min, max)));

    private static PropDef Int<T>(string name, string desc, Func<T, int?> get, Action<T, int> set, int min, int max) where T : LabelElement =>
        new(name, desc, (v, _) => get((T)v), (v, j, _) => set((T)v, CheckRange(name, ReadInt(j, name), min, max)));

    private static PropDef Bool<T>(string name, string desc, Func<T, bool> get, Action<T, bool> set) where T : LabelElement =>
        new(name, desc, (v, _) => get((T)v), (v, j, _) => set((T)v, ReadBool(j, name)));

    private static PropDef Str<T>(string name, string desc, Func<T, string?> get, Action<T, string> set) where T : LabelElement =>
        new(name, desc, (v, _) => get((T)v), (v, j, _) => set((T)v, ReadString(j, name)));

    // ---- value readers ----------------------------------------------------------------------

    public static double ReadNumber(JsonElement j, string name)
    {
        if (j.ValueKind == JsonValueKind.Number && j.TryGetDouble(out var d) && double.IsFinite(d)) return d;
        if (j.ValueKind == JsonValueKind.String && double.TryParse(j.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d)) return d;
        throw new McpException($"\"{name}\" must be a number.");
    }

    public static int ReadInt(JsonElement j, string name)
    {
        double d = ReadNumber(j, name);
        if (Math.Abs(d) > int.MaxValue) throw new McpException($"\"{name}\" is out of range.");
        return (int)Math.Round(d, MidpointRounding.AwayFromZero);
    }

    public static bool ReadBool(JsonElement j, string name) => j.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(j.GetString(), out var b) => b,
        _ => throw new McpException($"\"{name}\" must be true or false."),
    };

    public static string ReadString(JsonElement j, string name) => j.ValueKind switch
    {
        JsonValueKind.String => j.GetString() ?? string.Empty,
        JsonValueKind.Number => j.GetRawText(),
        _ => throw new McpException($"\"{name}\" must be text."),
    };

    public static int CheckRange(string name, int value, int min, int max) =>
        value < min || value > max ? throw new McpException($"\"{name}\" is out of range (it becomes {value} dots; allowed {min} to {max} dots).") : value;

    public static string Clip(string s, int max) =>
        s.Length > max ? throw new McpException($"That text is too long ({s.Length} characters; the limit is {max}).") : s;

    public static void RequireVariableName(string name)
    {
        if (name.Length is < 1 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new McpException("A variable name may only use letters, digits and underscore (at most 64 characters).");
    }
}
