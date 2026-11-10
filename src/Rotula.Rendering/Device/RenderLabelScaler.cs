using Rotula.Rendering.Input;

namespace Rotula.Rendering.Device;

/// <summary>
/// Rescales a <see cref="RenderLabel"/> to any printer resolution (150, 300, 600, 1200 dpi ...) so the label keeps its
/// physical size, and so every barcode module stays a whole number of device dots (minimum 1).
/// <para>
/// The designer only builds render labels at the document's own resolution (203, 300 or 600 dpi). A printer driver can
/// report any resolution, so the device renderer first calls <see cref="ToResolution"/> and then draws the result with the
/// ordinary renderer. The rules mirror <c>LabelDpiConverter</c> in Rotula.Core, which works on the document model and is tied
/// to its three-value resolution enum, so it cannot be used here.
/// </para>
/// </summary>
public static class RenderLabelScaler
{
    /// <summary>
    /// Returns <paramref name="label"/> redrawn at <paramref name="targetDpi"/>: every length, position and size is multiplied by
    /// <c>targetDpi / label.Dpi</c> (one ratio for everything, rounded half away from zero), and barcode modules become whole dots
    /// of at least 1. The input is never changed. When the resolution already matches, the same instance is returned.
    /// </summary>
    /// <param name="label">The label at its own resolution.</param>
    /// <param name="targetDpi">The resolution to draw at, in dots per inch; must be positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetDpi"/> or the label's own resolution is not positive.</exception>
    public static RenderLabel ToResolution(RenderLabel label, int targetDpi)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetDpi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(label.Dpi);
        if (targetDpi == label.Dpi) return label;

        // One ratio for every quantity, so rotated and mirrored geometry stays congruent: a rotation is an angle and is
        // never scaled, and (X, Y) rotates with the element's own box exactly as it did at the original resolution.
        double ratio = (double)targetDpi / label.Dpi;

        var elements = new List<RenderElement>(label.Elements.Count);
        foreach (var element in label.Elements)
            elements.Add(ScaleElement(element, ratio));

        // 'with' keeps the whole-label flags (Inverted, Mirrored) that are not constructor parameters.
        return label with
        {
            WidthDots = ToDeviceDots(label.WidthDots, ratio, 1),
            HeightDots = ToDeviceDots(label.HeightDots, ratio, 1),
            Dpi = targetDpi,
            Elements = elements,
        };
    }

    /// <summary>
    /// Scales a dot quantity to whole device dots: <paramref name="value"/> times <paramref name="ratio"/>, rounded half away from zero (the rule
    /// LabelDpiConverter uses), never below <paramref name="min"/>. This is the one rounding rule for the label size and for barcode modules; the
    /// driver pre-flight calls it too, so the module and picture size it judges are exactly the ones drawn.
    /// </summary>
    /// <param name="value">The quantity in dots at the original resolution.</param>
    /// <param name="ratio">Target resolution divided by the original resolution.</param>
    /// <param name="min">Smallest result (1 keeps a thin bar or a tiny label from vanishing).</param>
    public static int ToDeviceDots(double value, double ratio, int min)
    {
        double scaled = Math.Round(value * ratio, MidpointRounding.AwayFromZero);
        // Clamped before the cast so an absurd size saturates (and is then refused by a size check) instead of wrapping around.
        return Math.Max(min, (int)Math.Clamp(scaled, int.MinValue, int.MaxValue));
    }

    /// <summary>
    /// Scales a length that may be zero or negative. Zero stays zero because it means "none" or "unlimited" (no wrapping, no
    /// height limit, square corners), and a positive length never collapses below <paramref name="min"/> so a thin rule
    /// does not vanish at a lower resolution.
    /// </summary>
    private static double Length(double value, double ratio, double min = 0)
    {
        if (value == 0) return 0;
        double scaled = Math.Round(value * ratio, MidpointRounding.AwayFromZero);
        return value > 0 ? Math.Max(min, scaled) : scaled;
    }

    /// <summary>Scales a coordinate or a signed offset; there is no lower bound because 0 and negative values are valid.</summary>
    private static double Offset(double value, double ratio) =>
        Math.Round(value * ratio, MidpointRounding.AwayFromZero);

    private static RenderElement ScaleElement(RenderElement element, double ratio)
    {
        switch (element)
        {
            case RenderText t:
                return t with
                {
                    X = Offset(t.X, ratio),
                    Y = Offset(t.Y, ratio),
                    // A bundled stand-in font is sized in whole dots like ZPL's ^A height. A Windows font is sized in
                    // points, which are physical; its em size in dots is kept exact so the driver's (vector) text
                    // lands at the true point size instead of a rounded one.
                    FontHeightDots = t.FamilyName is null ? Length(t.FontHeightDots, ratio, 1) : t.FontHeightDots * ratio,
                    HangingIndentDots = Offset(t.HangingIndentDots, ratio),
                    WidthDots = Length(t.WidthDots, ratio, 1),
                    HeightDots = Length(t.HeightDots, ratio, 1),
                    // May be negative (tighter lines), so it is an offset rather than a length.
                    LineSpacingDots = Offset(t.LineSpacingDots, ratio),
                    // MaxLines and Alignment are counts and enums: nothing resolution-dependent.
                };

            case RenderPlaceholder p:
                // A preview-only stand-in box; it is scaled like any other box so its position still matches the label.
                return p with
                {
                    X = Offset(p.X, ratio),
                    Y = Offset(p.Y, ratio),
                    WidthDots = Length(p.WidthDots, ratio, 1),
                    HeightDots = Length(p.HeightDots, ratio, 1),
                };

            case RenderBox b:
                return b with
                {
                    X = Offset(b.X, ratio),
                    Y = Offset(b.Y, ratio),
                    WidthDots = Length(b.WidthDots, ratio, 1),
                    HeightDots = Length(b.HeightDots, ratio, 1),
                    ThicknessDots = Length(b.ThicknessDots, ratio, 1),
                    CornerRadiusDots = Length(b.CornerRadiusDots, ratio),
                };

            case RenderCircle c:
                return c with
                {
                    X = Offset(c.X, ratio),
                    Y = Offset(c.Y, ratio),
                    DiameterDots = Length(c.DiameterDots, ratio, 1),
                    ThicknessDots = Length(c.ThicknessDots, ratio, 1),
                };

            case RenderEllipse e:
                return e with
                {
                    X = Offset(e.X, ratio),
                    Y = Offset(e.Y, ratio),
                    WidthDots = Length(e.WidthDots, ratio, 1),
                    HeightDots = Length(e.HeightDots, ratio, 1),
                    ThicknessDots = Length(e.ThicknessDots, ratio, 1),
                };

            case RenderDiagonal d:
                // LeansRight is a direction, not a length, and is kept by the 'with' copy.
                return d with
                {
                    X = Offset(d.X, ratio),
                    Y = Offset(d.Y, ratio),
                    WidthDots = Length(d.WidthDots, ratio, 1),
                    HeightDots = Length(d.HeightDots, ratio, 1),
                    ThicknessDots = Length(d.ThicknessDots, ratio, 1),
                };

            case RenderLine l:
                return l with
                {
                    X = Offset(l.X, ratio),
                    Y = Offset(l.Y, ratio),
                    // Signed extents: the sign is the direction of the line and must survive.
                    DeltaX = Offset(l.DeltaX, ratio),
                    DeltaY = Offset(l.DeltaY, ratio),
                    ThicknessDots = Length(l.ThicknessDots, ratio, 1),
                };

            case RenderBarcode bc:
                // Only the module width and the bar height are dot measures. DataBarSeparatorHeight is counted in
                // modules, so it follows the module size by itself; every other property is a symbology option.
                return bc with
                {
                    X = Offset(bc.X, ratio),
                    Y = Offset(bc.Y, ratio),
                    // Whole dots, never 0: a zero-width bar would erase the symbol and a fractional one blurs it.
                    ModuleDots = ToDeviceDots(bc.ModuleDots, ratio, 1),
                    HeightDots = ToDeviceDots(bc.HeightDots, ratio, 1),
                };

            case RenderImage im:
                // The bitmap bytes are shared untouched (they are already dithered dots); only the size they are drawn at changes.
                return im with
                {
                    X = Offset(im.X, ratio),
                    Y = Offset(im.Y, ratio),
                    WidthDots = Length(im.WidthDots, ratio, 1),
                    HeightDots = Length(im.HeightDots, ratio, 1),
                };

            default:
                // A new element kind must decide how it scales; silently passing it through would print it at the wrong size.
                throw new NotSupportedException($"Element type {element.GetType().Name} is not supported by the render label scaler.");
        }
    }
}
