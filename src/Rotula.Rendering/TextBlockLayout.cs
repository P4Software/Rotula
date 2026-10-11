using Rotula.Core.Text;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Rendering;

/// <summary>
/// The one place that lays out and paints a block of styled text. The label preview (<see cref="LabelRenderer"/>) and the rasterizer that makes
/// the picture a printer receives (<see cref="SkiaTextRasterizer"/>) both use it, so what the screen shows is what is printed.
/// </summary>
internal sealed class TextBlockLayout : IDisposable
{
    private readonly SKFont _font;
    private readonly bool _underline;
    private readonly double _boxWidth;
    private readonly float _hanging;
    private readonly RenderTextAlignment _alignment;
    private readonly List<string> _lines;

    /// <summary>Lays the text out. Sizes are in dots; <paramref name="em"/> is the em size in dots.</summary>
    public TextBlockLayout(string text, SKTypeface typeface, float em, float skewX, bool underline, double boxWidth, double boxHeight, int maxLines,
        double lineSpacing, RenderTextAlignment alignment, double hangingIndent)
    {
        _font = new SKFont(typeface, em) { Edging = SKFontEdging.Antialias, Subpixel = true, SkewX = skewX };
        _underline = underline;
        _boxWidth = boxWidth;
        _alignment = alignment;
        _hanging = (float)Math.Max(0, hangingIndent);

        LineHeight = _font.Spacing + (float)lineSpacing;
        Ascent = -_font.Metrics.Ascent; // Skia reports the ascent as a negative number; the first baseline sits one ascent below the top edge.
        int allowed = maxLines;
        int fitByHeight = 0;
        if (boxHeight > 0 && LineHeight > 0)
        {
            // A fixed-height box shows only the lines that fit completely (at least one, so a tiny box is not invisible).
            fitByHeight = Math.Max(1, (int)Math.Floor(boxHeight / LineHeight));
            allowed = allowed > 0 ? Math.Min(allowed, fitByHeight) : fitByHeight;
        }

        _lines = LabelRenderer.WrapText(text, _font, boxWidth, 0, _hanging);
        int total = _lines.Count;
        if (maxLines > 0 && total > maxLines) Overflow |= TextOverflow.Lines;
        if (fitByHeight > 0 && total > fitByHeight) Overflow |= TextOverflow.Height;
        if (allowed > 0 && _lines.Count > allowed) _lines.RemoveRange(allowed, _lines.Count - allowed);
        if (boxWidth > 0)
            for (int i = 0; i < _lines.Count; i++)
                if (_font.MeasureText(_lines[i]) > AvailableWidth(i) + 0.5) { Overflow |= TextOverflow.Width; break; }

        Width = boxWidth > 0 ? boxWidth : _lines.Max(l => (double)_font.MeasureText(l));
        Height = LineHeight * _lines.Count;
    }

    /// <summary>The lines that are drawn.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Height of one line including the extra line spacing.</summary>
    public float LineHeight { get; }

    /// <summary>Distance from the top of the first line to its baseline.</summary>
    public float Ascent { get; }

    /// <summary>Width of the block: the box width, or the widest line when there is no box.</summary>
    public double Width { get; }

    /// <summary>Height of all drawn lines.</summary>
    public double Height { get; }

    /// <summary>What did not fit.</summary>
    public TextOverflow Overflow { get; }

    private double AvailableWidth(int line) => line == 0 ? _boxWidth : Math.Max(1, _boxWidth - _hanging);

    /// <summary>Paints the block with its top-left corner at the canvas origin.</summary>
    public void Paint(SKCanvas canvas, SKPaint paint)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            float lineWidth = _font.MeasureText(_lines[i]);
            bool isLast = i == _lines.Count - 1;
            float indent = i == 0 ? 0f : _hanging;
            float room = (float)Width - indent;
            float x = indent;
            if (_boxWidth > 0)
            {
                x += _alignment switch
                {
                    RenderTextAlignment.Center => (room - lineWidth) / 2f,
                    RenderTextAlignment.Right => room - lineWidth,
                    _ => 0f,
                };
            }

            float baseline = Ascent + i * LineHeight;
            var line = _lines[i];
            if (_alignment == RenderTextAlignment.Justified && _boxWidth > 0 && !isLast && line.Contains(' '))
            {
                DrawJustified(canvas, line, baseline, indent, room, paint);
                continue;
            }
            canvas.DrawText(line, x, baseline, SKTextAlign.Left, _font, paint);
            if (_underline) DrawUnderline(canvas, x, baseline, lineWidth, paint);
        }
    }

    /// <summary>Spreads the words of a line evenly across the room the line has.</summary>
    private void DrawJustified(SKCanvas canvas, string line, float baseline, float indent, float room, SKPaint paint)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            canvas.DrawText(line, indent, baseline, SKTextAlign.Left, _font, paint);
            if (_underline) DrawUnderline(canvas, indent, baseline, _font.MeasureText(line), paint);
            return;
        }
        float total = words.Sum(w => _font.MeasureText(w));
        float gap = Math.Max(0, (room - total) / (words.Length - 1));
        float x = indent;
        foreach (var w in words)
        {
            canvas.DrawText(w, x, baseline, SKTextAlign.Left, _font, paint);
            x += _font.MeasureText(w) + gap;
        }
        // The underline runs through the gaps too, like a word processor's.
        if (_underline) DrawUnderline(canvas, indent, baseline, x - gap - indent, paint);
    }

    private void DrawUnderline(SKCanvas canvas, float x, float baseline, float width, SKPaint paint)
    {
        var m = _font.Metrics;
        float thickness = Math.Max(1f, m.UnderlineThickness ?? _font.Size / 14f);
        float top = baseline + (m.UnderlinePosition ?? _font.Size / 10f);
        canvas.DrawRect(x, top, width, thickness, paint);
    }

    /// <summary>The font used to measure and paint, for callers that need exact metrics.</summary>
    public SKFont Font => _font;

    public void Dispose() => _font.Dispose();
}
