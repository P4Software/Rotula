using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Text;
using Rotula.Core.Model;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.App.Editing;

/// <summary>An axis-aligned rectangle in label dots.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    /// <summary>Right edge.</summary>
    public double Right => X + Width;
    /// <summary>Bottom edge.</summary>
    public double Bottom => Y + Height;
    /// <summary>Horizontal centre.</summary>
    public double CenterX => X + Width / 2;
    /// <summary>Vertical centre.</summary>
    public double CenterY => Y + Height / 2;

    /// <summary>True when the point lies inside (edges included).</summary>
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    /// <summary>True when the rectangles overlap.</summary>
    public bool Intersects(RectD o) => X <= o.Right && Right >= o.X && Y <= o.Bottom && Bottom >= o.Y;

    /// <summary>The smallest rectangle covering both.</summary>
    public RectD Union(RectD o)
    {
        double x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new RectD(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }

    /// <summary>Bounding box of a set of rectangles; an empty rectangle when the set is empty.</summary>
    public static RectD UnionAll(IEnumerable<RectD> rects)
    {
        bool any = false;
        RectD acc = default;
        foreach (var r in rects)
        {
            acc = any ? acc.Union(r) : r;
            any = true;
        }
        return acc;
    }
}

/// <summary>
/// Works out where each element sits on the label. Rotula.Rendering does not expose element measurements, so:
/// boxes, lines, circles and images are computed from the model (exact), while text and barcodes are measured by
/// drawing the element alone with the real renderer and finding the ink (using the shown record's values when a data record is previewed). Results are cached until the element's
/// size-relevant content changes (moving an element never re-measures it).
/// </summary>
public sealed class ElementBoundsCalculator
{
    /// <summary>Empty space around the probe drawing so ^FT elements (which extend left and up) are not clipped.</summary>
    private const int ProbePad = 400;
    private const int FallbackSize = 24;

    // The probe canvas is never larger than this in either direction nor in total: 4400 x 3500 dots (600 dpi, 4 x 3 in label) used to be
    // allocated on every mouse move of a resize, about 60 MB of native memory each time, and the allocation eventually failed.
    private const int MaxProbeSide = 6000;
    private const long MaxProbePixels = 12_000_000;

    private readonly Dictionary<string, (int W, int H, int OffX, int OffY)> _lastGood = new();
    private static int _failureReported;

    /// <summary>
    /// Raised once per process, the first time measuring an element by drawing it fails (for example when the bitmap cannot be allocated).
    /// Later failures are silent so a drag does not write a log line per frame; the element then keeps its last known size.
    /// </summary>
    public static event Action<Exception>? ProbeFailed;

    /// <summary>How many probes failed in this calculator (exposed for tests).</summary>
    public int ProbeFailureCount { get; private set; }

    /// <summary>The size of the probe canvas for an element: what it needs, not a multiple of the label.</summary>
    public static (int Width, int Height) ProbeSize(LabelElement e, LabelDocument doc)
    {
        int w = Math.Max(1000, doc.WidthDots * 3 / 2) + 2 * ProbePad;
        int h = Math.Max(1000, doc.HeightDots * 3 / 2) + 2 * ProbePad;
        if (e is TextElement { Block: { } block } t)
        {
            // A wrapped text is as wide as its block and as tall as its lines; a rotated one swaps the two.
            double lineDots = t.Font is { IsWindows: true } f ? FontChoice.PointsToDots(f.PointSize, (int)doc.Dpi) * 1.5 : (t.FontHeight ?? doc.DefaultFont?.Height ?? 30) * 1.5;
            // The text fit can make the text larger (shrink mode up to its largest size) or taller (grow mode up to its cap): the probe must hold the worst case.
            int probeLines = block.MaxLines;
            if (block.Fit is { Mode: Rotula.Core.Text.TextFitMode.Grow } grow) probeLines = Math.Max(probeLines, grow.MaxLinesCap);
            if (block.Fit is { Mode: Rotula.Core.Text.TextFitMode.Shrink } shrink)
                lineDots = Math.Max(lineDots, FontChoice.PointsToDots(shrink.NormalizedRange().Max, (int)doc.Dpi) * 1.5);
            double along = Math.Clamp(block.WidthDots, 1, 9999);
            double across = Math.Clamp(probeLines, 1, 9999) * (lineDots + Math.Abs(block.LineSpacing));
            bool swap = EffectiveRotation(t, doc) is FieldRotation.Rotated90 or FieldRotation.Rotated270;
            w = (int)Math.Min(MaxProbeSide, (swap ? across : along) + 2 * ProbePad);
            h = (int)Math.Min(MaxProbeSide, (swap ? along : across) + 2 * ProbePad);
        }
        w = Math.Clamp(w, 2 * ProbePad + 1, MaxProbeSide);
        h = Math.Clamp(h, 2 * ProbePad + 1, MaxProbeSide);
        while ((long)w * h > MaxProbePixels && h > 2 * ProbePad + 1) h = Math.Max(2 * ProbePad + 1, h * 3 / 4);
        return (w, h);
    }

    private readonly Func<LabelDocument> _document;
    private readonly Dictionary<string, (int W, int H, int OffX, int OffY)> _cache = new();

    /// <summary>Creates a calculator that reads the current document through <paramref name="document"/>.</summary>
    public ElementBoundsCalculator(Func<LabelDocument> document) => _document = document;

    /// <summary>
    /// Supplies the values of the data-merge record currently shown on the canvas (or null for the label's sample values).
    /// Text and barcodes are measured with these values, so the selection frame matches what is drawn for the record.
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>?>? DataProvider { get; set; }

    // The key text of the last data dictionary seen; rebuilt only when the dictionary object changes (one per record), not on every call.
    private IReadOnlyDictionary<string, string>? _keyedData;
    private string _dataKey = string.Empty;

    private string DataKey(IReadOnlyDictionary<string, string>? data)
    {
        if (data is null) return string.Empty;
        if (!ReferenceEquals(data, _keyedData))
        {
            _keyedData = data;
            _dataKey = string.Join('', data.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        }
        return _dataKey;
    }

    /// <summary>How many times the renderer was used to measure an element; exposed so tests can verify caching.</summary>
    public int ProbeCount { get; private set; }

    /// <summary>The rotation an element is drawn with (its own, or the label default).</summary>
    public static FieldRotation EffectiveRotation(LabelElement e, LabelDocument doc) => e.Rotation ?? doc.DefaultRotation;

    /// <summary>Bounding box of the element in label dots (relative to the label home, like element coordinates).</summary>
    public RectD GetBounds(LabelElement e)
    {
        var doc = _document();
        switch (e)
        {
            case BoxElement b:
                return FromSize(e, doc, b.WidthDots, b.HeightDots, EffectiveRotation(e, doc));
            case LineElement l:
            {
                bool h = l.Direction == LineDirection.Horizontal;
                return FromSize(e, doc, h ? l.LengthDots : l.ThicknessDots, h ? l.ThicknessDots : l.LengthDots, EffectiveRotation(e, doc));
            }
            case CircleElement c:
                // The renderer ignores rotation for circles, so the box is always square.
                return FromSize(e, doc, c.DiameterDots, c.DiameterDots, FieldRotation.Normal);
            case EllipseElement el:
                // ^GE has no orientation, so rotation never changes the box.
                return FromSize(e, doc, el.WidthDots, el.HeightDots, FieldRotation.Normal);
            case DiagonalLineElement dl:
                return FromSize(e, doc, dl.WidthDots, dl.HeightDots, FieldRotation.Normal);
            case ImageElement i:
            {
                if (i.Source is null) return FromSize(e, doc, Math.Max(i.WidthDots, FallbackSize), Math.Max(i.HeightDots, FallbackSize), FieldRotation.Normal);
                var (w, h) = ImageProcessor.ResolveSize(i);
                // The image dots are rotated before they are sent, using only the element's own rotation.
                return FromSize(e, doc, w, h, i.Rotation ?? FieldRotation.Normal);
            }
            default:
                return MeasureInk(e, doc);
        }
    }

    /// <summary>
    /// The frame the canvas draws selection handles on. For almost every element it is <see cref="GetBounds"/>; for a text that wraps in a
    /// block it is the block's own box (its full width, the height of the lines), so the right-hand handle sits where the wrap width ends and
    /// dragging it changes that width. Only an upright text anchored at its top-left corner has such a box; others use their ink.
    /// </summary>
    public RectD GetFrame(LabelElement e)
    {
        var bounds = GetBounds(e);
        if (!HasTextBoxFrame(e)) return bounds;
        var block = ((TextElement)e).Block!;
        // The ink may start right of the origin (centred or right-aligned lines), but the box always starts at the origin.
        return new RectD(e.X, bounds.Y, Math.Max(1, block.WidthDots), bounds.Height);
    }

    /// <summary>True when <see cref="GetFrame"/> is a text block's box rather than the element's ink (the frame can then be resized by width).</summary>
    public bool HasTextBoxFrame(LabelElement e) =>
        e is TextElement { Block: not null } text && text.OriginKind == FieldOriginKind.Origin && EffectiveRotation(text, _document()) == FieldRotation.Normal;

    /// <summary>Bounds of every element, same order as the document.</summary>
    public RectD GetSelectionBounds(IEnumerable<LabelElement> elements) => RectD.UnionAll(elements.Select(GetBounds));

    private static RectD FromSize(LabelElement e, LabelDocument doc, int w, int h, FieldRotation rotation)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        bool swap = rotation is FieldRotation.Rotated90 or FieldRotation.Rotated270;
        if (e.OriginKind == FieldOriginKind.Origin)
            return new RectD(e.X, e.Y, swap ? h : w, swap ? w : h);

        // ^FT: the origin is the bottom-left corner of the unrotated box and the box rotates clockwise around it.
        var corners = new (double X, double Y)[] { (0, -h), (w, -h), (w, 0), (0, 0) };
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (cx, cy) in corners)
        {
            var (rx, ry) = rotation switch
            {
                FieldRotation.Rotated90 => (-cy, cx),
                FieldRotation.Inverted180 => (-cx, -cy),
                FieldRotation.Rotated270 => (cy, -cx),
                _ => (cx, cy),
            };
            minX = Math.Min(minX, rx); maxX = Math.Max(maxX, rx);
            minY = Math.Min(minY, ry); maxY = Math.Max(maxY, ry);
        }
        return new RectD(e.X + minX, e.Y + minY, maxX - minX, maxY - minY);
    }

    private RectD MeasureInk(LabelElement e, LabelDocument doc)
    {
        var data = DataProvider?.Invoke();
        // A record's values change the text width, so they are part of the cache key (empty when no record is shown).
        string key = DataKey(data) + "" + MeasureKey(e, doc);
        if (!_cache.TryGetValue(key, out var m))
        {
            bool failed = false;
            m = Probe(e, doc, data, ref failed);
            if (failed && _lastGood.TryGetValue(e.Id, out var known)) return new RectD(e.X + known.OffX, e.Y + known.OffY, known.W, known.H);
            if (!failed)
            {
                if (_cache.Count > 2000) _cache.Clear();
                _cache[key] = m;
                _lastGood[e.Id] = m;
            }
        }
        return new RectD(e.X + m.OffX, e.Y + m.OffY, m.W, m.H);
    }

    /// <summary>Everything that influences how large a text/barcode is, except its position.</summary>
    private static string MeasureKey(LabelElement e, LabelDocument doc)
    {
        var f = doc.DefaultFont;
        string vars = string.Join('\u0001', doc.Variables.Select(v => v.Name + "=" + v.SampleValue));
        string content = Files.RotulaFileSerializer.Fingerprint(e);
        return $"{(int)doc.Dpi}|{doc.DefaultRotation}|{f?.FontId}{f?.Height}x{f?.Width}|{vars}|{content}";
    }

    private (int W, int H, int OffX, int OffY) Probe(LabelElement e, LabelDocument doc, IReadOnlyDictionary<string, string>? data, ref bool failed)
    {
        ProbeCount++;
        var (probeW, probeH) = ProbeSize(e, doc);
        var temp = new LabelDocument
        {
            Width = Measure.Dots(probeW),
            Height = Measure.Dots(probeH),
            Dpi = doc.Dpi,
            DefaultFont = doc.DefaultFont,
            DefaultRotation = doc.DefaultRotation,
        };
        // Clone() keeps the definition; the adapter resolves it, so a date or computed value is measured at its real width.
        foreach (var v in doc.Variables) temp.Variables.Add(v.Clone());
        var clone = ElementCloner.Clone(e, newId: false);
        clone.X = ProbePad;
        clone.Y = ProbePad;
        // Reverse printing inverts what is underneath; on a blank probe that would make the whole area ink.
        clone.ReverseField = false;
        // An object that is switched off for the shown record still has a size and must stay selectable; the renderer would otherwise skip it and
        // it would measure blank.
        clone.NotPrintable = false;
        clone.PrintCondition = null;
        temp.Elements.Add(clone);

        try
        {
            using var result = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(temp, data));
            var (x0, y0, x1, y1) = FindInk(result.Bitmap);
            if (x1 < x0) return (FallbackSize, FallbackSize, 0, e.OriginKind == FieldOriginKind.Typeset ? -FallbackSize : 0);
            return (x1 - x0 + 1, y1 - y0 + 1, x0 - ProbePad, y0 - ProbePad);
        }
        catch (Exception ex)
        {
            // Too large to draw, or the bitmap could not be allocated: never let a measurement take the editor down. The caller keeps the
            // last known size when there is one; otherwise a small placeholder lets the element still be selected and moved.
            failed = true;
            ProbeFailureCount++;
            if (Interlocked.Exchange(ref _failureReported, 1) == 0) ProbeFailed?.Invoke(ex);
            return (FallbackSize, FallbackSize, 0, 0);
        }
    }

    /// <summary>Finds the bounding box of non-white pixels; returns x1 &lt; x0 when the bitmap is blank.</summary>
    private static (int X0, int Y0, int X1, int Y1) FindInk(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var span = bmp.GetPixelSpan();
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * bmp.RowBytes;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                // Anything noticeably darker than the white background counts as ink (anti-aliased edges included).
                if (span[i] < 200 || span[i + 1] < 200 || span[i + 2] < 200)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
        }
        return (x0, y0, x1, y1);
    }
}
