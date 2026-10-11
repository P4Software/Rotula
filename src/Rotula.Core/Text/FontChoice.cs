using System.Globalization;
using Rotula.Core.Elements;

namespace Rotula.Core.Text;

/// <summary>Where the letters of a text come from.</summary>
public enum FontSource
{
    /// <summary>
    /// A font the printer has: one of the built-in ZPL fonts (<c>A</c> to <c>Z</c>, <c>0</c> to <c>9</c>) or a font stored on the printer
    /// (<c>^A@</c>). The label carries real text and the printer draws it with its own letters.
    /// </summary>
    Printer,

    /// <summary>
    /// A Windows (TrueType) font. A printer cannot have it, so the text is drawn to a bitmap at the printer's resolution when the label is
    /// printed and the label carries that picture (<c>^GF</c>) instead of text.
    /// </summary>
    Windows,
}

/// <summary>
/// The font a text uses, as the designer's font box shows it: a printer font (an id) or a Windows font (a family name), plus bold, italic,
/// underline and a size in points.
/// </summary>
/// <remarks>
/// <para><b>What is stored.</b> Only a Windows font is stored on the element (<see cref="TextElement.Font"/>). A printer font keeps living in the
/// element's <see cref="TextElement.FontId"/> / <see cref="TextElement.FontHeight"/> / <see cref="TextElement.FontWidth"/> exactly as before, so
/// every existing label opens and prints unchanged. <see cref="FromElement"/> and <see cref="ApplyTo"/> convert between the element and this value,
/// so the user interface can handle both kinds with one object.</para>
/// <para><b>One size.</b> For a Windows font <see cref="PointSize"/> is the size (it does not depend on the printer's resolution, so a label
/// converted from 203 to 300 dpi keeps its physical size without any change). For a printer font the size is the element's height in dots; use
/// <see cref="PointsToDots"/> and <see cref="DotsToPoints"/> to show it in points.</para>
/// <para><b>Styles.</b> A printer cannot make its built-in fonts bold, italic or underlined, so those settings only apply to a Windows font
/// (<see cref="SupportsStyles"/>); the generator warns when they are set on a printer font instead of dropping them silently.</para>
/// </remarks>
public sealed class FontChoice
{
    /// <summary>Largest point size accepted (about 14 cm), a generous bound against nonsense in files.</summary>
    public const double MaxPointSize = 1000;

    /// <summary>Longest family name accepted. Windows limits family names to 31 characters; the rest is room for localized names.</summary>
    public const int MaxFamilyLength = 64;

    /// <summary>Point size a new Windows font starts with.</summary>
    public const double DefaultPointSize = 12;

    /// <summary>Where the font lives.</summary>
    public FontSource Source { get; set; } = FontSource.Windows;

    /// <summary>The ZPL font id of a printer font ('0' is the scalable font, 'A' to 'Z' and '1' to '9' are bitmap fonts). Unused for a Windows font.</summary>
    public char PrinterFontId { get; set; } = '0';

    /// <summary>The family name of a Windows font, for example "Arial". Unused for a printer font.</summary>
    public string FamilyName { get; set; } = string.Empty;

    /// <summary>Bold (Windows fonts only).</summary>
    public bool Bold { get; set; }

    /// <summary>Italic (Windows fonts only).</summary>
    public bool Italic { get; set; }

    /// <summary>Underline (Windows fonts only).</summary>
    public bool Underline { get; set; }

    /// <summary>Size in points (1/72 inch) of a Windows font. Unused for a printer font, whose size is the element's height in dots.</summary>
    public double PointSize { get; set; } = DefaultPointSize;

    /// <summary>True for a Windows font, which is printed as a picture.</summary>
    public bool IsWindows => Source == FontSource.Windows;

    /// <summary>True when bold, italic and underline can be used (a Windows font).</summary>
    public bool SupportsStyles => IsWindows;

    /// <summary>A printer font with the given id.</summary>
    public static FontChoice ForPrinter(char fontId) => new() { Source = FontSource.Printer, PrinterFontId = fontId };

    /// <summary>A Windows font.</summary>
    /// <param name="family">Family name, for example "Arial".</param>
    /// <param name="pointSize">Size in points.</param>
    /// <param name="bold">Bold.</param>
    /// <param name="italic">Italic.</param>
    /// <param name="underline">Underline.</param>
    public static FontChoice ForWindows(string family, double pointSize = DefaultPointSize, bool bold = false, bool italic = false, bool underline = false) =>
        new() { Source = FontSource.Windows, FamilyName = family, PointSize = pointSize, Bold = bold, Italic = italic, Underline = underline };

    /// <summary>A copy that can be changed without touching this one.</summary>
    public FontChoice Clone() => new()
    {
        Source = Source, PrinterFontId = PrinterFontId, FamilyName = FamilyName, Bold = Bold, Italic = Italic, Underline = Underline, PointSize = PointSize,
    };

    /// <summary>
    /// The font of an element as the font box shows it: its Windows font, or else a printer font built from its font id.
    /// </summary>
    /// <param name="element">The text.</param>
    /// <param name="defaultFontId">The label's default font id (<c>^CF</c>) used when the element has none.</param>
    public static FontChoice FromElement(TextElement element, char defaultFontId = '0')
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Font is { } w ? w.Clone() : ForPrinter(element.FontId ?? defaultFontId);
    }

    /// <summary>
    /// Stores this choice on <paramref name="element"/>: a Windows font goes to <see cref="TextElement.Font"/>; a printer font sets
    /// <see cref="TextElement.FontId"/> and clears <see cref="TextElement.Font"/>, so a label that uses only printer fonts is saved as it always was.
    /// The element's sizes are not touched.
    /// </summary>
    public void ApplyTo(TextElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (IsWindows) element.Font = Clone();
        else
        {
            element.Font = null;
            element.FontId = PrinterFontId;
        }
    }

    /// <summary>Converts points to printer dots at <paramref name="dpi"/> (a point is 1/72 inch).</summary>
    public static double PointsToDots(double points, int dpi) => points * dpi / 72.0;

    /// <summary>Converts printer dots at <paramref name="dpi"/> to points.</summary>
    public static double DotsToPoints(double dots, int dpi) => dpi <= 0 ? 0 : dots * 72.0 / dpi;

    /// <summary>
    /// Why this choice cannot be used, as a message key with its arguments, or null when it is fine. Used by the generator and by
    /// the file reader to refuse hostile or empty values.
    /// </summary>
    public string? Validate()
    {
        if (!IsWindows) return null;
        if (FamilyName.Length == 0 || FamilyName.Length > MaxFamilyLength || FamilyName.Any(char.IsControl)) return "Text.Font.Invalid";
        if (double.IsNaN(PointSize) || double.IsInfinity(PointSize) || PointSize < 1 || PointSize > MaxPointSize) return "Text.Font.Invalid";
        return null;
    }

    /// <inheritdoc />
    public override string ToString() => IsWindows
        ? string.Create(CultureInfo.InvariantCulture, $"{FamilyName} {PointSize:0.##} pt{(Bold ? " bold" : string.Empty)}{(Italic ? " italic" : string.Empty)}{(Underline ? " underline" : string.Empty)}")
        : "Printer font " + PrinterFontId;
}

/// <summary>Checks the file names of fonts stored on the printer (<c>^A@</c>, <c>^CW</c>), which follow the printer's 8.3 naming.</summary>
public static class StoredFonts
{
    // An optional device letter (R: E: B: A:), a name of up to 8 characters and a font extension.
    private static readonly System.Text.RegularExpressions.Regex Path = new(@"^(?:[REBAreba]:)?[A-Za-z0-9_\-]{1,8}\.(?:FNT|TTF|TTE|fnt|ttf|tte)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when <paramref name="path"/> is a font file name a printer accepts, for example <c>E:ARIAL.TTF</c>.</summary>
    public static bool IsValidPath(string? path) => path is not null && Path.IsMatch(path);
}
