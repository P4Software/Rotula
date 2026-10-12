using System.Globalization;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Validation;
using Rotula.Core.Variables;

namespace Rotula.Core.Zpl;

/// <summary>
/// Writes a text in a Windows font as a graphic field. The printer cannot have the font, so the text is drawn to a one-bit picture at the
/// printer's resolution and the label carries the picture (<c>^GF</c>) in place of the characters.
/// </summary>
/// <remarks>
/// <para><b>Data binding.</b> The picture is drawn after the variables are replaced, once for every label, so a bound text such as
/// <c>{{description}}</c> prints the record's own words in the chosen font.</para>
/// <para><b>What a picture cannot do.</b> It is not text, so the printer cannot count it (<c>^SN</c>, <c>^SF</c>), take it from a stored field
/// number (<c>^FN</c>), embed characters (<c>^FE</c>) or fill in its clock (<c>^FC</c>): the first four are errors, and a printer-clock date inside
/// the text is replaced by the computer clock's value with a warning.</para>
/// </remarks>
internal static class RasterTextEmitter
{
    // The same limits an image gets: a printer rejects wider graphics, and a 4 MB picture would overflow its memory.
    private const int MaxWidth = 9999;
    private const int MaxHeight = 32000;
    private const int MaxBytes = 4 * 1024 * 1024;

    public static string? Emit(LabelDocument doc, TextElement t, FontChoice font, IReadOnlyDictionary<string, string>? data, VariableResolution? resolution,
        List<ValidationIssue> issues, ITextRasterizer? rasterizer)
    {
        if (font.Validate() is { } invalid)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, invalid, t.Id, font.FamilyName));
            return null;
        }
        if (t.Serial is not null || t.FieldNumber is not null || t.EmbedCharacter is not null || t.Clock is not null)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Raster.FieldOption", t.Id));
        if (t.Block is not null && t.TextBox is not null)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Box.WithBlock", t.Id));
        if (t.Direction != FieldDirection.Horizontal || t.InterCharacterGap != 0)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Text.Raster.Direction", t.Id));

        string template = t.GetTemplate();
        if (resolution is not null)
            foreach (var name in VariableResolver.FindVariables(template).Distinct(StringComparer.OrdinalIgnoreCase))
                if (resolution.PrinterBindings.ContainsKey(name))
                    issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Text.Raster.PrinterClock", t.Id, name));
        // Data values are in place before the picture is drawn: this is the whole data-binding rule.
        string text = VariableResolver.Resolve(template, data, doc.Variables, t.Id, issues);

        bool laidOut = t.Block is not null || t.TextBox is not null;
        // Outside a block the printer prints one line, so line breaks are spaces (the preview does the same).
        if (!laidOut) text = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        if (text.Length == 0) return null; // nothing to print; not a mistake (an empty record value)

        if (rasterizer is null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Raster.NoRasterizer", t.Id, font.FamilyName));
            return null;
        }

        int boxWidth = t.Block?.WidthDots ?? t.TextBox?.WidthDots ?? 0;
        if (t.Block is { } b)
        {
            if (b.WidthDots < 1 || b.WidthDots > MaxWidth) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Block.Width.Range", t.Id, "1", "9999"));
            if (b.MaxLines < 1 || b.MaxLines > 9999) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Block.MaxLines.Range", t.Id, "1", "9999"));
        }
        if (t.TextBox is { } box && (box.WidthDots < 1 || box.WidthDots > 32000 || box.HeightDots < 1 || box.HeightDots > 32000))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Box.Range", t.Id, "1", "32000"));
        if (issues.Any(i => i.ElementId == t.Id && i.Severity == ValidationSeverity.Error)) return null;

        // Text fit: the size or the line count this record's text needs, decided with the same layout the picture is drawn with.
        var fitted = TextFitResolver.Resolve(doc, t, text, rasterizer);
        if (fitted?.PointSize is { } fittedSize)
        {
            font = font.Clone();
            font.PointSize = fittedSize;
        }
        var request = new TextRasterRequest(text, font, (int)doc.Dpi, boxWidth, t.TextBox?.HeightDots ?? 0, fitted?.MaxLines ?? t.Block?.MaxLines ?? 0,
            t.Block?.LineSpacing ?? 0, t.Block?.Alignment ?? TextAlignment.Left, t.Block?.HangingIndent ?? 0);
        TextRasterResult result;
        try { result = rasterizer.Rasterize(request); }
        catch (ArgumentException)
        {
            // A size the drawing code refuses (too large to draw): reported like any other size problem.
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Raster.TooLarge", t.Id));
            return null;
        }
        var m = result.Measurement;
        if (m.FontSubstituted) issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Text.Font.Missing", t.Id, font.FamilyName, m.ActualFamily));
        if (m.Overflows && t.Block?.Fit is not { IgnoreOverflowWarning: true }) issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Text.Overflow", t.Id));

        var rotation = t.Rotation ?? doc.DefaultRotation;
        var bitmap = result.Bitmap.Rotate(rotation);
        if (bitmap.Width > MaxWidth || bitmap.Height > MaxHeight || bitmap.PackedRows.Length > MaxBytes)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Text.Raster.TooLarge", t.Id));
            return null;
        }

        // ^GF ignores rotation and, with ^FT, anchors the graphic's bottom-left corner instead of the text baseline. The picture is therefore
        // always placed with ^FO: for a typeset text its top-left corner is worked out from the baseline and the turn of the field.
        int x = t.X, y = t.Y;
        if (t.OriginKind == FieldOriginKind.Typeset)
        {
            var (dx, dy) = TypesetCorner(rotation, m.WidthDots, m.HeightDots, m.BaselineDots);
            x += dx;
            y += dy;
            if (x < 0 || y < 0)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Element.Position.Range", t.Id, "0", "32000"));
                return null;
            }
        }
        if (x < 0 || y < 0 || x > 32000 || y > 32000)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Element.Position.Range", t.Id, "0", "32000"));
            return null;
        }
        if (x + bitmap.Width > doc.WidthDots || y + bitmap.Height > doc.HeightDots)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Element.OutsideLabel", t.Id));

        int total = bitmap.PackedRows.Length;
        return string.Create(CultureInfo.InvariantCulture, $"^FO{x},{y}") + (t.ReverseField ? "^FR" : string.Empty)
            + string.Create(CultureInfo.InvariantCulture, $"^GFA,{total},{total},{bitmap.BytesPerRow},{bitmap.ToHex()}^FS");
    }

    /// <summary>
    /// Offset from a <c>^FT</c> origin (the left end of the first baseline) to the top-left corner of the turned picture. The picture, upright,
    /// has its top-left corner <paramref name="baseline"/> above the origin; turning it clockwise around the origin moves its corners.
    /// </summary>
    internal static (int Dx, int Dy) TypesetCorner(FieldRotation rotation, int width, int height, int baseline)
    {
        // Upright corners relative to the origin (y grows downwards).
        var corners = new (int X, int Y)[] { (0, -baseline), (width, -baseline), (0, height - baseline), (width, height - baseline) };
        int minX = int.MaxValue, minY = int.MaxValue;
        foreach (var (cx, cy) in corners)
        {
            // Clockwise quarter turns in a y-down plane: 90 -> (-y, x), 180 -> (-x, -y), 270 -> (y, -x).
            var (rx, ry) = rotation switch
            {
                FieldRotation.Rotated90 => (-cy, cx),
                FieldRotation.Inverted180 => (-cx, -cy),
                FieldRotation.Rotated270 => (cy, -cx),
                _ => (cx, cy),
            };
            minX = Math.Min(minX, rx);
            minY = Math.Min(minY, ry);
        }
        return (minX, minY);
    }
}
