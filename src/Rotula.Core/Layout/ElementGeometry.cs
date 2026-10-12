using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Core.Layout;

/// <summary>
/// Where an element's box lies around its origin point. ZPL places a field by one point (<c>^FO</c> top-left, or <c>^FT</c> baseline /
/// bottom-left) and turns the field clockwise around that point, so the box an element occupies depends on its origin kind and rotation as
/// well as its size. The same rules are used by the ZPL generator, the preview and the designer so they never disagree about where a box is.
/// </summary>
public static class ElementGeometry
{
    /// <summary>The rotation an element is drawn with: its own, or the label default (<c>^FW</c>).</summary>
    public static FieldRotation EffectiveRotation(LabelElement e, LabelDocument doc)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(doc);
        return e switch
        {
            // ^GC, ^GE and ^GD have no orientation parameter, so rotation never changes their box.
            CircleElement or EllipseElement or DiagonalLineElement => FieldRotation.Normal,
            // The dots of an image are turned before they are sent (only the element's own rotation applies, never ^FW).
            ImageElement img => img.Rotation ?? FieldRotation.Normal,
            _ => e.Rotation ?? doc.DefaultRotation,
        };
    }

    /// <summary>
    /// The box of an element whose unrotated size is <paramref name="width"/> x <paramref name="height"/>, relative to its origin point (0, 0):
    /// the origin kind decides which point of the box the origin is, and the rotation turns the box clockwise around it.
    /// </summary>
    public static DotRect BoxAtOrigin(LabelElement e, LabelDocument doc, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(e);
        var rotation = EffectiveRotation(e, doc);
        int w = Math.Max(1, width), h = Math.Max(1, height);
        bool swap = rotation is FieldRotation.Rotated90 or FieldRotation.Rotated270;
        if (e.OriginKind == FieldOriginKind.Origin)
            return new DotRect(0, 0, swap ? h : w, swap ? w : h);

        // ^FT: the origin is the bottom-left corner of the unrotated box and the box rotates clockwise around it.
        var corners = new (double X, double Y)[] { (0, -h), (w, -h), (w, 0), (0, 0) };
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
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
            minX = Math.Min(minX, rx); maxX = Math.Max(maxX, rx);
            minY = Math.Min(minY, ry); maxY = Math.Max(maxY, ry);
        }
        return new DotRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// The label rectangle in element coordinates. Element positions count from the label home (<c>^LH</c>, the left and top margins), so the
    /// label's own top-left corner is at minus the margins and its size is the full label, margins included.
    /// </summary>
    public static DotRect LabelBox(LabelDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        int hx = doc.Margins.Left.ToDots(doc.Dpi), hy = doc.Margins.Top.ToDots(doc.Dpi);
        return new DotRect(-hx, -hy, doc.WidthDots, doc.HeightDots);
    }
}
