using Rotula.Core.Model;
using Rotula.Rendering.Barcodes;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Rendering;

/// <summary>A problem found while drawing one element (for example barcode data that cannot be encoded).</summary>
/// <param name="Element">The element that could not be drawn properly.</param>
/// <param name="Message">A plain-language description of what happened and what to do next.</param>
public sealed record RenderIssue(RenderElement Element, string Message);

/// <summary>Options for <see cref="LabelRenderer.RenderBitmap"/>.</summary>
public sealed record RenderOptions
{
    /// <summary>
    /// Output pixels per printer dot. 1.0 gives a bitmap at printer resolution (one pixel per dot);
    /// other values are used for on-screen zoom. Must be greater than 0.
    /// </summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>Paper colour painted behind the elements.</summary>
    public SKColor Background { get; init; } = SKColors.White;
}

/// <summary>The result of a render: the bitmap plus anything that could not be drawn faithfully.</summary>
/// <param name="Bitmap">The rendered image. Dispose the result to release it.</param>
/// <param name="Issues">Elements that were replaced by a placeholder or otherwise degraded.</param>
public sealed record RenderResult(SKBitmap Bitmap, IReadOnlyList<RenderIssue> Issues) : IDisposable
{
    /// <summary>Releases the bitmap.</summary>
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Draws a <see cref="RenderLabel"/> with SkiaSharp, either onto any canvas (bitmap, PDF page, WPF surface)
/// or into a bitmap at printer resolution.
/// <para>
/// IMPORTANT - fidelity: barcodes and symbols are exact (module-for-module, so they scan), and boxes and
/// lines are exact to the dot. TEXT PREVIEW IS APPROXIMATE: the printer draws with its own resident fonts,
/// the preview with the bundled Liberation stand-ins, so glyph shapes, widths and line breaks can differ
/// slightly from the printed label.
/// </para>
/// </summary>
public static class LabelRenderer
{
    private const long MaxPixels = 120_000_000; // about 480 MB at 4 bytes per pixel; refuse beyond this.

    /// <summary>Renders the label to a new bitmap. Dispose the returned result when done.</summary>
    public static RenderResult RenderBitmap(RenderLabel label, RenderOptions? options = null)
    {
        options ??= new RenderOptions();
        if (options.Scale <= 0 || double.IsNaN(options.Scale))
            throw new ArgumentOutOfRangeException(nameof(options), "The render scale must be greater than zero.");
        if (label.WidthDots <= 0 || label.HeightDots <= 0)
            throw new ArgumentException("The label size must be greater than zero.", nameof(label));

        int pw = Math.Max(1, (int)Math.Round(label.WidthDots * options.Scale));
        int ph = Math.Max(1, (int)Math.Round(label.HeightDots * options.Scale));
        if ((long)pw * ph > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(options),
                "The label is too large to draw at this zoom. Lower the zoom level and try again.");

        var bitmap = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(options.Background);
            // Map dot space onto the pixel grid, using the exact ratio so rounding does not shift the content.
            canvas.Scale((float)pw / label.WidthDots, (float)ph / label.HeightDots);
            var issues = Draw(canvas, label);
            return new RenderResult(bitmap, issues);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Draws all elements onto <paramref name="canvas"/>, whose coordinate system must already be in printer dots
    /// with the origin at the label's top-left corner. The background is NOT painted here.
    /// </summary>
    /// <returns>Elements that could not be drawn faithfully; empty when everything was fine.</returns>
    public static IReadOnlyList<RenderIssue> Draw(SKCanvas canvas, RenderLabel label)
    {
        var issues = new List<RenderIssue>();
        foreach (var element in label.Elements)
        {
            using var layout = Layout.Create(element, issues);
            canvas.Save();
            try
            {
                canvas.Translate((float)element.X, (float)element.Y);

                // With ^FO the origin is the top-left corner of the ROTATED bounding box, so shift the unrotated
                // box back into the positive quadrant before rotating. With ^FT the field simply rotates around
                // its origin, so no shift is wanted.
                if (!element.Typeset)
                {
                    switch (element.Rotation)
                    {
                        case RenderRotation.Rotate90: canvas.Translate((float)layout.Height, 0); break;
                        case RenderRotation.Rotate180: canvas.Translate((float)layout.Width, (float)layout.Height); break;
                        case RenderRotation.Rotate270: canvas.Translate(0, (float)layout.Width); break;
                    }
                }
                // Skia's positive angle is clockwise in a y-down space, which is what the model means.
                if (element.Rotation != RenderRotation.None)
                    canvas.RotateDegrees((float)(int)element.Rotation);

                // ^FT: the origin is the text baseline, or the bottom-left corner for graphics and barcodes.
                if (element.Typeset)
                    canvas.Translate(0, (float)-layout.TypesetOffset);

                layout.Paint(canvas);
            }
            finally
            {
                canvas.Restore();
            }
        }
        return issues;
    }

    // ------------------------------------------------------------------------------ layout & painting

    /// <summary>Measured, ready-to-paint form of one element. Measuring and painting share one object so they never disagree.</summary>
    private abstract class Layout : IDisposable
    {
        /// <summary>Unrotated bounding box width in dots.</summary>
        public double Width { get; protected init; }

        /// <summary>Unrotated bounding box height in dots.</summary>
        public double Height { get; protected init; }

        /// <summary>Distance from the top of the box to the ^FT origin (baseline for text, bottom edge otherwise).</summary>
        public virtual double TypesetOffset => Height;

        /// <summary>Paints the element with its top-left corner at the canvas origin.</summary>
        public abstract void Paint(SKCanvas canvas);

        public virtual void Dispose() { }

        public static Layout Create(RenderElement e, List<RenderIssue> issues) => e switch
        {
            RenderText t => new TextLayout(t),
            RenderBox b => new BoxLayout(b),
            RenderPlaceholder p => new PlaceholderLayout(p),
            RenderCircle c => new CircleLayout(c),
            RenderLine l => new LineLayout(l),
            RenderBarcode bc => new BarcodeLayout(bc, issues),
            RenderImage im => new ImageLayout(im, issues),
            _ => new EmptyLayout(),
        };

        /// <summary>
        /// Builds a paint for an element. Reverse print inverts what is underneath: painting WHITE with the
        /// Difference blend mode gives |destination - white|, which flips each pixel it covers.
        /// </summary>
        protected static SKPaint MakePaint(RenderElement e, bool white, bool antiAlias)
        {
            var paint = new SKPaint { IsAntialias = antiAlias };
            if (e.Reverse)
            {
                paint.Color = SKColors.White;
                paint.BlendMode = SKBlendMode.Difference;
            }
            else
            {
                paint.Color = white ? SKColors.White : SKColors.Black;
            }
            return paint;
        }
    }

    private sealed class EmptyLayout : Layout
    {
        public override void Paint(SKCanvas canvas) { }
    }

    // ----------------------------------------------------------------------------------- text

    private sealed class TextLayout : Layout
    {
        private readonly RenderText _t;
        private readonly SKFont? _font;
        private readonly List<string> _lines = [];
        private readonly float _lineHeight;
        private readonly float _ascent;

        public TextLayout(RenderText t)
        {
            _t = t;
            if (string.IsNullOrEmpty(t.Text) || t.FontHeightDots <= 0)
                return;

            _font = new SKFont(RenderFonts.Get(t.Font, t.Bold), (float)t.FontHeightDots)
            {
                Edging = SKFontEdging.Antialias,
                Subpixel = true,
            };
            _lines = WrapText(t.Text, _font, t.WidthDots, t.MaxLines);
            _lineHeight = _font.Spacing + (float)t.LineSpacingDots;
            // Skia reports ascent as a negative number; the first baseline sits one ascent below the top edge.
            _ascent = -_font.Metrics.Ascent;
            Width = t.WidthDots > 0 ? t.WidthDots : _lines.Max(l => _font.MeasureText(l));
            Height = _lineHeight * _lines.Count;
        }

        public override double TypesetOffset => _ascent;

        public override void Paint(SKCanvas canvas)
        {
            if (_font is null) return;
            using var paint = MakePaint(_t, white: false, antiAlias: true);
            for (int i = 0; i < _lines.Count; i++)
            {
                float lineWidth = _font.MeasureText(_lines[i]);
                bool isLast = i == _lines.Count - 1;
                float x = 0f;
                if (_t.WidthDots > 0)
                {
                    x = _t.Alignment switch
                    {
                        RenderTextAlignment.Center => ((float)Width - lineWidth) / 2f,
                        RenderTextAlignment.Right => (float)Width - lineWidth,
                        _ => 0f,
                    };
                }

                var line = _lines[i];
                if (_t.Alignment == RenderTextAlignment.Justified && _t.WidthDots > 0 && !isLast && line.Contains(' '))
                {
                    DrawJustified(canvas, line, _ascent + i * _lineHeight, paint);
                    continue;
                }
                canvas.DrawText(line, x, _ascent + i * _lineHeight, SKTextAlign.Left, _font, paint);
            }
        }

        /// <summary>Spreads the words of a line evenly across the block width.</summary>
        private void DrawJustified(SKCanvas canvas, string line, float baseline, SKPaint paint)
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2)
            {
                canvas.DrawText(line, 0, baseline, SKTextAlign.Left, _font!, paint);
                return;
            }
            float total = words.Sum(w => _font!.MeasureText(w));
            float gap = Math.Max(0, ((float)Width - total) / (words.Length - 1));
            float x = 0;
            foreach (var w in words)
            {
                canvas.DrawText(w, x, baseline, SKTextAlign.Left, _font!, paint);
                x += _font!.MeasureText(w) + gap;
            }
        }

        public override void Dispose() => _font?.Dispose();
    }

    /// <summary>Splits text into lines that fit <paramref name="maxWidth"/> (0 = only explicit line breaks).</summary>
    internal static List<string> WrapText(string text, SKFont font, double maxWidth, int maxLines)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (maxWidth <= 0 || font.MeasureText(paragraph) <= maxWidth)
            {
                lines.Add(paragraph);
                continue;
            }

            var current = string.Empty;
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && font.MeasureText(candidate) > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            lines.Add(current);
        }

        if (maxLines > 0 && lines.Count > maxLines)
            lines.RemoveRange(maxLines, lines.Count - maxLines);
        return lines;
    }

    // ----------------------------------------------------------------------------------- shapes

    /// <summary>Hatched outline with a caption; honest about being a stand-in rather than the real content.</summary>
    private sealed class PlaceholderLayout : Layout
    {
        private readonly RenderPlaceholder _p;

        public PlaceholderLayout(RenderPlaceholder p)
        {
            _p = p;
            Width = Math.Max(0, p.WidthDots);
            Height = Math.Max(0, p.HeightDots);
        }

        public override void Paint(SKCanvas canvas)
        {
            if (Width <= 0 || Height <= 0) return;
            float w = (float)Width, h = (float)Height;
            using var paint = MakePaint(_p, white: false, antiAlias: false);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1;

            canvas.DrawRect(0.5f, 0.5f, w - 1, h - 1, paint);

            // Diagonal hatch, clipped to the box so the lines never spill out of it.
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, w, h));
            const float step = 12;
            for (float x = -h; x < w; x += step)
                canvas.DrawLine(x, h, x + h, 0, paint);
            canvas.Restore();

            if (string.IsNullOrEmpty(_p.Caption)) return;
            using var font = new SKFont(RenderFonts.Get(RenderFontFamily.Sans, bold: false), Math.Clamp(h / 4f, 8f, 24f))
            {
                Edging = SKFontEdging.Antialias,
            };
            // A white plate under the caption keeps it readable on top of the hatch lines.
            float textW = Math.Min(font.MeasureText(_p.Caption), w - 4);
            float plateH = font.Size + 4;
            float plateX = Math.Max(2, (w - textW) / 2 - 2), plateY = Math.Max(0, (h - plateH) / 2);
            using var plate = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            canvas.DrawRect(plateX, plateY, textW + 4, plateH, plate);
            canvas.Save();
            canvas.ClipRect(new SKRect(2, 0, w - 2, h));
            using var text = MakePaint(_p, white: false, antiAlias: true);
            canvas.DrawText(_p.Caption, plateX + 2, plateY + plateH - 4 - font.Metrics.Descent, SKTextAlign.Left, font, text);
            canvas.Restore();
        }
    }

    private sealed class BoxLayout : Layout
    {
        private readonly RenderBox _b;

        public BoxLayout(RenderBox b)
        {
            _b = b;
            Width = Math.Max(0, b.WidthDots);
            Height = Math.Max(0, b.HeightDots);
        }

        public override void Paint(SKCanvas canvas)
        {
            if (Width <= 0 || Height <= 0) return;
            float w = (float)Width, h = (float)Height;
            float th = (float)Math.Max(1, _b.ThicknessDots);
            float radius = (float)_b.CornerRadiusDots;

            bool solid = th * 2 >= Math.Min(w, h);
            using var paint = MakePaint(_b, _b.White, antiAlias: radius > 0);
            if (solid)
            {
                if (radius > 0) canvas.DrawRoundRect(0, 0, w, h, radius, radius, paint);
                else canvas.DrawRect(0, 0, w, h, paint);
                return;
            }

            // A stroke is centred on its path, so inset by half the thickness to keep the outer edge on the box bounds.
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = th;
            var rect = new SKRect(th / 2, th / 2, w - th / 2, h - th / 2);
            if (radius > 0) canvas.DrawRoundRect(rect, radius, radius, paint);
            else canvas.DrawRect(rect, paint);
        }
    }

    private sealed class CircleLayout : Layout
    {
        private readonly RenderCircle _c;

        public CircleLayout(RenderCircle c)
        {
            _c = c;
            Width = Height = Math.Max(0, c.DiameterDots);
        }

        public override void Paint(SKCanvas canvas)
        {
            if (Width <= 0) return;
            float d = (float)Width;
            float th = (float)Math.Max(1, _c.ThicknessDots);
            using var paint = MakePaint(_c, _c.White, antiAlias: true);
            if (th * 2 >= d)
            {
                canvas.DrawCircle(d / 2, d / 2, d / 2, paint);
                return;
            }
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = th;
            canvas.DrawCircle(d / 2, d / 2, (d - th) / 2, paint);
        }
    }

    private sealed class LineLayout : Layout
    {
        private readonly RenderLine _l;

        public LineLayout(RenderLine l)
        {
            _l = l;
            Width = Math.Abs(l.DeltaX);
            Height = Math.Abs(l.DeltaY);
        }

        public override void Paint(SKCanvas canvas)
        {
            // Axis-aligned lines stay crisp; only diagonals benefit from smoothing.
            bool axisAligned = _l.DeltaX == 0 || _l.DeltaY == 0;
            using var paint = MakePaint(_l, white: false, antiAlias: !axisAligned);
            paint.StrokeWidth = (float)Math.Max(1, _l.ThicknessDots);
            paint.StrokeCap = SKStrokeCap.Butt;
            canvas.DrawLine(0, 0, (float)_l.DeltaX, (float)_l.DeltaY, paint);
        }
    }

    private sealed class ImageLayout : Layout
    {
        private readonly RenderImage _im;
        private readonly SKImage? _image;

        public ImageLayout(RenderImage im, List<RenderIssue> issues)
        {
            _im = im;
            Width = Math.Max(0, im.WidthDots);
            Height = Math.Max(0, im.HeightDots);
            using var bmp = TryDecode(im.EncodedBytes);
            if (bmp is null)
                issues.Add(new RenderIssue(im, "The image could not be read. Choose the image file again."));
            else
                _image = SKImage.FromBitmap(bmp);
        }

        /// <summary>SkiaSharp throws for unrecognised data instead of returning null, so translate that into null.</summary>
        private static SKBitmap? TryDecode(byte[] bytes)
        {
            if (bytes.Length == 0) return null;
            try { return SKBitmap.Decode(bytes); }
            catch (ArgumentNullException) { return null; }
        }

        public override void Paint(SKCanvas canvas)
        {
            if (Width <= 0 || Height <= 0) return;
            if (_image is null)
            {
                DrawPlaceholder(canvas, (float)Width, (float)Height);
                return;
            }
            using var paint = MakePaint(_im, white: false, antiAlias: true);
            canvas.DrawImage(_image, new SKRect(0, 0, (float)Width, (float)Height),
                new SKSamplingOptions(SKFilterMode.Linear), paint);
        }

        public override void Dispose() => _image?.Dispose();
    }

    /// <summary>Crossed-out outline marking an element that could not be drawn.</summary>
    private static void DrawPlaceholder(SKCanvas canvas, float w, float h)
    {
        using var paint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawRect(1, 1, Math.Max(2, w - 2), Math.Max(2, h - 2), paint);
        canvas.DrawLine(0, 0, w, h, paint);
        canvas.DrawLine(0, h, w, 0, paint);
    }

    // ----------------------------------------------------------------------------------- barcodes

    private sealed class BarcodeLayout : Layout
    {
        private readonly RenderBarcode _bc;
        private readonly BarcodeMatrix? _matrix;
        private readonly int _module;
        private readonly float _rowHeight;
        private readonly float _textBand;   // vertical space taken by the human-readable line, 0 when none
        private readonly string _text = string.Empty;

        public BarcodeLayout(RenderBarcode bc, List<RenderIssue> issues)
        {
            _bc = bc;
            _module = Math.Max(1, bc.ModuleDots);
            var options = new BarcodeEncodeOptions
            {
                QrErrorCorrection = bc.QrErrorCorrection,
                Pdf417SecurityLevel = bc.Pdf417SecurityLevel,
                Pdf417Truncate = bc.Pdf417Truncate,
                CheckDigit = bc.CheckDigit,
            };
            _matrix = BarcodeEncoder.TryEncode(bc.Symbology, bc.Data, options, out var error);
            if (_matrix is null)
            {
                issues.Add(new RenderIssue(bc, error + " Check the barcode data."));
                Width = 160;
                Height = Math.Max(40, bc.HeightDots);
                return;
            }

            Width = _matrix.Width * _module;
            if (_matrix.IsLinear)
            {
                _rowHeight = Math.Max(1, bc.HeightDots);
                if (bc.ShowText)
                {
                    _text = BarcodeEncoder.HumanReadableText(bc.Symbology, bc.Data, options);
                    _textBand = TextSize + 4;
                }
                Height = _rowHeight + _textBand;
            }
            else
            {
                // ZXing repeats each PDF417 row 4 times, and ZPL's height is the height of one PDF417 row,
                // so one matrix row is a quarter of it. QR and Data Matrix modules are square.
                _rowHeight = bc.Symbology == BarcodeSymbology.Pdf417 ? Math.Max(1f, bc.HeightDots / 4f) : _module;
                Height = _matrix.Height * _rowHeight;
            }
        }

        private float TextSize => Math.Clamp(_module * 8f, 14f, 36f);

        public override void Paint(SKCanvas canvas)
        {
            if (_matrix is null)
            {
                DrawPlaceholder(canvas, (float)Width, (float)Height);
                return;
            }

            // No anti-aliasing: bars and modules must land on whole pixels to stay sharp and scannable.
            using var paint = MakePaint(_bc, white: false, antiAlias: false);
            float top = _bc.TextAbove && _textBand > 0 ? _textBand : 0f;

            for (int y = 0; y < _matrix.Height; y++)
            {
                int x = 0;
                while (x < _matrix.Width)
                {
                    if (!_matrix.IsDark(x, y)) { x++; continue; }
                    int start = x;
                    while (x < _matrix.Width && _matrix.IsDark(x, y)) x++;
                    canvas.DrawRect(start * _module, top + y * _rowHeight, (x - start) * _module, _rowHeight, paint);
                }
            }

            if (_textBand > 0)
                DrawHumanReadable(canvas, top);
        }

        private void DrawHumanReadable(SKCanvas canvas, float barsTop)
        {
            using var font = new SKFont(RenderFonts.Get(RenderFontFamily.Sans, false), TextSize) { Edging = SKFontEdging.Antialias };
            using var paint = MakePaint(_bc, white: false, antiAlias: true);
            float textWidth = font.MeasureText(_text);
            // Centered on the bars; the 4-dot gap mimics the printer's spacing between bars and text.
            float baseline = _bc.TextAbove
                ? -font.Metrics.Ascent
                : barsTop + _rowHeight + 4 - font.Metrics.Ascent;
            canvas.DrawText(_text, ((float)Width - textWidth) / 2f, baseline, SKTextAlign.Left, font, paint);
        }
    }
}
