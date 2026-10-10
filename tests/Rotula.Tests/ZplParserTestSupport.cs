using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;

namespace Rotula.Tests;

/// <summary>Helpers for the ZplParser* tests: fixture access and a structural comparer for label documents.</summary>
internal static class ZplParserTestSupport
{
    /// <summary>The tests/fixtures directory, found by walking up to the solution file.</summary>
    public static string FixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("Repository root not found from " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "tests", "fixtures");
    }

    /// <summary>Path of tests/fixtures/import/NAME.zpl; the file must exist (import fixtures are never auto-created).</summary>
    public static string ImportFixture(string name)
    {
        string path = Path.Combine(FixturesDir(), "import", name + ".zpl");
        Assert.True(File.Exists(path), "Missing import fixture " + path);
        return path;
    }

    /// <summary>
    /// Describes every difference between two documents in the terms ZPL can express: sizes in dots, effective fonts and
    /// rotations (an inherited value equals the same explicit value), and for each symbology only the options it prints.
    /// Ids, names and values that ZPL cannot carry (DPI, right/bottom margins, variables) are ignored.
    /// </summary>
    public static List<string> Diff(LabelDocument a, LabelDocument b)
    {
        var d = new List<string>();
        void Eq<T>(string what, T x, T y)
        {
            if (!EqualityComparer<T>.Default.Equals(x, y)) d.Add($"{what}: expected {x}, got {y}");
        }

        Eq("Width", a.WidthDots, b.WidthDots);
        Eq("Height", a.HeightDots, b.HeightDots);
        Eq("Orientation", a.Orientation, b.Orientation);
        Eq("Margin.Left", a.Margins.Left.ToDots(a.Dpi), b.Margins.Left.ToDots(b.Dpi));
        Eq("Margin.Top", a.Margins.Top.ToDots(a.Dpi), b.Margins.Top.ToDots(b.Dpi));
        Eq("MediaType", a.MediaType, b.MediaType);
        Eq("MediaTracking", a.MediaTracking, b.MediaTracking);
        Eq("DefaultRotation", a.DefaultRotation, b.DefaultRotation);
        Eq("DefaultFont", Font(a.DefaultFont), Font(b.DefaultFont));
        Eq("Encoding", a.Encoding, b.Encoding);
        Eq("Print.Quantity", a.Print.Quantity, b.Print.Quantity);
        Eq("Print.PauseEvery", a.Print.PauseEvery, b.Print.PauseEvery);
        Eq("Print.Replicates", a.Print.Replicates, b.Print.Replicates);
        Eq("Print.OverridePause", a.Print.OverridePause, b.Print.OverridePause);
        Eq("LabelShift", a.LabelShift, b.LabelShift);
        Eq("LabelTop", a.LabelTop, b.LabelTop);
        Eq("PrintMode", (a.PrintMode, a.PrePeel), (b.PrintMode, b.PrePeel));
        Eq("MirrorImage", a.MirrorImage, b.MirrorImage);
        Eq("PrintRate", Rate(a.PrintRate), Rate(b.PrintRate));
        Eq("MediaDarkness", a.MediaDarkness, b.MediaDarkness);
        Eq("FontAliases", string.Join(";", a.FontAliases.Select(f => f.Letter + "=" + f.Path)), string.Join(";", b.FontAliases.Select(f => f.Letter + "=" + f.Path)));
        Eq("StoredGraphics", string.Join(";", a.StoredGraphics.Select(g => g.Path + "/" + g.BytesPerRow + "/" + Convert.ToHexString(g.Data))),
            string.Join(";", b.StoredGraphics.Select(g => g.Path + "/" + g.BytesPerRow + "/" + Convert.ToHexString(g.Data))));
        Eq("Element count", a.Elements.Count, b.Elements.Count);
        if (a.Elements.Count != b.Elements.Count) return d;

        for (int i = 0; i < a.Elements.Count; i++)
        {
            var x = a.Elements[i];
            var y = b.Elements[i];
            string at = $"[{i}] {x.GetType().Name}";
            // A data-bound field prints as plain text, so it is compared with the text it became.
            if (x.GetType() != y.GetType() && !(x is TextElement && y is TextElement))
            {
                d.Add($"{at}: type became {y.GetType().Name}");
                continue;
            }
            Eq(at + ".X", x.X, y.X);
            Eq(at + ".Y", x.Y, y.Y);
            Eq(at + ".OriginKind", x.OriginKind, y.OriginKind);
            Eq(at + ".Reverse", x.ReverseField, y.ReverseField);
            switch (x)
            {
                case TextElement tx:
                    var ty = (TextElement)y;
                    Eq(at + ".Text", tx.Text.Replace("\r\n", "\n"), ty.Text);
                    Eq(at + ".Font", EffectiveFont(a, tx), EffectiveFont(b, ty));
                    Eq(at + ".Rotation", tx.Rotation ?? a.DefaultRotation, ty.Rotation ?? b.DefaultRotation);
                    Eq(at + ".Block", Block(tx.Block), Block(ty.Block));
                    Eq<object?>(at + ".TextBox", tx.TextBox is null ? null : (tx.TextBox.WidthDots, tx.TextBox.HeightDots), ty.TextBox is null ? null : (ty.TextBox.WidthDots, ty.TextBox.HeightDots));
                    Eq(at + ".Direction", (tx.Direction, tx.InterCharacterGap), (ty.Direction, ty.InterCharacterGap));
                    DiffFieldOptions(at, tx, ty, d);
                    break;
                case BarcodeElement bx:
                    DiffBarcode(at, a, b, bx, (BarcodeElement)y, d);
                    DiffFieldOptions(at, bx, (BarcodeElement)y, d);
                    break;
                case DiagonalLineElement da:
                    var db2 = (DiagonalLineElement)y;
                    Eq(at + ".Shape", (da.WidthDots, da.HeightDots, da.ThicknessDots, da.Color, da.Lean), (db2.WidthDots, db2.HeightDots, db2.ThicknessDots, db2.Color, db2.Lean));
                    break;
                case EllipseElement ea:
                    var eb = (EllipseElement)y;
                    Eq(at + ".Shape", (ea.WidthDots, ea.HeightDots, ea.ThicknessDots, ea.Color), (eb.WidthDots, eb.HeightDots, eb.ThicknessDots, eb.Color));
                    break;
                case SymbolElement sa:
                    var sb2 = (SymbolElement)y;
                    Eq(at + ".Symbol", (sa.Symbol, sa.HeightDots, sa.WidthDots, sa.Rotation ?? a.DefaultRotation), (sb2.Symbol, sb2.HeightDots, sb2.WidthDots, sb2.Rotation ?? b.DefaultRotation));
                    break;
                case BoxElement boxA:
                    var boxB = (BoxElement)y;
                    Eq(at + ".Shape", (boxA.WidthDots, boxA.HeightDots, boxA.ThicknessDots, boxA.Color, boxA.CornerRounding),
                        (boxB.WidthDots, boxB.HeightDots, boxB.ThicknessDots, boxB.Color, boxB.CornerRounding));
                    break;
                case LineElement la:
                    var lb = (LineElement)y;
                    Eq(at + ".Shape", (la.Direction, la.LengthDots, la.ThicknessDots, la.Color), (lb.Direction, lb.LengthDots, lb.ThicknessDots, lb.Color));
                    break;
                case CircleElement ca:
                    var cb = (CircleElement)y;
                    Eq(at + ".Shape", (ca.DiameterDots, ca.ThicknessDots, ca.Color), (cb.DiameterDots, cb.ThicknessDots, cb.Color));
                    break;
                case ImageElement ia:
                    // Images are compared by the dots that print, not by the source pixels or settings that produced them.
                    var ma = ImageProcessor.ToMonochrome(ia);
                    var mb = ImageProcessor.ToMonochrome((ImageElement)y);
                    // ^GF carries whole bytes per row, so a width that is not a multiple of 8 comes back padded with white dots.
                    Eq(at + ".Size", (ma.BytesPerRow, ma.Height), (mb.BytesPerRow, mb.Height));
                    Eq(at + ".Dots", ma.ToHex(), mb.ToHex());
                    break;
                case RawZplElement ra:
                    Eq(at + ".Zpl", ra.Zpl, ((RawZplElement)y).Zpl);
                    break;
                case RecallPlaceholderElement pa:
                    var pb = (RecallPlaceholderElement)y;
                    Eq(at + ".Zpl", (pa.Zpl, pa.EmitAsField), (pb.Zpl, pb.EmitAsField));
                    break;
            }
        }
        return d;
    }

    /// <summary>Fails with a readable list of differences.</summary>
    public static void AssertSame(LabelDocument expected, LabelDocument actual)
    {
        var diff = Diff(expected, actual);
        Assert.True(diff.Count == 0, "Documents differ:\n" + string.Join("\n", diff));
    }

    private static (int?, int?, int?)? Rate(PrintRateOptions? r) => r is null || r.IsEmpty ? null : (r.Speed, r.SlewSpeed, r.BackfeedSpeed);

    private static void DiffFieldOptions(string at, FieldDataElement x, FieldDataElement y, List<string> d)
    {
        void Eq<T>(string what, T p, T q)
        {
            if (!EqualityComparer<T>.Default.Equals(p, q)) d.Add($"{at}.{what}: expected {p}, got {q}");
        }
        Eq("FieldNumber", (x.FieldNumber, x.FieldPrompt, x.EmbedCharacter), (y.FieldNumber, y.FieldPrompt, y.EmbedCharacter));
        Eq<object?>("Clock", x.Clock is null ? null : (x.Clock.Primary, x.Clock.Secondary, x.Clock.Third), y.Clock is null ? null : (y.Clock.Primary, y.Clock.Secondary, y.Clock.Third));
        Eq<object?>("Serial", x.Serial is null ? null : (x.Serial.Kind, x.Serial.Increment, x.Serial.LeadingZeros, x.Serial.Mask, x.Serial.IncrementText),
            y.Serial is null ? null : (y.Serial.Kind, y.Serial.Increment, y.Serial.LeadingZeros, y.Serial.Mask, y.Serial.IncrementText));
    }

    private static (char, int, int)? Font(FontSpec? f) => f is null ? null : (f.FontId, f.Height, f.Width);

    private static (char?, int?, int?) EffectiveFont(LabelDocument doc, TextElement t) =>
        (t.FontId ?? doc.DefaultFont?.FontId, t.FontHeight ?? doc.DefaultFont?.Height, t.FontWidth ?? doc.DefaultFont?.Width);

    private static (int, int, int, TextAlignment, int)? Block(TextBlockOptions? b) =>
        b is null ? null : (b.WidthDots, b.MaxLines, b.LineSpacing, b.Alignment, b.HangingIndent);

    private static void DiffBarcode(string at, LabelDocument da, LabelDocument db, BarcodeElement x, BarcodeElement y, List<string> d)
    {
        void Eq<T>(string what, T p, T q)
        {
            if (!EqualityComparer<T>.Default.Equals(p, q)) d.Add($"{at}.{what}: expected {p}, got {q}");
        }

        Eq("Symbology", x.Symbology, y.Symbology);
        if (x.Symbology != y.Symbology) return;
        // EAN-13 / UPC-A lose a typed check digit on output because the printer recomputes it.
        Eq("Data", BarcodeValidator.NormalizeForPrinter(x.Symbology, x.Data), BarcodeValidator.NormalizeForPrinter(y.Symbology, y.Data));
        Eq("Rotation", x.Rotation ?? da.DefaultRotation, y.Rotation ?? db.DefaultRotation);
        bool linear = x.Symbology is BarcodeSymbology.Code128 or BarcodeSymbology.Code39 or BarcodeSymbology.Code93
            or BarcodeSymbology.Ean13 or BarcodeSymbology.UpcA or BarcodeSymbology.Interleaved2Of5;
        if (linear)
        {
            Eq("Linear", (x.ModuleWidth, x.WideBarRatio, x.Height, x.HumanReadable, x.HumanReadablePosition),
                (y.ModuleWidth, y.WideBarRatio, y.Height, y.HumanReadable, y.HumanReadablePosition));
        }
        switch (x.Symbology)
        {
            case BarcodeSymbology.Code128: Eq("Mode", x.Code128Mode, y.Code128Mode); break;
            case BarcodeSymbology.Code39 or BarcodeSymbology.Code93 or BarcodeSymbology.UpcA or BarcodeSymbology.Interleaved2Of5:
                Eq("CheckDigit", x.CheckDigit, y.CheckDigit); break;
            case BarcodeSymbology.QrCode:
                Eq("Qr", (x.QrModel, x.ModuleSize, x.QrErrorCorrection), (y.QrModel, y.ModuleSize, y.QrErrorCorrection)); break;
            case BarcodeSymbology.DataMatrix:
                Eq("Dm", (x.ModuleSize, x.Columns, x.Rows), (y.ModuleSize, y.Columns, y.Rows)); break;
            case BarcodeSymbology.Pdf417:
                Eq("Pdf", (x.ModuleWidth, x.Height, x.Pdf417SecurityLevel, x.Columns, x.Rows, x.Pdf417Truncate),
                    (y.ModuleWidth, y.Height, y.Pdf417SecurityLevel, y.Columns, y.Rows, y.Pdf417Truncate)); break;
        }
    }
}
