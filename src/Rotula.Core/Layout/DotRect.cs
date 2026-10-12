using Rotula.Core.Model;

namespace Rotula.Core.Layout;

/// <summary>An axis-aligned rectangle in label dots (y grows downwards).</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in dots.</param>
/// <param name="Height">Height in dots.</param>
public readonly record struct DotRect(double X, double Y, double Width, double Height)
{
    /// <summary>Right edge.</summary>
    public double Right => X + Width;

    /// <summary>Bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>Horizontal centre.</summary>
    public double CenterX => X + Width / 2;

    /// <summary>Vertical centre.</summary>
    public double CenterY => Y + Height / 2;

    /// <summary>The same rectangle moved by a distance.</summary>
    public DotRect Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>One of the nine anchor points of the rectangle.</summary>
    public (double X, double Y) PointAt(AnchorPoint anchor) => anchor switch
    {
        AnchorPoint.TopLeft => (X, Y),
        AnchorPoint.TopCenter => (CenterX, Y),
        AnchorPoint.TopRight => (Right, Y),
        AnchorPoint.MiddleLeft => (X, CenterY),
        AnchorPoint.Center => (CenterX, CenterY),
        AnchorPoint.MiddleRight => (Right, CenterY),
        AnchorPoint.BottomLeft => (X, Bottom),
        AnchorPoint.BottomCenter => (CenterX, Bottom),
        _ => (Right, Bottom),
    };
}
