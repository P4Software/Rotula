using Rotula.Core.Sheets;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Rendering.Export;

/// <summary>How a run of labels is put on sheets.</summary>
public sealed class SheetExportOptions
{
    /// <summary>1-based position on the first sheet where the first label goes (earlier positions are already used).</summary>
    public int StartPosition { get; init; } = 1;

    /// <summary>The fill order of the positions.</summary>
    public SheetOrder Order { get; init; } = SheetOrder.AcrossThenDown;

    /// <summary>Crop marks or outlines drawn on every sheet.</summary>
    public SheetGuides Guides { get; init; } = SheetGuides.None;
}

/// <summary>What a sheet export wrote.</summary>
/// <param name="Pages">Number of sheets (PDF pages).</param>
/// <param name="Labels">Number of labels placed.</param>
/// <param name="Issues">Render notes, each once (for example a barcode that could not be drawn).</param>
public sealed record SheetExportResult(int Pages, long Labels, IReadOnlyList<RenderIssue> Issues);

/// <summary>
/// Writes labels onto sheets for an office (laser or inkjet) printer as a multi-page PDF: one page per sheet, at the sheet's real paper size,
/// every label at its own physical size (dots divided by its resolution), centred in its position and clipped to it so it never runs into a
/// neighbour. Text, boxes, lines and barcode modules stay vectors, as in <see cref="LabelExporter.SavePdf(RenderLabel, Stream)"/>. Print the
/// PDF at "actual size" (100 %, no "fit to page") or the labels will not line up.
/// </summary>
public static class SheetExporter
{
    private const double PointsPerMm = 72.0 / 25.4;

    /// <summary>
    /// Resolution of the parts the PDF cannot keep as vectors (a reversed field). The PDF writer also rounds the page size to whole pixels at this
    /// resolution, so it must be fine: at 600 dpi US paper is exact and A4 is within 0.02 mm, and no label printer has more dots.
    /// </summary>
    private const float RasterDpi = 600;

    /// <summary>Length of a crop mark and its distance from the label edge.</summary>
    private const double CropMarkMm = 5, CropGapMm = 1;

    /// <summary>
    /// Writes the labels as a PDF. The labels are pulled lazily one sheet at a time; pass the same <see cref="RenderLabel"/> instance for copies
    /// to draw them without converting the design again. Nothing is written for an empty sequence (the result says 0 pages).
    /// </summary>
    /// <param name="layout">The sheet; it must be valid (<see cref="SheetLayout.Validate"/>).</param>
    /// <param name="labels">The labels in print order.</param>
    /// <param name="output">Where the PDF goes.</param>
    /// <param name="options">Start position, order and guides; null for the defaults.</param>
    /// <exception cref="ArgumentException">The layout has an error or the start position is not on the sheet.</exception>
    public static SheetExportResult SavePdf(SheetLayout layout, IEnumerable<RenderLabel> labels, Stream output, SheetExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new SheetExportOptions();
        if (!layout.IsValid) throw new ArgumentException("The sheet layout has errors.", nameof(layout));

        var issues = new List<RenderIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int pages = 0;
        long placed = 0;
        SKManagedWStream? stream = null;
        SKDocument? document = null;
        try
        {
            foreach (var page in SheetImposition.Paginate(layout, labels, options.StartPosition, options.Order))
            {
                if (document is null)
                {
                    // Created on the first sheet only, so an empty run writes no half-made file.
                    stream = new SKManagedWStream(output, false);
                    document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { RasterDpi = RasterDpi });
                }
                var canvas = document.BeginPage(Pt(layout.PageWidthMm), Pt(layout.PageHeightMm));
                canvas.Clear(SKColors.White);
                foreach (var placement in page.Placements)
                {
                    foreach (var issue in DrawLabel(canvas, placement.Slot, placement.Item))
                        if (seen.Add(issue.Message)) issues.Add(issue);
                    placed++;
                }
                DrawGuides(canvas, layout, page, options.Guides);
                document.EndPage();
                pages++;
            }
            document?.Close();
        }
        finally
        {
            document?.Dispose();
            stream?.Dispose();
        }
        return new SheetExportResult(pages, placed, issues);
    }

    /// <summary>Writes the labels as a PDF file through a temporary file, so a failed export never leaves a broken file behind.</summary>
    public static SheetExportResult SavePdf(SheetLayout layout, IEnumerable<RenderLabel> labels, string path, SheetExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full = Path.GetFullPath(path);
        string? folder = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string temp = full + ".tmp";
        try
        {
            SheetExportResult result;
            using (var file = File.Create(temp)) result = SavePdf(layout, labels, file, options);
            if (result.Pages > 0) File.Move(temp, full, overwrite: true);
            return result;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>The position of a slot and the size of a label in PDF points, as the exporter places it (exposed for tests and previews).</summary>
    /// <returns>The slot rectangle and the rectangle the label is drawn in (centred in the slot, at the label's physical size).</returns>
    public static (SKRect Slot, SKRect Label) Placement(SheetSlot slot, RenderLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var box = SKRect.Create(Pt(slot.XMm), Pt(slot.YMm), Pt(slot.WidthMm), Pt(slot.HeightMm));
        // One dot is 1/dpi inch; one point is 1/72 inch.
        float w = (float)(label.WidthDots * 72.0 / label.Dpi), h = (float)(label.HeightDots * 72.0 / label.Dpi);
        var drawn = SKRect.Create(box.MidX - w / 2, box.MidY - h / 2, w, h);
        return (box, drawn);
    }

    private static IReadOnlyList<RenderIssue> DrawLabel(SKCanvas canvas, SheetSlot slot, RenderLabel label)
    {
        var (box, drawn) = Placement(slot, label);
        canvas.Save();
        try
        {
            // A design larger than its position is cut at the position's edge rather than printed onto the next label.
            canvas.ClipRect(box);
            canvas.Translate(drawn.Left, drawn.Top);
            canvas.Scale((float)(72.0 / label.Dpi));
            return LabelRenderer.Draw(canvas, label);
        }
        finally
        {
            canvas.Restore();
        }
    }

    private static void DrawGuides(SKCanvas canvas, SheetLayout layout, SheetPage<RenderLabel> page, SheetGuides guides)
    {
        if (guides == SheetGuides.None) return;
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        if (guides == SheetGuides.Outlines)
        {
            paint.Color = new SKColor(0x99, 0x99, 0x99);
            paint.StrokeWidth = 0.5f;
            foreach (var p in page.Placements)
                canvas.DrawRect(SKRect.Create(Pt(p.Slot.XMm), Pt(p.Slot.YMm), Pt(p.Slot.WidthMm), Pt(p.Slot.HeightMm)), paint);
            return;
        }

        // Crop marks sit in the paper margins only, so they never print onto a label; a margin too small for a mark gets none.
        paint.Color = SKColors.Black;
        paint.StrokeWidth = 0.35f;
        var xs = new SortedSet<double>();
        var ys = new SortedSet<double>();
        for (int c = 0; c < layout.Columns; c++)
        {
            double left = layout.LeftMarginMm + c * layout.HorizontalPitchMm;
            xs.Add(Math.Round(left, 3));
            xs.Add(Math.Round(left + layout.LabelWidthMm, 3));
        }
        for (int r = 0; r < layout.Rows; r++)
        {
            double top = layout.TopMarginMm + r * layout.VerticalPitchMm;
            ys.Add(Math.Round(top, 3));
            ys.Add(Math.Round(top + layout.LabelHeightMm, 3));
        }
        double top0 = layout.TopMarginMm, bottom = layout.GridBottomMm, left0 = layout.LeftMarginMm, right = layout.GridRightMm;
        foreach (double x in xs)
        {
            Mark(canvas, paint, x, top0 - CropGapMm, x, top0 - CropGapMm - Math.Min(CropMarkMm, top0 - CropGapMm), top0 - CropGapMm);
            Mark(canvas, paint, x, bottom + CropGapMm, x, bottom + CropGapMm + Math.Min(CropMarkMm, layout.PageHeightMm - bottom - CropGapMm), layout.PageHeightMm - bottom - CropGapMm);
        }
        foreach (double y in ys)
        {
            Mark(canvas, paint, left0 - CropGapMm, y, left0 - CropGapMm - Math.Min(CropMarkMm, left0 - CropGapMm), y, left0 - CropGapMm);
            Mark(canvas, paint, right + CropGapMm, y, right + CropGapMm + Math.Min(CropMarkMm, layout.PageWidthMm - right - CropGapMm), y, layout.PageWidthMm - right - CropGapMm);
        }
    }

    private static void Mark(SKCanvas canvas, SKPaint paint, double x1, double y1, double x2, double y2, double room)
    {
        // Less than a millimetre and a half of room is not a mark anyone can use.
        if (room < 1.5) return;
        canvas.DrawLine(Pt(x1), Pt(y1), Pt(x2), Pt(y2), paint);
    }

    private static float Pt(double mm) => (float)(mm * PointsPerMm);
}
