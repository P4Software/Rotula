namespace Rotula.Core.Text;

/// <summary>How a wrapped text block (<c>^FB</c>) reacts when its content is longer than the box.</summary>
public enum TextFitMode
{
    /// <summary>The box is used as designed. Text that does not fit is cut off by the printer, and Rotula warns about it.</summary>
    None,

    /// <summary>The font gets as large as the box allows, between a minimum and a maximum size. Every record of a data merge gets its own size.</summary>
    Shrink,

    /// <summary>The block keeps its font size and gets as many lines as the content needs, up to a cap.</summary>
    Grow,
}

/// <summary>
/// The "text fit" setting of a wrapped text block (see <see cref="Elements.TextBlockOptions.Fit"/>). It is Rotula-only metadata: the printer never
/// sees it. At generate time (and for every record of a data merge) <see cref="TextFitResolver"/> works out the font size or the number of lines
/// and the ZPL carries plain computed values, so the fit mode itself does not survive a ZPL round trip (the computed <c>^A</c> / <c>^FB</c> values do).
/// </summary>
/// <remarks>
/// Sizes are in points, for printer fonts and Windows fonts alike. The model stores a printer font's height in dots and a Windows font's size in
/// points; keeping the limits in points means switching the font kind, or converting the label between 203 and 300 dpi, never needs to rescale them.
/// </remarks>
public sealed class TextFitOptions
{
    /// <summary>Smallest size a limit may have (points).</summary>
    public const double LowestSize = 1;

    /// <summary>Default for <see cref="MaxLinesCap"/>.</summary>
    public const int DefaultMaxLinesCap = 20;

    /// <summary>The fit mode.</summary>
    public TextFitMode Mode { get; set; } = TextFitMode.None;

    /// <summary>Smallest font size <see cref="TextFitMode.Shrink"/> may use, in points.</summary>
    public double MinSize { get; set; } = 6;

    /// <summary>Largest font size <see cref="TextFitMode.Shrink"/> uses when the text fits, in points.</summary>
    public double MaxSize { get; set; } = 24;

    /// <summary>Most lines <see cref="TextFitMode.Grow"/> may give the block (1-9999). The block never has fewer lines than its own <c>MaxLines</c>.</summary>
    public int MaxLinesCap { get; set; } = DefaultMaxLinesCap;

    /// <summary>Do not warn that the text may not fit. Works in every mode, for labels where cut-off text is intended.</summary>
    public bool IgnoreOverflowWarning { get; set; }

    /// <summary>True when the options change nothing: such a block is saved and written exactly as before this feature existed.</summary>
    public bool IsDefault => Mode == TextFitMode.None && !IgnoreOverflowWarning;

    /// <summary>A copy that can be changed without touching this one.</summary>
    public TextFitOptions Clone() => new() { Mode = Mode, MinSize = MinSize, MaxSize = MaxSize, MaxLinesCap = MaxLinesCap, IgnoreOverflowWarning = IgnoreOverflowWarning };

    /// <summary>The limits made usable: both inside the allowed range and the maximum not below the minimum.</summary>
    public (double Min, double Max) NormalizedRange()
    {
        double min = Math.Clamp(double.IsNaN(MinSize) ? LowestSize : MinSize, LowestSize, FontChoice.MaxPointSize);
        double max = Math.Clamp(double.IsNaN(MaxSize) ? min : MaxSize, LowestSize, FontChoice.MaxPointSize);
        return (min, Math.Max(min, max));
    }
}
