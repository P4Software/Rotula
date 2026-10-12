using System.Text;
using Rotula.Core.Elements;
using Rotula.Core.Gs1;
using Rotula.Core.Model;
using Rotula.Files;
using Rotula.Rendering;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;
using Rotula.Tests.Showcase;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.Multi;

namespace Rotula.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that only runs when <c>ROTULA_RENDER_SHOWCASE=1</c>. The showcase regenerates files in the source tree, so it
/// must never run as part of the normal test suite.
/// </summary>
internal sealed class ShowcaseFactAttribute : FactAttribute
{
    public ShowcaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ROTULA_RENDER_SHOWCASE") != "1")
            Skip = "Regenerates docs/showcase and docs/images/showcase. Set ROTULA_RENDER_SHOWCASE=1 to run it.";
    }
}

/// <summary>
/// Renders the README gallery. The files in <c>docs/showcase</c> (<c>*.rotula</c>, plus the <c>*.csv</c> of the labels that use data merge) are the
/// source: each one is loaded exactly as a user would open it, rendered with Rotula's own PNG exporter, and the presentation picture is written to
/// <c>docs/images/showcase/*.png</c>. Every barcode the label prints is found in the rendered picture and read back with ZXing, so a gallery image
/// can never show a symbol a scanner cannot read. Because the barcodes are discovered from the rendered label, a label edited in Rotula and
/// saved over its file is checked just the same.
/// <para>To render: <c>$env:ROTULA_RENDER_SHOWCASE=1; dotnet test tests/Rotula.Tests --filter ShowcaseRenderTests</c> (PowerShell).</para>
/// <para>The files were first generated from the C# drawing code in <c>Showcase/</c>. Setting <c>ROTULA_REBUILD_SHOWCASE_SOURCES=1</c> as well
/// writes them again from that code, which overwrites any edit made in Rotula.</para>
/// </summary>
public class ShowcaseRenderTests
{
    /// <summary>Width of the label inside a gallery picture, in pixels; the picture is this plus the margin on both sides.</summary>
    private const int LabelPictureWidth = 920;

    /// <summary>Space around the label in the gallery picture.</summary>
    private const int Margin = 40;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find Rotula.sln above " + AppContext.BaseDirectory);
    }

    [ShowcaseFact]
    public void RenderShowcase()
    {
        string root = RepoRoot();
        string sources = Path.Combine(root, "docs", "showcase");
        string pictures = Path.Combine(root, "docs", "images", "showcase");
        Directory.CreateDirectory(sources);
        Directory.CreateDirectory(pictures);
        bool rebuild = Environment.GetEnvironmentVariable("ROTULA_REBUILD_SHOWCASE_SOURCES") == "1";

        // Optional filter, handy while designing a single label: ROTULA_SHOWCASE_ONLY=wine
        string? only = Environment.GetEnvironmentVariable("ROTULA_SHOWCASE_ONLY");
        var failures = new List<string>();

        foreach (var label in ShowcaseLabels.All())
        {
            if (only is { Length: > 0 } && !label.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;

            string file = Path.Combine(sources, label.Name + ".rotula");
            string csv = Path.Combine(sources, label.Name + ".csv");
            if (rebuild)
            {
                RotulaFileSerializer.Save(label.Document, file);
                // A label shown with several data records also gets them as a CSV file, so the example can be repeated with Data merge.
                if (label.Variants.Count > 1 && label.Variants.All(v => v.Record is not null))
                    File.WriteAllText(csv, ToCsv(label.Variants.Select(v => v.Record!).ToList()), new UTF8Encoding(false));
            }
            Assert.True(File.Exists(file), $"{file} is missing. Run once with ROTULA_REBUILD_SHOWCASE_SOURCES=1 to create it.");
            var document = RotulaFileSerializer.Load(file);

            // The records come from the CSV file, in order, one per variant.
            var records = File.Exists(csv) ? ReadRecords(csv) : null;

            var sheet = new List<(SKBitmap Picture, string Caption)>();
            for (int i = 0; i < label.Variants.Count; i++)
            {
                var variant = label.Variants[i];
                var record = records is null ? null : records[i];
                var render = LabelDocumentAdapter.ToRenderLabel(document, record);
                using var result = LabelRenderer.RenderBitmap(render);
                Assert.Empty(result.Issues);

                // Also exercise the real exporter once, so a regression in SavePng shows up here.
                string raw = Path.Combine(Path.GetTempPath(), "rotula-showcase-" + label.Name + variant.Suffix + ".png");
                Assert.Empty(LabelExporter.SavePng(render, raw));
                File.Delete(raw);

                failures.AddRange(CheckScans(label.Name + variant.Suffix, result.Bitmap, PlanScans(render)));

                if (label.Layout != SheetLayout.Separate) sheet.Add((result.Bitmap.Copy(), variant.Caption));
                else WritePicture(Path.Combine(pictures, label.Name + variant.Suffix + ".png"), result.Bitmap);
            }

            if (label.Layout != SheetLayout.Separate)
            {
                WriteSheet(Path.Combine(pictures, label.Name + ".png"), sheet, label.Layout == SheetLayout.Row);
                foreach (var item in sheet) item.Picture.Dispose();
            }
        }

        Assert.True(failures.Count == 0, "Barcodes that could not be read back:\n" + string.Join("\n", failures));
    }

    /// <summary>Writes records as CSV (header row first, values quoted when they hold a comma, a quote or a line break).</summary>
    private static string ToCsv(IReadOnlyList<IReadOnlyDictionary<string, string>> records)
    {
        var columns = records[0].Keys.ToList();
        static string Cell(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder();
        sb.Append(string.Join(",", columns.Select(Cell))).Append("\r\n");
        foreach (var r in records) sb.Append(string.Join(",", columns.Select(c => Cell(r.TryGetValue(c, out var v) ? v : string.Empty)))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Reads the records of a showcase CSV with Rotula's own reader, as a user's Data merge would.</summary>
    private static List<Dictionary<string, string>> ReadRecords(string csvPath)
    {
        var table = Rotula.Core.Merge.CsvReader.ReadFile(csvPath);
        return table.Rows.Select(row => table.Columns.Select((name, i) => (name, value: i < row.Length ? row[i] : string.Empty))
            .ToDictionary(c => c.name, c => c.value)).ToList();
    }

    // ------------------------------------------------------------------------------------------ scanning

    /// <summary>A barcode that must be readable in a rendered picture: the symbology, the text a scanner returns and where the symbol is.</summary>
    private sealed record ExpectedScan(BarcodeFormat Format, string Text, int X, int Y, int Width, int Height, bool Gs1);

    /// <summary>
    /// Finds every barcode the label prints (conditions already applied) and works out what a decoder must read from it. Symbologies ZXing cannot
    /// read (POSTNET, MaxiCode and others) are left out.
    /// </summary>
    private static List<ExpectedScan> PlanScans(RenderLabel label)
    {
        var plan = new List<ExpectedScan>();
        foreach (var rb in label.Elements.OfType<RenderBarcode>())
        {
            BarcodeFormat? format = rb.Symbology switch
            {
                BarcodeSymbology.Code128 => BarcodeFormat.CODE_128,
                BarcodeSymbology.Ean13 => BarcodeFormat.EAN_13,
                BarcodeSymbology.QrCode => BarcodeFormat.QR_CODE,
                BarcodeSymbology.DataMatrix => BarcodeFormat.DATA_MATRIX,
                BarcodeSymbology.Pdf417 => BarcodeFormat.PDF_417,
                _ => null,
            };
            if (format is null) continue;

            // Measure the symbol with the same encoder the renderer used.
            var probe = new BarcodeElement
            {
                Symbology = rb.Symbology, Data = rb.Data, ModuleWidth = rb.ModuleDots, ModuleSize = rb.ModuleDots, Height = rb.HeightDots,
                QrErrorCorrection = rb.QrErrorCorrection, Pdf417SecurityLevel = rb.Pdf417SecurityLevel, CheckDigit = rb.CheckDigit,
                Code128Mode = rb.Gs1 ? Code128Mode.Ean : Code128Mode.Automatic, DataMatrixGs1 = rb.Gs1 && rb.Symbology == BarcodeSymbology.DataMatrix,
            };
            (int w, int h) = LabelBuilder.BarsSize(probe);
            bool linear = rb.Symbology is BarcodeSymbology.Code128 or BarcodeSymbology.Ean13;
            int hri = linear && rb.ShowText ? 70 : 0;
            bool turned = rb.Rotation is RenderRotation.Rotate90 or RenderRotation.Rotate270;
            int rw = turned ? h + hri : w, rh = turned ? w : h + hri;
            const int quiet = 24;

            // EAN-13 data may be given without its check digit; a scanner always reports all 13 digits.
            string text = rb.Symbology == BarcodeSymbology.Ean13 && rb.Data.Length == 12 ? rb.Data + Gs1Keys.CheckDigit(rb.Data) : rb.Data;
            plan.Add(new ExpectedScan(format.Value, text, (int)rb.X - quiet, (int)rb.Y - quiet, rw + 2 * quiet, rh + 2 * quiet, rb.Gs1));
        }
        return plan;
    }

    /// <summary>Removes what differs between how a GS1 scanner reports data and how the label is written: parentheses, FNC1 and the identifier.</summary>
    private static string Normalize(string text) =>
        new string(text.Where(c => c is not ('(' or ')') && c >= ' ').ToArray()).Replace("]C1", string.Empty).Replace("]d2", string.Empty);

    private static IEnumerable<string> CheckScans(string name, SKBitmap bitmap, IReadOnlyList<ExpectedScan> expected)
    {
        foreach (var e in expected)
        {
            // Crop to the symbol's area: a reader given the whole label may lock on a neighbouring symbol of the same family.
            int x0 = Math.Max(0, e.X), y0 = Math.Max(0, e.Y);
            int w = Math.Min(bitmap.Width - x0, e.Width), h = Math.Min(bitmap.Height - y0, e.Height);
            // ZXing wants luminance bytes; the picture is black on white so a plain grey conversion is exact.
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = bitmap.GetPixel(x0 + x, y0 + y);
                    gray[y * w + x] = (byte)((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);
                }

            var hints = new Dictionary<DecodeHintType, object>
            {
                [DecodeHintType.POSSIBLE_FORMATS] = new[] { e.Format },
                [DecodeHintType.TRY_HARDER] = true,
                [DecodeHintType.ASSUME_GS1] = true,
            };
            var source = new RGBLuminanceSource(gray, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
            Result? result = null;
            try { result = new MultiFormatReader().decode(new BinaryBitmap(new HybridBinarizer(source)), hints); }
            catch (ReaderException) { /* nothing readable: reported below */ }

            // A GS1 label must read as GS1: the reader reports the "]C1" symbology identifier only when the symbol starts with FNC1, which a plain
            // Code 128 that merely contains parentheses would not.
            string? identifier = result?.ResultMetadata is { } meta && meta.TryGetValue(ResultMetadataType.SYMBOLOGY_IDENTIFIER, out var id) ? id?.ToString() : null;
            if (e.Gs1 && e.Format == BarcodeFormat.CODE_128 && result is not null && identifier != "]C1")
            {
                yield return $"{name}: \"{e.Text}\" was not read as GS1-128 (identifier {identifier ?? "none"}).";
                continue;
            }

            if (result is null) yield return $"{name}: {e.Format} \"{e.Text}\" could not be read at all.";
            else if (Normalize(result.Text) != Normalize(e.Text)) yield return $"{name}: {e.Format} read \"{result.Text}\" instead of \"{e.Text}\".";
        }
    }

    // ------------------------------------------------------------------------------------------ presentation

    private static readonly SKColor Backdrop = new(0xE8, 0xEB, 0xF0);

    /// <summary>Draws the label on a soft grey backdrop with a shadow, scaled to the gallery width, and writes it as PNG.</summary>
    private static void WritePicture(string path, SKBitmap label)
    {
        using var picture = Compose(new[] { (label, string.Empty) }, row: false, LabelPictureWidth);
        Save(path, picture);
    }

    /// <summary>Several records in one picture (used for the merge and the conditions examples): stacked, or side by side.</summary>
    private static void WriteSheet(string path, IReadOnlyList<(SKBitmap Picture, string Caption)> labels, bool row)
    {
        using var picture = Compose(labels, row, row ? 640 : 780);
        Save(path, picture);
    }

    private static void Save(string path, SKBitmap picture)
    {
        using var image = SKImage.FromBitmap(picture);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>
    /// Lays the labels out on the backdrop, each scaled to <paramref name="labelWidth"/> pixels wide, in a row or a column, with an optional
    /// caption under each one.
    /// </summary>
    private static SKBitmap Compose(IReadOnlyList<(SKBitmap Picture, string Caption)> labels, bool row, int labelWidth)
    {
        const int gap = Margin, captionBand = 70;
        bool anyCaption = labels.Any(l => l.Caption.Length > 0);
        int band = anyCaption ? captionBand : 0;
        var heights = labels.Select(l => (int)Math.Round(labelWidth * (double)l.Picture.Height / l.Picture.Width)).ToArray();

        int totalW = row ? Margin * 2 + labels.Count * labelWidth + (labels.Count - 1) * gap : Margin * 2 + labelWidth;
        int totalH = row ? Margin * 2 + heights.Max() + band : Margin * 2 + heights.Sum() + band * labels.Count + (labels.Count - 1) * gap;
        var surface = new SKBitmap(totalW, totalH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(surface);
        canvas.Clear(Backdrop);

        using var captionFace = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var captionFont = new SKFont(captionFace, 27) { Subpixel = true };
        using var captionPaint = new SKPaint { Color = new SKColor(0x3A, 0x42, 0x4F), IsAntialias = true };

        float x = Margin, y = Margin;
        for (int i = 0; i < labels.Count; i++)
        {
            var target = new SKRect(x, y, x + labelWidth, y + heights[i]);

            // Soft drop shadow so the white label lifts off the backdrop.
            using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 70), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(10, 10) })
                canvas.DrawRect(new SKRect(target.Left + 2, target.Top + 8, target.Right + 2, target.Bottom + 8), shadow);

            // Mitchell resampling keeps bar edges and thin rules crisp when the label is scaled down to the gallery width.
            using var image = SKImage.FromBitmap(labels[i].Picture);
            canvas.DrawImage(image, target, new SKSamplingOptions(SKCubicResampler.Mitchell));

            if (labels[i].Caption.Length > 0)
            {
                canvas.DrawText(labels[i].Caption, target.MidX, target.Bottom + 48, SKTextAlign.Center, captionFont, captionPaint);
            }

            if (row) x += labelWidth + gap;
            else y += heights[i] + band + gap;
        }
        return surface;
    }
}
