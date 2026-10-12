using System.Globalization;
using System.Text;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
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
    /// <param name="rasterizer">
    /// Draws the text of Windows fonts to a picture (see <see cref="TextElement.Font"/>). Without one, such a text is an error and is not printed;
    /// a label that uses only printer fonts never needs it.
    /// </param>
    public static ZplGenerationResult Generate(LabelDocument doc, IReadOnlyDictionary<string, string>? data = null, ITextRasterizer? rasterizer = null) =>
        GenerateCore(doc, data, null, null, rasterizer);

    /// <summary>
    /// Generates ZPL after resolving every variable of the label (dates, counters, prompts, computed values) from <paramref name="context"/>. The
    /// clock, the data record, the operator's answers and the counter positions all come from the context, so the same label and context always give
    /// the same bytes. Date variables whose source is the printer clock become clock codes (<c>^FC</c>, with <c>^SO</c> for offsets) in text fields.
    /// Problems of the variables are part of the result's issues.
    /// </summary>
    /// <param name="doc">The label.</param>
    /// <param name="context">Where variable values come from.</param>
    /// <param name="quantity">When set, replaces the label's own print quantity (<c>^PQ</c>); a run whose labels all differ passes 1.</param>
    /// <param name="rasterizer">Draws the text of Windows fonts (see <see cref="Generate"/>); the text is drawn after the variables are replaced, once per label.</param>
    public static ZplGenerationResult GenerateWithVariables(LabelDocument doc, VariableContext context, int? quantity = null, ITextRasterizer? rasterizer = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(context);
        var resolution = VariableEngine.Resolve(doc, context);
        return GenerateCore(doc, resolution.Values, resolution, quantity, rasterizer);
    }

    private static ZplGenerationResult GenerateCore(LabelDocument doc, IReadOnlyDictionary<string, string>? data, VariableResolution? resolution, int? quantity, ITextRasterizer? rasterizer)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var issues = new List<ValidationIssue>();
        if (resolution is not null) issues.AddRange(resolution.Issues);
        int widthDots = doc.WidthDots, heightDots = doc.HeightDots;
        ValidateDocument(doc, widthDots, heightDots, issues);

        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append('\n');

        // Downloaded graphics are immediate (tilde) commands: they go before the label format, not inside it.
        foreach (var g in doc.StoredGraphics) Line(StoredGraphicCommand(g));

        Line("^XA");
        Line("^MN" + doc.MediaTracking switch { MediaTracking.Continuous => "N", MediaTracking.Mark => "M", _ => "Y" });
        Line("^MT" + (doc.MediaType == MediaType.DirectThermal ? "D" : "T"));
        Line(Invariant($"^CI{doc.Encoding.CiCode}"));
        Line(Invariant($"^PW{widthDots}"));
        Line(Invariant($"^LL{heightDots}"));
        Line("^PO" + (doc.Orientation == LabelOrientation.Inverted ? "I" : "N"));
        Line(Invariant($"^LH{doc.Margins.Left.ToDots(doc.Dpi)},{doc.Margins.Top.ToDots(doc.Dpi)}"));
        // Optional printer settings: written only when the design sets them, so a plain label stays byte-for-byte as before.
        if (doc.LabelShift != 0) Line(Invariant($"^LS{doc.LabelShift}"));
        if (doc.LabelTop != 0) Line(Invariant($"^LT{doc.LabelTop}"));
        if (doc.MirrorImage) Line("^PMY");
        if (doc.PrintMode is { } mode) Line("^MM" + PrintModeChar(mode) + (doc.PrePeel ? ",Y" : string.Empty));
        if (doc.PrintRate is { IsEmpty: false } rate)
            Line("^PR" + Params(rate.Speed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                rate.SlewSpeed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, rate.BackfeedSpeed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        if (doc.MediaDarkness is { } darkness) Line("^MD" + darkness.ToString(CultureInfo.InvariantCulture));
        Line("^FW" + RotationChar(doc.DefaultRotation));
        if (doc.DefaultFont is { } f) Line(Invariant($"^CF{f.FontId},{f.Height},{f.Width}"));
        foreach (var alias in doc.FontAliases) Line("^CW" + alias.Letter + "," + alias.Path);
        // Offsets of the printer's secondary and third clock, defined once for the whole label before any field uses them.
        if (resolution is not null)
            foreach (var (clockSet, offset) in resolution.ClockOffsets.OrderBy(kv => kv.Key)) Line(PrinterClock.OffsetCommand(clockSet, offset));

        foreach (var element in doc.Elements)
        {
            // An object that is switched off for this record is not emitted and not validated: a barcode that is only invalid on a record
            // where it is hidden must not stop that record from printing. The same data that fills the placeholders decides the condition.
            if (!ElementPrinting.IsPrinted(element, data, doc.Variables, issues)) continue;
            int before = issues.Count;
            string? field = EmitElement(doc, element, data, resolution, issues, rasterizer);
            bool failed = issues.Skip(before).Any(i => i.Severity == ValidationSeverity.Error);
            if (field is not null && !failed) Line(field);
        }

        var p = doc.Print;
        Line(Invariant($"^PQ{quantity ?? p.Quantity},{p.PauseEvery},{p.Replicates},{(p.OverridePause ? 'Y' : 'N')}"));
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
        if (doc.LabelShift < -9999 || doc.LabelShift > 9999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Label.Shift.Range", null, "-9999", "9999"));
        if (doc.LabelTop < -240 || doc.LabelTop > 240) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Label.Top.Range", null, "-240", "240"));
        if (doc.MediaDarkness is < -30m or > 30m) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Print.Darkness.Range", null, "-30", "30"));
        if (doc.PrintRate is { } pr && new[] { pr.Speed, pr.SlewSpeed, pr.BackfeedSpeed }.Any(v => v is < 1 or > 14))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Print.Rate.Range", null, "1", "14"));
        foreach (var alias in doc.FontAliases)
            if (!(char.IsAsciiLetterOrDigit(alias.Letter) && FontPath.IsMatch(alias.Path)))
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Font.Alias.Invalid", null, alias.Letter.ToString(), alias.Path));
        foreach (var g in doc.StoredGraphics)
            if (!StoredGraphic.IsValidPath(g.Path) || g.Data.Length > 4 * 1024 * 1024)
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Graphic.Stored.Invalid", null, g.Path));
        if (doc.DefaultFont is { } f && (f.Height < 10 || f.Height > 32000 || f.Width < 0 || f.Width > 32000))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Font.Size.Range", null, "10", "32000"));
    }

    // Font file names: an optional device letter, a name of up to 8 characters and a font extension.
    private static readonly System.Text.RegularExpressions.Regex FontPath = new(@"^(?:[REBA]:)?[A-Za-z0-9_\-]{1,8}\.(?:FNT|TTF|TTE|fnt|ttf|tte)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StoredGraphicCommand(StoredGraphic g) =>
        Invariant($"~DG{g.Path},{g.Data.Length},{g.BytesPerRow},") + Convert.ToHexString(g.Data);

    private static char PrintModeChar(PrintMode m) => m switch
    {
        PrintMode.Peel => 'P',
        PrintMode.Rewind => 'R',
        PrintMode.Applicator => 'A',
        PrintMode.Cutter => 'C',
        PrintMode.DelayedCutter => 'D',
        PrintMode.Rfid => 'F',
        PrintMode.Kiosk => 'K',
        _ => 'T',
    };

    private static void CheckPosition(LabelElement e, LabelDocument doc, int extentW, int extentH, List<ValidationIssue> issues)
    {
        if (e.X < 0 || e.Y < 0 || e.X > 32000 || e.Y > 32000)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Element.Position.Range", e.Id, "0", "32000"));
        else if (e.X + extentW > doc.WidthDots || e.Y + extentH > doc.HeightDots)
            // Only a warning: printers clip silently, and the extent of text/barcodes is unknown here.
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Element.OutsideLabel", e.Id));
    }

    // ---- element emission ------------------------------------------------------------------

    private static string? EmitElement(LabelDocument doc, LabelElement e, IReadOnlyDictionary<string, string>? data, VariableResolution? resolution, List<ValidationIssue> issues, ITextRasterizer? rasterizer)
    {
        switch (e)
        {
            case TextElement t: return EmitText(doc, t, data, resolution, issues, rasterizer);
            case BarcodeElement b: return EmitBarcode(doc, b, data, issues);
            case BoxElement box: return EmitBox(doc, box, issues);
            case LineElement line: return EmitLine(doc, line, issues);
            case CircleElement c: return EmitCircle(doc, c, issues);
            case EllipseElement el: return EmitEllipse(doc, el, issues);
            case DiagonalLineElement dl: return EmitDiagonal(doc, dl, issues);
            case SymbolElement sy: return EmitSymbol(doc, sy, issues);
            case ImageElement img: return EmitImage(doc, img, issues);
            // Imported content the designer cannot model is written back unchanged.
            case RawZplElement raw: return raw.Zpl.Length == 0 ? null : raw.Zpl;
            case RecallPlaceholderElement recall:
                return recall.Zpl.Length == 0 ? null : recall.EmitAsField ? Origin(recall) + recall.Zpl + "^FS" : recall.Zpl;
            default:
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Element.Unsupported", e.Id, e.GetType().Name));
                return null;
        }
    }

    internal static string Origin(LabelElement e) =>
        Invariant($"^F{(e.OriginKind == FieldOriginKind.Typeset ? 'T' : 'O')}{e.X},{e.Y}") + (e.ReverseField ? "^FR" : string.Empty);

    internal static string RotationChar(FieldRotation r) => r switch
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

    private static string? EmitText(LabelDocument doc, TextElement t, IReadOnlyDictionary<string, string>? data, VariableResolution? resolution, List<ValidationIssue> issues, ITextRasterizer? rasterizer)
    {
        // A Windows font cannot exist on the printer: the text is drawn to a picture and the label carries the picture.
        if (t.Font is { IsWindows: true } windowsFont)
            return RasterTextEmitter.Emit(doc, t, windowsFont, data, resolution, issues, rasterizer);

        CheckPosition(t, doc, 0, 0, issues);
        string resolved = ResolveTextWithClock(doc, t, data, resolution, issues, out FieldClockOptions? autoClock);
        var sb = new StringBuilder(Origin(t));

        // Text fit: decided here, for this record's own text, so every label of a data merge gets the size its content needs. The printer only
        // ever sees the computed ^A / ^FB values; the fit mode itself is Rotula metadata.
        var fitted = TextFitResolver.Resolve(doc, t, resolved, rasterizer);

        if (t.Direction != FieldDirection.Horizontal || t.InterCharacterGap != 0)
        {
            if (t.InterCharacterGap < 0 || t.InterCharacterGap > 9999)
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Direction.Range", t.Id, "0", "9999"));
            char dir = t.Direction switch { FieldDirection.Vertical => 'V', FieldDirection.Reverse => 'R', _ => 'H' };
            sb.Append("^FP").Append(Params(dir.ToString(), t.InterCharacterGap == 0 ? string.Empty : t.InterCharacterGap.ToString(CultureInfo.InvariantCulture)));
        }

        // A font choice that names a printer font stands for its id; bold, italic and underline do not exist in the printer's own fonts.
        char? fontId = t.Font is { IsWindows: false } printerChoice ? printerChoice.PrinterFontId : t.FontId;
        if (t.Font is { IsWindows: false, Bold: true } or { IsWindows: false, Italic: true } or { IsWindows: false, Underline: true })
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Text.Style.PrinterFont", t.Id));
        if (fontId is not null || t.StoredFontPath is not null || t.FontHeight is not null || t.FontWidth is not null || t.Rotation is not null || fitted?.FontHeight is not null)
        {
            // Unset parts come from ^CF; when the document has none, fall back to ZPL's scalable font so ^A is always complete.
            // A fitted size is always written in full (height and width), because the default width would not follow a smaller height.
            var def = doc.DefaultFont ?? new FontSpec('0', 30, 30);
            int h = fitted?.FontHeight ?? t.FontHeight ?? def.Height, w = fitted?.FontWidth ?? t.FontWidth ?? def.Width;
            if (h < 10 || h > 32000 || w < 0 || w > 32000)
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Font.Size.Range", t.Id, "10", "32000"));
            if (t.StoredFontPath is { } stored)
            {
                // ^A@ prints with a font file that is already on the printer (the same names ^CW accepts).
                if (!StoredFonts.IsValidPath(stored)) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.StoredFont.Invalid", t.Id, stored));
                sb.Append(Invariant($"^A@{RotationOrEmpty(t.Rotation)},{h},{w},{stored}"));
            }
            else sb.Append(Invariant($"^A{fontId ?? def.FontId}{RotationOrEmpty(t.Rotation)},{h},{w}"));
        }

        if (t.Block is { } b)
        {
            if (b.WidthDots < 1 || b.WidthDots > 9999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Block.Width.Range", t.Id, "1", "9999"));
            if (b.MaxLines < 1 || b.MaxLines > 9999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Block.MaxLines.Range", t.Id, "1", "9999"));
            char just = b.Alignment switch { TextAlignment.Center => 'C', TextAlignment.Right => 'R', TextAlignment.Justified => 'J', _ => 'L' };
            sb.Append(Invariant($"^FB{b.WidthDots},{fitted?.MaxLines ?? b.MaxLines},{b.LineSpacing},{just},{b.HangingIndent}"));
        }
        if (t.TextBox is { } box)
        {
            // ^FB and ^TB both lay out a block; a field can only have one of them.
            if (t.Block is not null) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Box.WithBlock", t.Id));
            if (box.WidthDots < 1 || box.WidthDots > 32000 || box.HeightDots < 1 || box.HeightDots > 32000)
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Box.Range", t.Id, "1", "32000"));
            sb.Append("^TB").Append(Params(RotationOrEmpty(t.Rotation), box.WidthDots.ToString(CultureInfo.InvariantCulture), box.HeightDots.ToString(CultureInfo.InvariantCulture)));
        }

        // The printer does not tell how wide its letters are, so this is an estimate; the picture of a Windows font is measured exactly instead.
        // A fitted text was already measured at its final size; "ignore" silences the warning for blocks where cut-off text is intended.
        if ((t.Block is not null || t.TextBox is not null) && t.Block?.Fit is not { IgnoreOverflowWarning: true })
        {
            var fit = fitted?.Overflow ?? TextFitEstimator.Estimate(t, resolved, doc.DefaultFont);
            if (fit != TextOverflow.None) issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Text.Overflow.Estimated", t.Id));
        }

        AppendDataField(sb, doc, t, resolved, allowLineBreaks: t.Block is not null, textBox: t.TextBox is not null, fdPrefix: string.Empty, issues, autoClock);
        return sb.ToString();
    }

    /// <summary>
    /// Writes the part of a data field after its command: field options, then the data (<c>^FD</c>, or <c>^SN</c> for a counting
    /// number) and finally <c>^FS</c>. Shared by text and barcodes so both support the same options.
    /// </summary>
    private static void AppendDataField(StringBuilder sb, LabelDocument doc, FieldDataElement e, string text, bool allowLineBreaks, bool textBox, string fdPrefix, List<ValidationIssue> issues, FieldClockOptions? derivedClock = null)
    {
        var serial = e.Serial;
        string? body = null; // the ^SN replacement for ^FD

        if (serial is { Kind: PrinterSerialKind.Number })
        {
            // ^SN carries the value as a plain parameter, which has no hex escape: only simple characters can be written.
            if (!PrinterSerialOptions.IsValidStartValue(text))
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Field.Serial.Value", e.Id));
            if (fdPrefix.Length > 0) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Field.Serial.Barcode", e.Id));
            body = "^SN" + Params(text, serial.Increment.ToString(CultureInfo.InvariantCulture), YN(serial.LeadingZeros));
        }
        else if (serial is { Kind: PrinterSerialKind.Mask })
        {
            if (!PrinterSerialOptions.IsValidMask(serial.Mask) || !PrinterSerialOptions.IsValidIncrementText(serial.IncrementText))
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Field.Serial.Mask", e.Id));
        }

        string? hexMarker = null;
        string fd = string.Empty;
        if (body is null)
        {
            var esc = ZplText.Escape(text, doc.Encoding, allowLineBreaks);
            if (e is TextElement) foreach (var ch in esc.Unmappable)
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Text.UnmappableChar", e.Id, ch));
            string escaped = esc.Text;
            // Inside a ^TB box a less-than sign starts an escape sequence, and the three characters "<<>" write a literal one.
            if (textBox) escaped = escaped.Replace("<", "<<>", StringComparison.Ordinal);
            if (esc.NeedsHexEscape) hexMarker = "^FH_";
            fd = "^FD" + fdPrefix + escaped;
        }

        if (hexMarker is not null) sb.Append(hexMarker);
        if (e.EmbedCharacter is { } embed)
        {
            if (embed is '^' or '~' || embed < ' ' || embed > '~') issues.Add(new ValidationIssue(ValidationSeverity.Error, "Field.Embed.Invalid", e.Id));
            sb.Append("^FE").Append(embed);
        }
        // A clock the user set on the field wins; otherwise use the one derived from printer-clock date variables.
        if ((e.Clock ?? derivedClock) is { } clock)
        {
            var present = new List<char> { clock.Primary };
            if (clock.Secondary is { } second) present.Add(second);
            if (clock.Third is { } third) present.Add(third);
            if (present.Any(m => m is '^' or '~' or ',' || m < ' ' || m > '~') || present.Distinct().Count() != present.Count)
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Field.Clock.Invalid", e.Id));
            // The primary indicator is always written so the command never depends on a default.
            sb.Append("^FC").Append(Params(clock.Primary.ToString(), clock.Secondary?.ToString() ?? string.Empty, clock.Third?.ToString() ?? string.Empty));
        }
        if (e.FieldNumber is { } number)
        {
            if (number < 0 || number > 9999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Field.Number.Range", e.Id, "0", "9999"));
            sb.Append("^FN").Append(number.ToString(CultureInfo.InvariantCulture));
            if (e.FieldPrompt is { Length: > 0 } prompt)
            {
                if (prompt.Length > 255 || !prompt.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == ' '))
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, "Field.Prompt.Invalid", e.Id));
                sb.Append('"').Append(prompt).Append('"');
            }
        }

        if (body is not null) sb.Append(body);
        // A numbered field without data only refers to the data another field stored under that number.
        else if (text.Length > 0 || e.FieldNumber is null) sb.Append(fd);
        if (serial is { Kind: PrinterSerialKind.Mask })
            sb.Append("^SF").Append(Params(serial.Mask, serial.IncrementText));
        sb.Append("^FS");
    }

    /// <summary>
    /// Resolves the placeholders of a text field. Date variables bound to the printer clock are written as clock codes and the clock indicator
    /// characters are chosen so that none of them occurs in the rest of the field's text (the printer would read it as a code).
    /// </summary>
    private static string ResolveTextWithClock(LabelDocument doc, TextElement t, IReadOnlyDictionary<string, string>? data, VariableResolution? resolution,
        List<ValidationIssue> issues, out FieldClockOptions? derivedClock)
    {
        derivedClock = null;
        string template = t.GetTemplate();
        if (resolution is null || resolution.PrinterBindings.Count == 0)
            return VariableResolver.Resolve(template, data, doc.Variables, t.Id, issues);
        var used = VariableResolver.FindVariables(template).Where(resolution.PrinterBindings.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(n => resolution.PrinterBindings[n]).ToList();
        if (used.Count == 0) return VariableResolver.Resolve(template, data, doc.Variables, t.Id, issues);

        // Indicator per clock set (1 = primary, 2 = secondary, 3 = third).
        var indicator = new Dictionary<int, char>();
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (t.Clock is { } own)
        {
            indicator[1] = own.Primary;
            if (own.Secondary is { } s2) indicator[2] = s2;
            if (own.Third is { } s3) indicator[3] = s3;
            foreach (var b in used)
                if (!indicator.ContainsKey(b.ClockSet)) denied.Add(b.VariableName);
            foreach (var name in denied)
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.PrinterNoIndicator, t.Id, name));
        }
        else
        {
            // Everything that will be in the field apart from the clock codes: the fixed text, other variables' values and the separators inside the formats.
            var probe = new HashSet<char>(VariableResolver.Resolve(template, data, doc.Variables, t.Id, new List<ValidationIssue>(), name => resolution.PrinterBindings.ContainsKey(name) ? string.Empty : null));
            foreach (var b in used)
                for (int i = 0; i < b.TokenFormat.Length; i++)
                {
                    if (b.TokenFormat[i] == '%') i++;
                    else probe.Add(b.TokenFormat[i]);
                }
            int top = used.Max(b => b.ClockSet);
            var free = PrinterClock.IndicatorCandidates.Where(c => !probe.Contains(c)).GetEnumerator();
            for (int set = 1; set <= top; set++)
            {
                if (!free.MoveNext())
                {
                    // Not enough unused characters: fall back to computer values for the whole field.
                    foreach (var b in used) issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.PrinterNoFreeIndicator, t.Id, b.VariableName));
                    return VariableResolver.Resolve(template, data, doc.Variables, t.Id, issues);
                }
                indicator[set] = free.Current;
            }
            derivedClock = new FieldClockOptions { Primary = indicator[1], Secondary = top >= 2 ? indicator[2] : null, Third = top >= 3 ? indicator[3] : null };
        }

        return VariableResolver.Resolve(template, data, doc.Variables, t.Id, issues, name =>
        {
            if (!resolution.PrinterBindings.TryGetValue(name, out var b) || denied.Contains(name) || !indicator.TryGetValue(b.ClockSet, out char c)) return null;
            return PrinterClock.Render(b.TokenFormat, c);
        });
    }

    private static string? EmitBarcode(LabelDocument doc, BarcodeElement b, IReadOnlyDictionary<string, string>? data, List<ValidationIssue> issues)
    {
        // A QR Code made by the content builder is built from its structured definition per record, so escaping is applied to the values
        // after substitution; Data (the template) is only used when there is no definition.
        string resolved = Barcodes.QrContent.QrContentBuilder.ResolveForElement(b, data, doc.Variables, issues)
            ?? VariableResolver.Resolve(b.Data, data, doc.Variables, b.Id, issues);
        var found = BarcodeValidator.Validate(b, resolved);
        issues.AddRange(found);
        CheckPosition(b, doc, 0, 0, issues);
        if (found.Any(i => i.Severity == ValidationSeverity.Error)) return null;

        string payload = BarcodeValidator.NormalizeForPrinter(b, resolved);
        var sb = new StringBuilder(Origin(b));
        sb.Append(ZplBarcodeEmitter.Command(b, RotationOrEmpty(b.Rotation), RotationChar(doc.DefaultRotation), out string fdPrefix));
        if (fdPrefix.Contains(ZplBarcodeEmitter.QrByteCountToken, StringComparison.Ordinal))
            // Manual byte input states how many bytes follow, counted in the label's encoding (UTF-8 under ^CI28).
            fdPrefix = fdPrefix.Replace(ZplBarcodeEmitter.QrByteCountToken,
                Math.Min(9999, doc.Encoding.GetEncoding().GetByteCount(payload)).ToString("0000", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        AppendDataField(sb, doc, b, payload, allowLineBreaks: false, textBox: false, fdPrefix, issues);
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

    private static string? EmitEllipse(LabelDocument doc, EllipseElement e, List<ValidationIssue> issues)
    {
        CheckPosition(e, doc, e.WidthDots, e.HeightDots, issues);
        if (e.WidthDots < 3 || e.WidthDots > 4095 || e.HeightDots < 3 || e.HeightDots > 4095 || e.ThicknessDots < 1 || e.ThicknessDots > 4095)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Graphic.Ellipse.Range", e.Id, "3", "4095"));
        return Origin(e) + Invariant($"^GE{e.WidthDots},{e.HeightDots},{e.ThicknessDots},{ColorChar(e.Color)}^FS");
    }

    private static string? EmitDiagonal(LabelDocument doc, DiagonalLineElement d, List<ValidationIssue> issues)
    {
        CheckPosition(d, doc, d.WidthDots, d.HeightDots, issues);
        if (d.WidthDots < 3 || d.WidthDots > 32000 || d.HeightDots < 3 || d.HeightDots > 32000 || d.ThicknessDots < 1 || d.ThicknessDots > 32000)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Graphic.Diagonal.Range", d.Id, "3", "32000"));
        return Origin(d) + Invariant($"^GD{d.WidthDots},{d.HeightDots},{d.ThicknessDots},{ColorChar(d.Color)},{(d.Lean == DiagonalLean.Left ? 'L' : 'R')}^FS");
    }

    private static string? EmitSymbol(LabelDocument doc, SymbolElement s, List<ValidationIssue> issues)
    {
        CheckPosition(s, doc, s.WidthDots, s.HeightDots, issues);
        if (s.HeightDots < 0 || s.HeightDots > 32000 || s.WidthDots < 0 || s.WidthDots > 32000)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Graphic.Symbol.Range", s.Id, "0", "32000"));
        char letter = (char)('A' + (int)s.Symbol);
        // Height and width are always written: without them the printer would use whatever ^CF last set.
        return Origin(s) + Invariant($"^GS{RotationOrEmpty(s.Rotation)},{s.HeightDots},{s.WidthDots}^FD{letter}^FS");
    }

    private static string ColorChar(GraphicColor c) => c == GraphicColor.White ? "W" : "B";
}
