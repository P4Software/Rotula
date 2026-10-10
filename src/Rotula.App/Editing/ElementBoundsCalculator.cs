using Rotula.Core.Elements;
using Rotula.Core.Imaging;
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
            m = Probe(e, doc, data);
            if (_cache.Count > 2000) _cache.Clear();
            _cache[key] = m;
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

    private (int W, int H, int OffX, int OffY) Probe(LabelElement e, LabelDocument doc, IReadOnlyDictionary<string, string>? data)
    {
        ProbeCount++;
        var temp = new LabelDocument
        {
            Width = Measure.Dots(Math.Max(1000, doc.WidthDots * 3 / 2) + 2 * ProbePad),
            Height = Measure.Dots(Math.Max(1000, doc.HeightDots * 3 / 2) + 2 * ProbePad),
            Dpi = doc.Dpi,
            DefaultFont = doc.DefaultFont,
            DefaultRotation = doc.DefaultRotation,
        };
        foreach (var v in doc.Variables) temp.Variables.Add(new LabelVariable(v.Name, v.SampleValue));
        var clone = ElementCloner.Clone(e, newId: false);
        clone.X = ProbePad;
        clone.Y = ProbePad;
        // Reverse printing inverts what is underneath; on a blank probe that would make the whole area ink.
        clone.ReverseField = false;
        temp.Elements.Add(clone);

        try
        {
            using var result = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(temp, data));
            var (x0, y0, x1, y1) = FindInk(result.Bitmap);
            if (x1 < x0) return (FallbackSize, FallbackSize, 0, e.OriginKind == FieldOriginKind.Typeset ? -FallbackSize : 0);
            return (x1 - x0 + 1, y1 - y0 + 1, x0 - ProbePad, y0 - ProbePad);
        }
        catch (ArgumentException)
        {
            // Too large to draw: fall back to a small placeholder so the element can still be selected and moved.
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
