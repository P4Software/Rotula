using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rotula.Core.Model;
using Rotula.Rendering.View;

namespace Rotula.App.Controls;

/// <summary>
/// A ruler along the design canvas. Tick spacing comes from <see cref="Ruler.GetTicks"/> in Rotula.Rendering; this control
/// only draws them. Zero is the label's top-left corner (not the printable-area origin).
/// </summary>
public sealed class RulerControl : FrameworkElement
{
    /// <summary>Orientation of the ruler.</summary>
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(RulerControl),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Unit shown.</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(LengthUnit), typeof(RulerControl),
        new FrameworkPropertyMetadata(LengthUnit.Millimeters, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Printer resolution.</summary>
    public static readonly DependencyProperty DpiProperty = DependencyProperty.Register(
        nameof(Dpi), typeof(int), typeof(RulerControl), new FrameworkPropertyMetadata(203, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Pixels per dot.</summary>
    public static readonly DependencyProperty PixelsPerDotProperty = DependencyProperty.Register(
        nameof(PixelsPerDot), typeof(double), typeof(RulerControl), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Scroll offset of the canvas in pixels.</summary>
    public static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        nameof(ScrollOffset), typeof(double), typeof(RulerControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Orientation.</summary>
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <summary>Unit.</summary>
    public LengthUnit Unit { get => (LengthUnit)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>DPI.</summary>
    public int Dpi { get => (int)GetValue(DpiProperty); set => SetValue(DpiProperty, value); }

    /// <summary>Pixels per dot.</summary>
    public double PixelsPerDot { get => (double)GetValue(PixelsPerDotProperty); set => SetValue(PixelsPerDotProperty, value); }

    /// <summary>Scroll offset.</summary>
    public double ScrollOffset { get => (double)GetValue(ScrollOffsetProperty); set => SetValue(ScrollOffsetProperty, value); }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = RenderSize;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF3)), null, new Rect(size));
        bool horizontal = Orientation == Orientation.Horizontal;
        double length = horizontal ? size.Width : size.Height;
        double ppd = PixelsPerDot;
        if (ppd <= 0 || Dpi <= 0 || length <= 0) return;

        // Position p (px, in the ruler) shows label dot (ScrollOffset + p - Margin) / ppd.
        double startDots = (ScrollOffset - DesignCanvas.Gutter) / ppd;
        double endDots = (ScrollOffset + length - DesignCanvas.Gutter) / ppd;
        var ticks = Ruler.GetTicks(Unit, Dpi, ppd, startDots, endDots);
        var pen = new Pen(Brushes.DimGray, 1);
        var dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        foreach (var t in ticks)
        {
            double p = DesignCanvas.Gutter + t.PositionDots * ppd - ScrollOffset;
            if (p < 0 || p > length) continue;
            // Half-pixel offset keeps one-pixel lines sharp.
            double pos = Math.Floor(p) + 0.5;
            double len = t.IsMajor ? 12 : 5;
            if (horizontal) dc.DrawLine(pen, new Point(pos, size.Height), new Point(pos, size.Height - len));
            else dc.DrawLine(pen, new Point(size.Width, pos), new Point(size.Width - len, pos));
            if (t.IsMajor && t.Label.Length > 0)
            {
                var ft = new FormattedText(t.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, Brushes.DimGray, dpiScale);
                if (horizontal) dc.DrawText(ft, new Point(pos + 2, 1));
                else
                {
                    dc.PushTransform(new TranslateTransform(1, pos + 2));
                    dc.PushTransform(new RotateTransform(0));
                    dc.DrawText(ft, new Point(0, 0));
                    dc.Pop();
                    dc.Pop();
                }
            }
        }
        dc.DrawLine(new Pen(Brushes.Silver, 1), horizontal ? new Point(0, size.Height - 0.5) : new Point(size.Width - 0.5, 0),
            horizontal ? new Point(size.Width, size.Height - 0.5) : new Point(size.Width - 0.5, size.Height));
    }
}
