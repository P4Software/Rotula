namespace Rotula.Rendering.View;

/// <summary>
/// Pure zoom logic for the design canvas. Zoom is a factor between 0.10 (10%) and 8.0 (800%).
/// At 100% the label is shown at its real physical size on a 96 DPI screen
/// (see <see cref="PixelsPerDot"/>), so a 100 mm label is about 378 device-independent pixels wide.
/// </summary>
public readonly record struct ZoomModel
{
    /// <summary>Smallest allowed zoom factor (10%).</summary>
    public const double MinZoom = 0.10;

    /// <summary>Largest allowed zoom factor (800%).</summary>
    public const double MaxZoom = 8.0;

    /// <summary>Device-independent pixels per inch on screen (WPF logical resolution).</summary>
    public const double ScreenDpi = 96.0;

    /// <summary>Zoom steps used by zoom in / zoom out, as factors.</summary>
    public static IReadOnlyList<double> Steps { get; } =
        [0.10, 0.25, 0.50, 0.75, 1.00, 1.25, 1.50, 2.00, 3.00, 4.00, 6.00, 8.00];

    private readonly double _factor;

    /// <summary>Creates a zoom; the factor is clamped into the allowed range.</summary>
    public ZoomModel(double factor) => _factor = Clamp(factor);

    /// <summary>The zoom factor (1.0 = 100%). The default value of the struct reads as 100%.</summary>
    public double Factor => _factor == 0 ? 1.0 : _factor;

    /// <summary>The zoom as a whole-number percentage for display.</summary>
    public int Percent => (int)Math.Round(Factor * 100);

    /// <summary>Clamps a factor into [<see cref="MinZoom"/>, <see cref="MaxZoom"/>]; NaN becomes 100%.</summary>
    public static double Clamp(double factor) =>
        double.IsNaN(factor) ? 1.0 : Math.Min(MaxZoom, Math.Max(MinZoom, factor));

    /// <summary>Creates a zoom from a percentage (for example 150 for 150%).</summary>
    public static ZoomModel FromPercent(double percent) => new(percent / 100.0);

    /// <summary>The next larger preset step, or the maximum when already at or above the last step.</summary>
    public ZoomModel ZoomIn()
    {
        foreach (var step in Steps)
            if (step > Factor + 1e-9) return new ZoomModel(step);
        return new ZoomModel(MaxZoom);
    }

    /// <summary>The next smaller preset step, or the minimum when already at or below the first step.</summary>
    public ZoomModel ZoomOut()
    {
        for (int i = Steps.Count - 1; i >= 0; i--)
            if (Steps[i] < Factor - 1e-9) return new ZoomModel(Steps[i]);
        return new ZoomModel(MinZoom);
    }

    /// <summary>Applies a mouse-wheel style multiplicative change (for example 1.1 to zoom in 10%).</summary>
    public ZoomModel Multiply(double ratio) => new(Factor * ratio);

    /// <summary>
    /// Screen pixels (device-independent) per printer dot at this zoom. 100% means true physical size:
    /// <c>96 / dpi</c> pixels per dot.
    /// </summary>
    public double PixelsPerDot(int dpi)
    {
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), "The printer resolution must be greater than zero.");
        return Factor * ScreenDpi / dpi;
    }

    /// <summary>The zoom that makes the whole label fit inside a viewport with a margin around it.</summary>
    /// <param name="viewportWidth">Viewport width in device-independent pixels.</param>
    /// <param name="viewportHeight">Viewport height in device-independent pixels.</param>
    /// <param name="labelWidthDots">Label width in dots.</param>
    /// <param name="labelHeightDots">Label height in dots.</param>
    /// <param name="dpi">Printer resolution.</param>
    /// <param name="margin">Free space kept on every side, in pixels.</param>
    public static ZoomModel FitToViewport(double viewportWidth, double viewportHeight,
        double labelWidthDots, double labelHeightDots, int dpi, double margin = 24)
    {
        if (labelWidthDots <= 0 || labelHeightDots <= 0) return new ZoomModel(1.0);
        double availW = Math.Max(1, viewportWidth - 2 * margin);
        double availH = Math.Max(1, viewportHeight - 2 * margin);
        double pxPerDot = Math.Min(availW / labelWidthDots, availH / labelHeightDots);
        return new ZoomModel(pxPerDot * dpi / ScreenDpi);
    }

    /// <summary>
    /// Scroll offset to use after changing zoom so the content point under the cursor stays under the cursor.
    /// </summary>
    /// <param name="oldScroll">Current scroll offset in pixels along one axis.</param>
    /// <param name="anchorInViewport">Cursor position inside the viewport (pixels) along the same axis.</param>
    /// <param name="oldPixelsPerDot">Pixels per dot before the change.</param>
    /// <param name="newPixelsPerDot">Pixels per dot after the change.</param>
    /// <returns>The new scroll offset in pixels (not clamped to the scrollable range).</returns>
    public static double ScrollAfterZoom(double oldScroll, double anchorInViewport, double oldPixelsPerDot, double newPixelsPerDot)
    {
        if (oldPixelsPerDot <= 0) throw new ArgumentOutOfRangeException(nameof(oldPixelsPerDot));
        double contentDot = (oldScroll + anchorInViewport) / oldPixelsPerDot;
        return contentDot * newPixelsPerDot - anchorInViewport;
    }
}
