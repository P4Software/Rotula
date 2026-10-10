using Rotula.Core.Elements;
using Rotula.Core.Zpl;

namespace Rotula.Core.Model;

/// <summary>Printable-area margins. The left/top values become the ^LH label home.</summary>
public sealed class LabelMargins
{
    /// <summary>Left margin; element X coordinates are relative to it.</summary>
    public Measure Left { get; set; } = Measure.Dots(0);
    /// <summary>Top margin; element Y coordinates are relative to it.</summary>
    public Measure Top { get; set; } = Measure.Dots(0);
    /// <summary>Right margin (used for bounds warnings only).</summary>
    public Measure Right { get; set; } = Measure.Dots(0);
    /// <summary>Bottom margin (used for bounds warnings only).</summary>
    public Measure Bottom { get; set; } = Measure.Dots(0);
}

/// <summary>Default font set with ^CF; text elements that omit a value inherit it.</summary>
public sealed class FontSpec
{
    /// <summary>Creates a font spec.</summary>
    public FontSpec(char fontId, int height, int width)
    {
        FontId = fontId;
        Height = height;
        Width = width;
    }

    /// <summary>ZPL font identifier ('0' is the scalable font, A-Z and 1-9 are bitmap fonts).</summary>
    public char FontId { get; set; }
    /// <summary>Character height in dots.</summary>
    public int Height { get; set; }
    /// <summary>Character width in dots (0 = proportional to height).</summary>
    public int Width { get; set; }
}

/// <summary>Print job options (^PQ).</summary>
public sealed class PrintOptions
{
    /// <summary>Total number of labels (1-99999999).</summary>
    public int Quantity { get; set; } = 1;
    /// <summary>Pause after this many labels (0 = never).</summary>
    public int PauseEvery { get; set; }
    /// <summary>Replicates of each serial number (1 = none).</summary>
    public int Replicates { get; set; } = 1;
    /// <summary>Override the pause count (^PQ "o" parameter).</summary>
    public bool OverridePause { get; set; }
}

/// <summary>
/// Print speeds (ZPL <c>^PR</c>) in inches per second, 1 to 14. Null leaves the printer's own setting alone.
/// </summary>
public sealed class PrintRateOptions
{
    /// <summary>Print speed (ZPL letters A-E are read as 2, 3, 4, 6 and 8).</summary>
    public int? Speed { get; set; }
    /// <summary>Slew speed: how fast the printer feeds over blank areas.</summary>
    public int? SlewSpeed { get; set; }
    /// <summary>Backfeed speed.</summary>
    public int? BackfeedSpeed { get; set; }

    /// <summary>True when no speed is set, so no command needs to be written.</summary>
    public bool IsEmpty => Speed is null && SlewSpeed is null && BackfeedSpeed is null;
}

/// <summary>A font file assigned to a one-character font id (ZPL <c>^CW</c>), for example letter <c>Q</c> for <c>E:ARIAL.TTF</c>.</summary>
public sealed class FontAlias
{
    /// <summary>Creates an alias.</summary>
    public FontAlias(char letter, string path)
    {
        Letter = char.ToUpperInvariant(letter);
        Path = path;
    }

    /// <summary>The font id (A-Z or 0-9).</summary>
    public char Letter { get; set; }
    /// <summary>The stored font, as <c>device:NAME.EXT</c> (the device is optional).</summary>
    public string Path { get; set; }
}

/// <summary>
/// A graphic that the file downloads to the printer (ZPL <c>~DG</c>, or <c>~DY</c> with raw bitmap data) so that a later
/// recall (<c>^XG</c>, <c>^IM</c>) in the same file can print it. Kept exactly as the printer receives it.
/// </summary>
public sealed class StoredGraphic
{
    /// <summary>Creates a stored graphic.</summary>
    /// <param name="path">Where it is stored, for example <c>R:LOGO.GRF</c>; normalised by <see cref="NormalizePath"/>.</param>
    /// <param name="bytesPerRow">Width of one row in bytes (8 dots per byte).</param>
    /// <param name="data">Packed rows, most significant bit first, 1 = black; its length is a whole number of rows.</param>
    public StoredGraphic(string path, int bytesPerRow, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (bytesPerRow < 1) throw new ArgumentOutOfRangeException(nameof(bytesPerRow));
        if (data.Length == 0 || data.Length % bytesPerRow != 0) throw new ArgumentException("Data must hold whole rows.", nameof(data));
        Path = NormalizePath(path);
        BytesPerRow = bytesPerRow;
        Data = data;
    }

    /// <summary>Normalised location: upper case, <c>device:NAME.EXT</c>.</summary>
    public string Path { get; }
    /// <summary>Bytes in one row.</summary>
    public int BytesPerRow { get; }
    /// <summary>Packed bitmap rows.</summary>
    public byte[] Data { get; }
    /// <summary>Width in dots.</summary>
    public int WidthDots => BytesPerRow * 8;
    /// <summary>Height in dots.</summary>
    public int HeightDots => Data.Length / BytesPerRow;

    /// <summary>
    /// Normalises a printer object path the way the printer reads it: upper case, device <c>R:</c> when none is given and
    /// extension <c>.GRF</c> when none is given.
    /// </summary>
    public static string NormalizePath(string path)
    {
        string p = (path ?? string.Empty).Trim().ToUpperInvariant();
        string device = "R:";
        int colon = p.IndexOf(':');
        if (colon == 1) { device = p[..2]; p = p[2..]; }
        else if (colon > 1) { p = p[(colon + 1)..]; }
        if (!p.Contains('.')) p += ".GRF";
        return device + p;
    }

    private static readonly System.Text.RegularExpressions.Regex PathShape = new(@"^[REBA]:[A-Z0-9_\-]{1,8}\.[A-Z0-9]{1,3}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="path"/> (as returned by <see cref="NormalizePath"/>) is a name a printer accepts: a device letter,
    /// a name of up to 8 characters and a short extension.
    /// </summary>
    public static bool IsValidPath(string path) => PathShape.IsMatch(path ?? string.Empty);

    /// <summary>
    /// Finds the graphic a recall refers to. A recall without a device letter searches in the printer's order (R, E, B, A);
    /// a recall without an extension means <c>.GRF</c>. Returns null when the file does not download it.
    /// </summary>
    public static StoredGraphic? Find(IEnumerable<StoredGraphic> graphics, string reference)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        string wanted = NormalizePath(reference);
        bool hasDevice = (reference ?? string.Empty).Trim().Length > 1 && reference!.Trim()[1] == ':';
        StoredGraphic? best = null;
        foreach (var g in graphics)
        {
            bool match = hasDevice ? g.Path == wanted : g.Path[2..] == wanted[2..];
            // A later download replaces an earlier one of the same name, so the last match wins.
            if (match) best = g;
        }
        return best;
    }

    /// <summary>The bitmap as 8-bit luminance (0 = black, 255 = white).</summary>
    public Imaging.GrayscaleBitmap ToGrayscale()
    {
        int w = WidthDots, h = HeightDots;
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = (Data[y * BytesPerRow + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
        return new Imaging.GrayscaleBitmap(w, h, pixels);
    }
}

/// <summary>A declared variable with the sample value used when no data is merged.</summary>
public sealed class LabelVariable
{
    /// <summary>Creates a variable.</summary>
    public LabelVariable(string name, string sampleValue = "")
    {
        Name = name;
        SampleValue = sampleValue;
    }

    /// <summary>Variable name used in <c>{{name}}</c> placeholders (letters, digits, underscore).</summary>
    public string Name { get; set; }
    /// <summary>Value used when the merge data has no entry for <see cref="Name"/>.</summary>
    public string SampleValue { get; set; }
}

/// <summary>
/// The label design: size, printer settings and an ordered element list (the order is both
/// z-order and ZPL emission order). Lengths stay in user units until ZPL generation.
/// </summary>
public sealed class LabelDocument
{
    /// <summary>Label width.</summary>
    public Measure Width { get; set; } = Measure.Millimeters(100);
    /// <summary>Label height.</summary>
    public Measure Height { get; set; } = Measure.Millimeters(150);
    /// <summary>Printhead resolution used to convert lengths to dots.</summary>
    public PrinterDpi Dpi { get; set; } = PrinterDpi.Dpi203;
    /// <summary>Whole-label orientation (^PO).</summary>
    public LabelOrientation Orientation { get; set; } = LabelOrientation.Normal;
    /// <summary>Margins; left/top define ^LH.</summary>
    public LabelMargins Margins { get; set; } = new();
    /// <summary>Media type (^MT).</summary>
    public MediaType MediaType { get; set; } = MediaType.ThermalTransfer;
    /// <summary>Media tracking (^MN).</summary>
    public MediaTracking MediaTracking { get; set; } = MediaTracking.Web;
    /// <summary>Default field rotation (^FW); elements with a null rotation inherit it.</summary>
    public FieldRotation DefaultRotation { get; set; } = FieldRotation.Normal;
    /// <summary>Default font (^CF) or null to leave the printer default.</summary>
    public FontSpec? DefaultFont { get; set; } = new('0', 30, 30);
    /// <summary>Character encoding (^CI); UTF-8 (^CI28) by default.</summary>
    public ZplEncoding Encoding { get; set; } = ZplEncoding.Utf8;
    /// <summary>
    /// Id of the stock-library entry (<c>Rotula.Core.Stocks.StockEntry.Id</c>) this label was sized from, or null when
    /// the size was typed in by hand. It is informational only and never affects the ZPL output.
    /// </summary>
    public string? StockId { get; set; }
    /// <summary>Print quantity options (^PQ).</summary>
    public PrintOptions Print { get; set; } = new();
    // ---- printer settings that are off (null / 0 / false) unless the design asks for them, so nothing extra is written by default.

    /// <summary>Shifts every field to the left by this many dots (ZPL <c>^LS</c>, -9999 to 9999); 0 writes nothing.</summary>
    public int LabelShift { get; set; }
    /// <summary>Moves the whole format down (positive) or up (negative) by this many dot rows (ZPL <c>^LT</c>, -240 to 240); 0 writes nothing.</summary>
    public int LabelTop { get; set; }
    /// <summary>What the printer does after printing (ZPL <c>^MM</c>), or null to leave it alone.</summary>
    public PrintMode? PrintMode { get; set; }
    /// <summary>Peel-off only: start peeling before the whole label is printed (the second <c>^MM</c> parameter).</summary>
    public bool PrePeel { get; set; }
    /// <summary>Print the whole label as a mirror image (ZPL <c>^PM</c>).</summary>
    public bool MirrorImage { get; set; }
    /// <summary>Print, slew and backfeed speed (ZPL <c>^PR</c>), or null to leave them alone.</summary>
    public PrintRateOptions? PrintRate { get; set; }
    /// <summary>Darkness relative to the printer's current setting (ZPL <c>^MD</c>, -30 to 30, decimals allowed), or null to leave it alone.</summary>
    public decimal? MediaDarkness { get; set; }
    /// <summary>Font ids assigned to stored font files (ZPL <c>^CW</c>), written before the fields.</summary>
    public List<FontAlias> FontAliases { get; } = new();
    /// <summary>Graphics the file downloads (ZPL <c>~DG</c>), written before the label; recalls in the label can then be previewed.</summary>
    public List<StoredGraphic> StoredGraphics { get; } = new();

    /// <summary>Declared variables with sample values.</summary>
    public List<LabelVariable> Variables { get; } = new();
    /// <summary>Elements in z-order / emission order.</summary>
    public List<LabelElement> Elements { get; } = new();

    /// <summary>Label width in dots at <see cref="Dpi"/>.</summary>
    public int WidthDots => Width.ToDots(Dpi);
    /// <summary>Label height in dots at <see cref="Dpi"/>.</summary>
    public int HeightDots => Height.ToDots(Dpi);
}
