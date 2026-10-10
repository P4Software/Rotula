using System.Globalization;
using System.Text;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using static System.FormattableString;

namespace Rotula.Core.Zpl;

/// <summary>Raised by <see cref="ZplGenerationResult.GetZplOrThrow"/> when the label has errors.</summary>
public sealed class ZplValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ZplValidationException(IReadOnlyList<ValidationIssue> issues)
        : base("The label has validation errors: " + string.Join("; ", issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey)))
    {
        Issues = issues;
    }

    /// <summary>All issues found (errors and warnings).</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }
}

/// <summary>Output of <see cref="ZplGenerator.Generate"/>.</summary>
public sealed class ZplGenerationResult
{
    internal ZplGenerationResult(string zpl, IReadOnlyList<ValidationIssue> issues)
    {
        Zpl = zpl;
        Issues = issues;
    }

    /// <summary>The ZPL program (^XA...^XZ, LF line endings, pure ASCII). Elements with errors are omitted.</summary>
    public string Zpl { get; }

    /// <summary>Errors and warnings found while generating.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>True when at least one issue is an error (the ZPL then lacks the offending elements).</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);

    /// <summary>Returns <see cref="Zpl"/> or throws <see cref="ZplValidationException"/> when there are errors.</summary>
    public string GetZplOrThrow() => HasErrors ? throw new ZplValidationException(Issues) : Zpl;
}

/// <summary>
/// Deterministic ZPL II generator: the same document and data always produce byte-identical output.
/// The output is pure ASCII (non-ASCII text is hex-escaped with ^FH) so transport encoding cannot corrupt it.
/// </summary>
public static class ZplGenerator
{
    /// <summary>Generates ZPL for <paramref name="doc"/>, merging <paramref name="data"/> into <c>{{name}}</c> placeholders.</summary>
    /// <param name="doc">The label.</param>
    /// <param name="data">Variable values; missing names fall back to the declared sample values.</param>
    public static ZplGenerationResult Generate(LabelDocument doc, IReadOnlyDictionary<string, string>? data = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var issues = new List<ValidationIssue>();
        int widthDots = doc.WidthDots, heightDots = doc.HeightDots;
        ValidateDocument(doc, widthDots, heightDots, issues);

        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append('\n');

        Line("^XA");
        Line("^MN" + doc.MediaTracking switch { MediaTracking.Continuous => "N", MediaTracking.Mark => "M", _ => "Y" });
        Line("^MT" + (doc.MediaType == MediaType.DirectThermal ? "D" : "T"));
        Line(Invariant($"^CI{doc.Encoding.CiCode}"));
        Line(Invariant($"^PW{widthDots}"));
        Line(Invariant($"^LL{heightDots}"));
        Line("^PO" + (doc.Orientation == LabelOrientation.Inverted ? "I" : "N"));
        Line(Invariant($"^LH{doc.Margins.Left.ToDots(doc.Dpi)},{doc.Margins.Top.ToDots(doc.Dpi)}"));
        Line("^FW" + RotationChar(doc.DefaultRotation));
        if (doc.DefaultFont is { } f) Line(Invariant($"^CF{f.FontId},{f.Height},{f.Width}"));

        foreach (var element in doc.Elements)
        {
            int before = issues.Count;
            string? field = EmitElement(doc, element, data, issues);
            bool failed = issues.Skip(before).Any(i => i.Severity == ValidationSeverity.Error);
            if (field is not null && !failed) Line(field);
        }

        var p = doc.Print;
        Line(Invariant($"^PQ{p.Quantity},{p.PauseEvery},{p.Replicates},{(p.OverridePause ? 'Y' : 'N')}"));
        Line("^XZ");
        return new ZplGenerationResult(sb.ToString(), issues);
    }

    // ---- document / element validation ----------------------------------------------------

    private static void ValidateDocument(LabelDocument doc, int w, int h, List<ValidationIssue> issues)
    {
        if (w < 1 || w > 9999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Label.Width.Range", null, "1", "9999"));
        if (h < 1 || h > 32000) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Label.Height.Range", null, "1", "32000"));
        var p = doc.Print;
        if (p.Quantity < 1 || p.Quantity > 99999999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Print.Quantity.Range", null, "1", "99999999"));
        if (p.PauseEvery < 0 || p.PauseEvery > 99999999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Print.Pause.Range", null, "0", "99999999"));
        if (p.Replicates < 1 || p.Replicates > 99999999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Print.Replicates.Range", null, "1", "99999999"));
        if (doc.DefaultFont is { } f && (f.Height < 10 || f.Height > 32000 || f.Width < 0 || f.Width > 32000))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Font.Size.Range", null, "10", "32000"));
    }

    private static void CheckPosition(LabelElement e, LabelDocument doc, int extentW, int extentH, List<ValidationIssue> issues)
    {
        if (e.X < 0 || e.Y < 0 || e.X > 32000 || e.Y > 32000)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Element.Position.Range", e.Id, "0", "32000"));
        else if (e.X + extentW > doc.WidthDots || e.Y + extentH > doc.HeightDots)
            // Only a warning: printers clip silently, and the extent of text/barcodes is unknown here.
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Element.OutsideLabel", e.Id));
    }

    // ---- element emission ------------------------------------------------------------------

    private static string? EmitElement(LabelDocument doc, LabelElement e, IReadOnlyDictionary<string, string>? data, List<ValidationIssue> issues)
    {
        switch (e)
        {
            case TextElement t: return EmitText(doc, t, data, issues);
            case BarcodeElement b: return EmitBarcode(doc, b, data, issues);
            case BoxElement box: return EmitBox(doc, box, issues);
            case LineElement line: return EmitLine(doc, line, issues);
            case CircleElement c: return EmitCircle(doc, c, issues);
            case ImageElement img: return EmitImage(doc, img, issues);
            default:
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Element.Unsupported", e.Id, e.GetType().Name));
                return null;
        }
    }

    private static string Origin(LabelElement e) =>
        Invariant($"^F{(e.OriginKind == FieldOriginKind.Typeset ? 'T' : 'O')}{e.X},{e.Y}") + (e.ReverseField ? "^FR" : string.Empty);

    private static string RotationChar(FieldRotation r) => r switch
    {
        FieldRotation.Rotated90 => "R",
        FieldRotation.Inverted180 => "I",
        FieldRotation.Rotated270 => "B",
        _ => "N",
    };

    private static string RotationOrEmpty(FieldRotation? r) => r is null ? string.Empty : RotationChar(r.Value);

    /// <summary>Joins command parameters and drops trailing empty ones so output stays minimal and stable.</summary>
    private static string Params(params string[] ps)
    {
        int n = ps.Length;
        while (n > 0 && ps[n - 1].Length == 0) n--;
        return string.Join(',', ps.Take(n));
    }

    private static string YN(bool v) => v ? "Y" : "N";

    private static string EmitText(LabelDocument doc, TextElement t, IReadOnlyDictionary<string, string>? data, List<ValidationIssue> issues)
    {
        CheckPosition(t, doc, 0, 0, issues);
        string resolved = VariableResolver.Resolve(t.GetTemplate(), data, doc.Variables, t.Id, issues);
        var sb = new StringBuilder(Origin(t));

        if (t.FontId is not null || t.FontHeight is not null || t.FontWidth is not null || t.Rotation is not null)
        {
            // Unset parts come from ^CF; when the document has none, fall back to ZPL's scalable font so ^A is always complete.
            var def = doc.DefaultFont ?? new FontSpec('0', 30, 30);
            int h = t.FontHeight ?? def.Height, w = t.FontWidth ?? def.Width;
            if (h < 10 || h > 32000 || w < 0 || w > 32000)
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Font.Size.Range", t.Id, "10", "32000"));
            sb.Append(Invariant($"^A{t.FontId ?? def.FontId}{RotationOrEmpty(t.Rotation)},{h},{w}"));
        }

        if (t.Block is { } b)
        {
            if (b.WidthDots < 1 || b.WidthDots > 9999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Block.Width.Range", t.Id, "1", "9999"));
            if (b.MaxLines < 1 || b.MaxLines > 9999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Block.MaxLines.Range", t.Id, "1", "9999"));
            char just = b.Alignment switch { TextAlignment.Center => 'C', TextAlignment.Right => 'R', TextAlignment.Justified => 'J', _ => 'L' };
            sb.Append(Invariant($"^FB{b.WidthDots},{b.MaxLines},{b.LineSpacing},{just},{b.HangingIndent}"));
        }

        var esc = ZplText.Escape(resolved, doc.Encoding, allowLineBreaks: t.Block is not null);
        foreach (var ch in esc.Unmappable)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Text.UnmappableChar", t.Id, ch));
        if (esc.NeedsHexEscape) sb.Append("^FH_");
        sb.Append("^FD").Append(esc.Text).Append("^FS");
        return sb.ToString();
    }

    private static string? EmitBarcode(LabelDocument doc, BarcodeElement b, IReadOnlyDictionary<string, string>? data, List<ValidationIssue> issues)
    {
        string resolved = VariableResolver.Resolve(b.Data, data, doc.Variables, b.Id, issues);
        var found = BarcodeValidator.Validate(b, resolved);
        issues.AddRange(found);
        CheckPosition(b, doc, 0, 0, issues);
        if (found.Any(i => i.Severity == ValidationSeverity.Error)) return null;

        string payload = BarcodeValidator.NormalizeForPrinter(b.Symbology, resolved);
        string rot = RotationOrEmpty(b.Rotation);
        string above = YN(b.HumanReadablePosition == HumanReadablePosition.Above);
        string hr = YN(b.HumanReadable);
        string h = b.Height.ToString(CultureInfo.InvariantCulture);
        string by = Invariant($"^BY{b.ModuleWidth},{b.WideBarRatio:0.0}");
        var sb = new StringBuilder(Origin(b));
        string fdPrefix = string.Empty;

        switch (b.Symbology)
        {
            case BarcodeSymbology.Code128:
                char mode = b.Code128Mode switch { Code128Mode.Ucc => 'U', Code128Mode.Automatic => 'A', Code128Mode.Ean => 'D', _ => 'N' };
                sb.Append(by).Append("^BC").Append(Params(rot, h, hr, above, "N", mode.ToString()));
                break;
            case BarcodeSymbology.Code39:
                sb.Append(by).Append("^B3").Append(Params(rot, YN(b.CheckDigit), h, hr, above));
                break;
            case BarcodeSymbology.Code93:
                sb.Append(by).Append("^BA").Append(Params(rot, h, hr, above, YN(b.CheckDigit)));
                break;
            case BarcodeSymbology.Ean13:
                sb.Append(by).Append("^BE").Append(Params(rot, h, hr, above));
                break;
            case BarcodeSymbology.UpcA:
                sb.Append(by).Append("^BU").Append(Params(rot, h, hr, above, YN(b.CheckDigit)));
                break;
            case BarcodeSymbology.Interleaved2Of5:
                sb.Append(by).Append("^B2").Append(Params(rot, h, hr, above, YN(b.CheckDigit)));
                break;
            case BarcodeSymbology.QrCode:
                sb.Append("^BQ").Append(Params(rot, b.QrModel.ToString(CultureInfo.InvariantCulture), b.ModuleSize.ToString(CultureInfo.InvariantCulture)));
                // QR data carries its own header: error correction letter + "A" (automatic input mode) + comma.
                fdPrefix = (b.QrErrorCorrection switch { QrErrorCorrection.Low => "L", QrErrorCorrection.Medium => "M", QrErrorCorrection.High => "H", _ => "Q" }) + "A,";
                break;
            case BarcodeSymbology.DataMatrix:
                sb.Append("^BX").Append(Params(rot, b.ModuleSize.ToString(CultureInfo.InvariantCulture), "200",
                    b.Columns > 0 ? b.Columns.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    b.Rows > 0 ? b.Rows.ToString(CultureInfo.InvariantCulture) : string.Empty));
                break;
            case BarcodeSymbology.Pdf417:
                sb.Append(Invariant($"^BY{b.ModuleWidth}")).Append("^B7").Append(Params(rot, h, b.Pdf417SecurityLevel.ToString(CultureInfo.InvariantCulture),
                    b.Columns > 0 ? b.Columns.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    b.Rows > 0 ? b.Rows.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    YN(b.Pdf417Truncate)));
                break;
        }

        var esc = ZplText.Escape(payload, doc.Encoding, allowLineBreaks: false);
        if (esc.NeedsHexEscape) sb.Append("^FH_");
        sb.Append("^FD").Append(fdPrefix).Append(esc.Text).Append("^FS");
        return sb.ToString();
    }

    private static string? EmitBox(LabelDocument doc, BoxElement b, List<ValidationIssue> issues)
    {
        CheckPosition(b, doc, b.WidthDots, b.HeightDots, issues);
        if (b.WidthDots < 1 || b.HeightDots < 1 || b.WidthDots > 32000 || b.HeightDots > 32000 || b.ThicknessDots < 1 || b.ThicknessDots > 32000)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Graphic.Size.Range", b.Id, "1", "32000"));
        if (b.CornerRounding < 0 || b.CornerRounding > 8)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Graphic.Rounding.Range", b.Id, "0", "8"));
        return Origin(b) + "^GB" + Params(b.WidthDots.ToString(CultureInfo.InvariantCulture), b.HeightDots.ToString(CultureInfo.InvariantCulture),
            b.ThicknessDots.ToString(CultureInfo.InvariantCulture), ColorChar(b.Color), b.CornerRounding.ToString(CultureInfo.InvariantCulture)) + "^FS";
    }

    private static string? EmitLine(LabelDocument doc, LineElement l, List<ValidationIssue> issues)
    {
        bool horizontal = l.Direction == LineDirection.Horizontal;
        CheckPosition(l, doc, horizontal ? l.LengthDots : l.ThicknessDots, horizontal ? l.ThicknessDots : l.LengthDots, issues);
        if (l.LengthDots < 1 || l.ThicknessDots < 1 || l.LengthDots > 32000 || l.ThicknessDots > 32000)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Graphic.Size.Range", l.Id, "1", "32000"));
        int w = horizontal ? l.LengthDots : l.ThicknessDots, h = horizontal ? l.ThicknessDots : l.LengthDots;
        return Origin(l) + Invariant($"^GB{w},{h},{l.ThicknessDots},{ColorChar(l.Color)}^FS");
    }

    private static string? EmitCircle(LabelDocument doc, CircleElement c, List<ValidationIssue> issues)
    {
        CheckPosition(c, doc, c.DiameterDots, c.DiameterDots, issues);
        if (c.DiameterDots < 3 || c.DiameterDots > 4095 || c.ThicknessDots < 1 || c.ThicknessDots > 4095)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Graphic.Circle.Range", c.Id, "3", "4095"));
        return Origin(c) + Invariant($"^GC{c.DiameterDots},{c.ThicknessDots},{ColorChar(c.Color)}^FS");
    }

    private static string? EmitImage(LabelDocument doc, ImageElement img, List<ValidationIssue> issues)
    {
        if (img.Source is null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Image.Source.Missing", img.Id));
            return null;
        }
        if (img.Threshold < 0 || img.Threshold > 255)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Image.Threshold.Range", img.Id, "0", "255"));
            return null;
        }
        var (rw, rh) = ImageProcessor.ResolveSize(img);
        if (rw > 9999 || rh > 32000)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Image.Size.Range", img.Id, "9999", "32000"));
            return null;
        }
        var mono = ImageProcessor.ToMonochrome(img);
        CheckPosition(img, doc, mono.Width, mono.Height, issues);
        int total = mono.PackedRows.Length;
        // ^GF ignores ^FW and ^FO orientation, so rotation was already applied to the dots.
        return Origin(img) + Invariant($"^GFA,{total},{total},{mono.BytesPerRow},{mono.ToHex()}^FS");
    }

    private static string ColorChar(GraphicColor c) => c == GraphicColor.White ? "W" : "B";
}
