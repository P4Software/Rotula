using Rotula.Core.Elements;

namespace Rotula.App.Editing;

/// <summary>
/// Pointer geometry of the design canvas in label dots, kept apart from WPF so the rules can be unit-tested.
/// All tolerances are given in dots; the canvas converts from screen pixels using the current zoom.
/// </summary>
public static class HitTesting
{
    /// <summary>Distance, in pixels, from the top edge of a selected element to its rotate handle.</summary>
    public const double RotateHandleOffsetPixels = 22;

    /// <summary>The front-most element whose bounds (grown by the tolerance, so thin lines can be hit) contain the point.</summary>
    public static LabelElement? HitElement(EditorSession session, double x, double y, double toleranceDots)
    {
        var elements = session.Document.Elements;
        for (int i = elements.Count - 1; i >= 0; i--)
        {
            var b = session.Bounds.GetBounds(elements[i]);
            if (Inflate(b, toleranceDots).Contains(x, y)) return elements[i];
        }
        return null;
    }

    /// <summary>Elements whose bounds touch the rectangle (rubber-band selection).</summary>
    public static IReadOnlyList<LabelElement> ElementsIn(EditorSession session, RectD area) =>
        session.Document.Elements.Where(e => session.Bounds.GetBounds(e).Intersects(area)).ToList();

    /// <summary>Position of a resize handle on a bounding box.</summary>
    public static (double X, double Y) HandlePosition(RectD b, ResizeHandle h) => h switch
    {
        ResizeHandle.TopLeft => (b.X, b.Y),
        ResizeHandle.Top => (b.CenterX, b.Y),
        ResizeHandle.TopRight => (b.Right, b.Y),
        ResizeHandle.Right => (b.Right, b.CenterY),
        ResizeHandle.BottomRight => (b.Right, b.Bottom),
        ResizeHandle.Bottom => (b.CenterX, b.Bottom),
        ResizeHandle.BottomLeft => (b.X, b.Bottom),
        _ => (b.X, b.CenterY),
    };

    /// <summary>The handle under the point, if any. Corners win over edges when they overlap on tiny elements.</summary>
    public static ResizeHandle? HitHandle(RectD bounds, double x, double y, double radiusDots)
    {
        ResizeHandle[] order =
        {
            ResizeHandle.TopLeft, ResizeHandle.TopRight, ResizeHandle.BottomRight, ResizeHandle.BottomLeft,
            ResizeHandle.Top, ResizeHandle.Right, ResizeHandle.Bottom, ResizeHandle.Left,
        };
        foreach (var h in order)
        {
            var (hx, hy) = HandlePosition(bounds, h);
            if (Math.Abs(hx - x) <= radiusDots && Math.Abs(hy - y) <= radiusDots) return h;
        }
        return null;
    }

    /// <summary>Centre of the rotate handle, above the top edge.</summary>
    public static (double X, double Y) RotateHandlePosition(RectD bounds, double offsetDots) => (bounds.CenterX, bounds.Y - offsetDots);

    /// <summary>True when the point is on the rotate handle.</summary>
    public static bool HitRotateHandle(RectD bounds, double x, double y, double offsetDots, double radiusDots)
    {
        var (hx, hy) = RotateHandlePosition(bounds, offsetDots);
        return Math.Abs(hx - x) <= radiusDots && Math.Abs(hy - y) <= radiusDots;
    }

    /// <summary>
    /// The box a handle drag produces: the dragged edges follow the pointer and the opposite edges stay put.
    /// The size never drops below <paramref name="minSize"/>.
    /// </summary>
    public static RectD ResizeRect(RectD start, ResizeHandle handle, double pointerX, double pointerY, double minSize)
    {
        double left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        if (handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft) left = Math.Min(pointerX, right - minSize);
        if (handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight) right = Math.Max(pointerX, left + minSize);
        if (handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight) top = Math.Min(pointerY, bottom - minSize);
        if (handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight) bottom = Math.Max(pointerY, top + minSize);
        return new RectD(left, top, right - left, bottom - top);
    }

    /// <summary>Number of clockwise quarter turns for a rotate-handle drag that ends at the given point (0 = straight up).</summary>
    public static int QuarterTurns(RectD bounds, double x, double y)
    {
        double angle = Math.Atan2(x - bounds.CenterX, -(y - bounds.CenterY)) * 180 / Math.PI;
        int turns = (int)Math.Round(angle / 90.0);
        return ((turns % 4) + 4) % 4;
    }

    private static RectD Inflate(RectD r, double by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
}
