using System.Globalization;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Core.Barcodes;

/// <summary>
/// The quiet zone (clear margin) a symbology needs around its symbol, in modules (one module is one X-dimension), and where the
/// requirement comes from. A reader needs this margin to find the start and end of the symbol; printing something inside it, or running
/// the label edge through it, is the most common reason a good-looking barcode does not scan.
/// </summary>
/// <param name="LeftModules">Margin before the first bar, in modules, along the reading direction.</param>
/// <param name="RightModules">Margin after the last bar, in modules, along the reading direction.</param>
/// <param name="AllSides">True for 2D and stacked symbols, which need the margin on all four sides (then left/right are the value for each side).</param>
/// <param name="Source">The standard or specification the value comes from (plain text, for tool tips and documentation).</param>
public sealed record BarcodeQuietZone(int LeftModules, int RightModules, bool AllSides, string Source);

/// <summary>The quiet zone around a barcode in dots, for each side of the label orientation (what the design canvas draws).</summary>
/// <param name="Left">Dots to the left of the symbol.</param>
/// <param name="Top">Dots above the symbol.</param>
/// <param name="Right">Dots to the right of the symbol.</param>
/// <param name="Bottom">Dots below the symbol.</param>
public readonly record struct QuietZoneDots(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Everything a person needs to judge the size of a barcode on paper: the X-dimension, the bar widths, the printed width of the symbol
/// and the quiet zone, all derived from the dot-based settings and the label resolution.
/// </summary>
/// <param name="Dpi">Printer resolution the figures are for.</param>
/// <param name="ModuleDots">Module (narrow bar) width in dots.</param>
/// <param name="XDimensionMils">X-dimension in mils (1/1000 inch).</param>
/// <param name="XDimensionMm">X-dimension in millimetres.</param>
/// <param name="NarrowBarDots">Narrow bar width in dots (the same as <see cref="ModuleDots"/>).</param>
/// <param name="WideBarDots">Wide bar width in dots, or null when the symbology has no separate wide bar (Code 128, EAN/UPC, 2D codes).</param>
/// <param name="SymbolWidthDots">Printed width of the symbol without quiet zones in dots, or null when it cannot be known without encoding.</param>
/// <param name="SymbolWidthMm">The same in millimetres, or null.</param>
/// <param name="QuietZone">The quiet zone the symbology needs.</param>
/// <param name="QuietLeftDots">Quiet zone before the symbol in dots.</param>
/// <param name="QuietRightDots">Quiet zone after the symbol in dots.</param>
/// <param name="QuietLeftMm">Quiet zone before the symbol in millimetres.</param>
/// <param name="QuietRightMm">Quiet zone after the symbol in millimetres.</param>
public sealed record BarcodeSizeReport(
    int Dpi,
    int ModuleDots,
    double XDimensionMils,
    double XDimensionMm,
    int NarrowBarDots,
    int? WideBarDots,
    int? SymbolWidthDots,
    double? SymbolWidthMm,
    BarcodeQuietZone QuietZone,
    int QuietLeftDots,
    int QuietRightDots,
    double QuietLeftMm,
    double QuietRightMm)
{
    /// <summary>Symbol plus both quiet zones in dots, or null when the symbol width is not known.</summary>
    public int? TotalWidthDots => SymbolWidthDots is { } w ? w + QuietLeftDots + QuietRightDots : null;
}

/// <summary>
/// Whether a module (narrow bar) width is big enough to scan at a given printer resolution, as judged by
/// <see cref="BarcodeSizing.ModuleSafetyOf(BarcodeSymbology, int, int, bool)"/>. Used before printing through the Windows driver, where the label is drawn as a picture and
/// the printer's own dot pattern is no longer under the designer's control.
/// </summary>
public enum ModuleSafety
{
    /// <summary>The module is at least as wide as the symbology's minimum X-dimension.</summary>
    Safe,
    /// <summary>The module is two or more dots wide but narrower than the symbology's minimum X-dimension (typical on high-resolution office printers).</summary>
    BelowMinimum,
    /// <summary>The module is a single device dot: no tolerance for dot gain or head wear, whatever the symbology.</summary>
    SingleDot,
}

/// <summary>
/// Pure sizing helper for barcodes: turns the dot-based module width and the label resolution into the figures printers and
/// scanner specifications use (X-dimension in mils and mm), measures the symbol where the width follows from the symbology rules,
/// and checks the printer reality (is the module big enough to print and scan) and the quiet zone (does the symbol fit on the label).
/// It carries no UI text: warnings are <see cref="ValidationIssue"/>s with message keys the app localizes.
/// </summary>
public static class BarcodeSizing
{
    /// <summary>Warning key: the module is one dot at 300 dpi or more.</summary>
    public const string OneDotKey = "Barcode.Size.OneDotModule";
    /// <summary>Warning key: a 1D narrow bar thinner than two dots at 203 dpi.</summary>
    public const string ThinBarKey = "Barcode.Size.ThinBar";
    /// <summary>Warning key: the symbol with its quiet zones does not fit the label width.</summary>
    public const string QuietZoneKey = "Barcode.Size.QuietZoneOutside";

    // Scan-reliability thresholds of the printer reality check. A 203 dpi head prints a dot about 0.125 mm wide; a bar of one dot has
    // no tolerance for dot gain or a slightly worn head, which is why bar code printer guides advise at least two dots for linear codes.
    private const int MinNarrowDots203 = 2;
    private const int HighDpi = 300;

    /// <summary>
    /// Quiet zone per symbology. Values are in modules (X). Sources: Code 128 ISO/IEC 15417 (10X, also GS1-128 in the GS1 General
    /// Specifications); Code 39 ISO/IEC 16388 (10X); Interleaved 2 of 5 ISO/IEC 16390 (10X, also ITF-14 in GS1); EAN-13 11X left and 7X
    /// right, UPC-A 9X on both sides, EAN-8 7X on both sides, UPC-E 9X left and 7X right, add-ons 7X left and 5X right (GS1 General
    /// Specifications, ISO/IEC 15420); QR Code 4X (ISO/IEC 18004); Data Matrix 1X (ISO/IEC 16022); PDF417 2X (ISO/IEC 15438);
    /// MicroPDF417 1X (ISO/IEC 24728); Aztec Code needs none (ISO/IEC 24778). Code 93 and Codabar use 10X (AIM uniform symbology
    /// specifications). Symbologies without an entry fall back to a conservative value by family (see <see cref="QuietZoneOf"/>).
    /// </summary>
    private static readonly Dictionary<BarcodeSymbology, BarcodeQuietZone> QuietZones = new()
    {
        [BarcodeSymbology.Code128] = new(10, 10, false, "ISO/IEC 15417"),
        [BarcodeSymbology.Code39] = new(10, 10, false, "ISO/IEC 16388"),
        [BarcodeSymbology.Logmars] = new(10, 10, false, "ISO/IEC 16388, MIL-STD-1189"),
        [BarcodeSymbology.Code93] = new(10, 10, false, "AIM USS Code 93"),
        [BarcodeSymbology.Interleaved2Of5] = new(10, 10, false, "ISO/IEC 16390"),
        [BarcodeSymbology.Codabar] = new(10, 10, false, "AIM USS Codabar"),
        [BarcodeSymbology.Ean13] = new(11, 7, false, "GS1 General Specifications"),
        [BarcodeSymbology.UpcA] = new(9, 9, false, "GS1 General Specifications"),
        [BarcodeSymbology.Ean8] = new(7, 7, false, "GS1 General Specifications"),
        [BarcodeSymbology.UpcE] = new(9, 7, false, "GS1 General Specifications"),
        [BarcodeSymbology.UpcEanExtension] = new(7, 5, false, "GS1 General Specifications"),
        [BarcodeSymbology.QrCode] = new(4, 4, true, "ISO/IEC 18004"),
        [BarcodeSymbology.DataMatrix] = new(1, 1, true, "ISO/IEC 16022"),
        [BarcodeSymbology.Pdf417] = new(2, 2, true, "ISO/IEC 15438"),
        [BarcodeSymbology.MicroPdf417] = new(1, 1, true, "ISO/IEC 24728"),
        [BarcodeSymbology.Aztec] = new(0, 0, true, "ISO/IEC 24778"),
    };

    /// <summary>
    /// The quiet zone of <paramref name="symbology"/>. A symbology with no entry in the table gets a conservative family default
    /// (linear 10X, matrix 1X, stacked 2X, postal none); that default is a safe margin, not a citation of a standard.
    /// </summary>
    public static BarcodeQuietZone QuietZoneOf(BarcodeSymbology symbology)
    {
        if (QuietZones.TryGetValue(symbology, out var known)) return known;
        var category = Enum.IsDefined(symbology) ? BarcodeCatalog.Get(symbology).Category : BarcodeCategory.Linear;
        return category switch
        {
            BarcodeCategory.Matrix2D => new BarcodeQuietZone(1, 1, true, "default"),
            BarcodeCategory.Stacked => new BarcodeQuietZone(2, 2, true, "default"),
            BarcodeCategory.Postal => new BarcodeQuietZone(0, 0, false, "default"),
            _ => new BarcodeQuietZone(10, 10, false, "default"),
        };
    }

    /// <summary>Millimetres per mil (1/1000 inch): 0.0254.</summary>
    private const double MmPerMil = 0.0254;

    /// <summary>Converts the millimetre figure a specification states to mils, so the table keeps the standard's own number instead of a rounded inch value.</summary>
    private static double MilsFromMm(double mm) => mm / MmPerMil;

    /// <summary>
    /// Smallest module (X-dimension) per symbology, in mils (1/1000 inch), with the document it comes from. Each figure is the hard floor the
    /// cited specification gives for ordinary on-demand (thermal or laser) label printing, in millimetres converted exactly; going below it is
    /// outside what the standard accepts, so a scanner can no longer be expected to read the symbol. It is a floor, not the recommended size:
    /// the GS1 target sizes are larger. Only the symbologies that have a citable figure are listed here; every other symbology gets a labelled
    /// family fallback from <see cref="MinimumModuleOf"/>.
    ///
    /// Sources (GS1 General Specifications are quoted from the copies that could be read: Version 14 of Jan 2014, GSCN 16-434 effective
    /// 20 Dec 2016 and GSCN 22-031 ratified Jul 2022, which becomes Release 23; later releases may move the figures):
    /// - EAN-13, EAN-8, UPC-A, UPC-E, add-ons, GS1 DataBar: symbol specification table 1 (v14 Figure 5.5.2.7-2, Release 23 section 5.12.3.1)
    ///   gives a minimum X of 0.264 mm (0.0104 in, 80 percent magnification) and, in note (*), allows 0.249 mm (0.0098 in, 75 percent)
    ///   for on-demand printing (thermal, laser), which is the case here; the 0.249 mm figure is used.
    /// - Code 128 and Interleaved 2 of 5: GS1-128 and ITF-14 tables 2 and 5 (v14 Figures 5.5.2.7-3 and 5.5.2.7-6, as revised by GSCN 16-434
    ///   in 5.5.2.7.2 note (*) and 5.5.2.7.5 note (*)): the table minimum is 0.495 mm (0.0195 in) but when the symbol is physically too small
    ///   the X-dimension "SHALL NOT be less than 0.250 mm (0.0098 in)". That absolute floor is used. ISO/IEC 15417 and ISO/IEC 16390 state no
    ///   minimum of their own, so a plain Code 128 or Interleaved 2 of 5 label gets the same floor by analogy.
    /// - QR Code is not in this table, because its figure depends on the content (see <see cref="MinimumModuleOf"/>): a GS1 QR Code (a GS1
    ///   Digital Link URI) takes GS1 symbol specification table 1 (Release 23 section 5.12.3.1, GSCN 22-031): 0.375 mm (0.0148 in); the
    ///   addenda for retail URLs (AI 8200) use 0.396 mm. Any other QR Code has no citable figure: ISO/IEC 18004 states no minimum module size,
    ///   so it gets the matrix-2D family fallback.
    /// - Data Matrix: GS1 symbol specification tables 6 (regulated healthcare non-retail) and 7 (direct part marking), v14 Figures 5.5.2.7-7
    ///   and 5.5.2.7-8: 0.255 mm (0.0100 in). The retail tables ask 0.375 to 0.396 mm. ISO/IEC 16022 states no minimum.
    /// - Code 39: AIM Uniform Symbology Specification Code 39 (USS-39) states a minimum standard X-dimension of 0.0075 in (0.191 mm); ISO/IEC
    ///   16388 itself could not be read. The clause number of USS-39 was not retrievable, only the sentence quoting it.
    /// - LOGMARS is Code 39 with a mandatory check character (MIL-STD-1189), so it takes the Code 39 figure.
    /// - PDF417: 0.01 in (0.254 mm) minimum narrow element, as quoted from the US DLA DLMS manual 0206.35 in a search excerpt (the document
    ///   itself could not be opened), ISO/IEC 15438 is paywalled and states no figure in the readable previews.
    /// - POSTNET: USPS DMM archive (Aug 2017) 204, section 1.2.5: bars are 0.020 in plus or minus 0.005 in wide, so the narrowest allowed bar
    ///   is 0.015 in (0.381 mm). Applies to the bar width, which is the module of this symbology.
    /// </summary>
    private static readonly Dictionary<BarcodeSymbology, (double Mils, string Source)> MinimumModules = new()
    {
        [BarcodeSymbology.Code128] = (MilsFromMm(0.250), "GS1 General Specifications 5.5.2.7.2 and 5.5.2.7.5 note (*), GSCN 16-434 (GS1-128 absolute minimum X-dimension 0.250 mm); ISO/IEC 15417 states none"),
        [BarcodeSymbology.Interleaved2Of5] = (MilsFromMm(0.250), "GS1 General Specifications 5.5.2.7.2 note (*), GSCN 16-434 (ITF-14 absolute minimum X-dimension 0.250 mm); ISO/IEC 16390 states none"),
        [BarcodeSymbology.Ean13] = (MilsFromMm(0.249), "GS1 General Specifications symbol specification table 1 note (*): 0.249 mm (75 percent) for on-demand printing, 0.264 mm otherwise"),
        [BarcodeSymbology.UpcA] = (MilsFromMm(0.249), "GS1 General Specifications symbol specification table 1 note (*): 0.249 mm (75 percent) for on-demand printing, 0.264 mm otherwise"),
        [BarcodeSymbology.Ean8] = (MilsFromMm(0.249), "GS1 General Specifications symbol specification table 1 note (*): 0.249 mm (75 percent) for on-demand printing, 0.264 mm otherwise"),
        [BarcodeSymbology.UpcE] = (MilsFromMm(0.249), "GS1 General Specifications symbol specification table 1 note (*): 0.249 mm (75 percent) for on-demand printing, 0.264 mm otherwise"),
        [BarcodeSymbology.UpcEanExtension] = (MilsFromMm(0.249), "GS1 General Specifications symbol specification table 1 (EAN/UPC plus add-on rows) note (*): 0.249 mm (75 percent) for on-demand printing"),
        [BarcodeSymbology.DataBar] = (MilsFromMm(0.249), "GS1 General Specifications symbol specification table 1 (GS1 DataBar rows) note (*): 0.249 mm (75 percent) for on-demand printing, 0.264 mm otherwise"),
        [BarcodeSymbology.DataMatrix] = (MilsFromMm(0.255), "GS1 General Specifications symbol specification tables 6 and 7 (v14 5.5.2.7): GS1 DataMatrix minimum X-dimension 0.255 mm; ISO/IEC 16022 states none"),
        [BarcodeSymbology.Code39] = (7.5, "AIM Uniform Symbology Specification Code 39 (USS-39): minimum standard X-dimension 0.0075 in (0.191 mm), clause not retrieved; ISO/IEC 16388 not readable"),
        [BarcodeSymbology.Logmars] = (7.5, "Code 39 variant (MIL-STD-1189), takes the AIM USS-39 minimum X-dimension 0.0075 in (0.191 mm)"),
        [BarcodeSymbology.Pdf417] = (MilsFromMm(0.254), "US DLA DLMS manual 0206.35: minimum narrow element X-dimension 0.01 in (0.254 mm), read from a search excerpt; ISO/IEC 15438 not readable"),
        [BarcodeSymbology.Postnet] = (15.0, "USPS DMM 204 section 1.2.5 (archive Aug 2017): bar width 0.020 +/- 0.005 in, narrowest allowed bar 0.015 in"),
    };

    /// <summary>
    /// GS1 QR Code, the form a GS1 Digital Link URI takes in a QR Code: GS1 General Specifications symbol specification table 1 (Release 23
    /// section 5.12.3.1, GSCN 22-031), minimum X-dimension 0.375 mm. Used only for QR Codes whose content is a GS1 Digital Link
    /// (<see cref="HasGs1DigitalLinkContent"/>): GS1 sets this figure for its own symbols, not for every QR Code.
    /// </summary>
    private static readonly (double Mils, string Source) Gs1QrMinimum =
        (MilsFromMm(0.375), "GS1 General Specifications 5.12.3.1 symbol specification table 1 (GSCN 22-031): GS1 QR Code (GS1 Digital Link URI) minimum X-dimension 0.375 mm");

    /// <summary>
    /// Any other QR Code. ISO/IEC 18004 states no minimum module size, so it takes the matrix-2D family fallback, the GS1 DataMatrix figure of
    /// 0.255 mm, labelled "Fallback" like every other family default. Settled before the first release of picture printing because the minimum
    /// is an argument of the pre-flight warning, and so part of the acknowledgement fingerprint: the stricter GS1 figure made every 4-dot QR
    /// Code at 300 dpi (13.3 mils, a common, scannable size) ask for a tick.
    /// </summary>
    private static readonly (double Mils, string Source) GenericQrMinimum =
        (MilsFromMm(0.255), "Fallback (matrix 2D family): GS1 DataMatrix minimum X-dimension 0.255 mm, because ISO/IEC 18004 states no minimum module size for QR Code; GS1 Digital Link content takes the GS1 QR Code 0.375 mm");

    /// <summary>
    /// The minimum module and its source for <paramref name="symbology"/>: the table entry when the symbology has a citable figure, otherwise a
    /// family fallback chosen by <see cref="BarcodeCategory"/>. QR Code depends on its content: <paramref name="gs1DigitalLink"/> selects the
    /// GS1 QR Code figure, otherwise a generic QR gets the matrix-2D fallback with a source that names ISO/IEC 18004. For every other symbology
    /// <paramref name="gs1DigitalLink"/> changes nothing: Data Matrix has one GS1 figure, and the others have no Digital Link form.
    /// Every fallback is labelled "Fallback" in its source text and is a documented value of a related symbology, never an invented number:
    /// linear 7.5 mils (the AIM USS-39 Code 39 figure; Code 93 and Codabar are quoted at 7.5 mils by secondary sources only, and Code 11, MSI,
    /// Plessey and the 2 of 5 variants have no figure that could be read);
    /// matrix 2D the GS1 DataMatrix 0.255 mm (Aztec Code has no figure that could be read, and a MaxiCode symbol has a fixed size set by ISO/IEC
    /// 16023, so it never has an adjustable module);
    /// stacked the PDF417 0.254 mm (MicroPDF417, Codablock, Code 49 and TLC39 have no figure that could be read);
    /// postal the POSTNET 0.015 in (PLANET uses the same bar dimensions in USPS practice, but Publication 25 could not be read).
    /// </summary>
    private static (double Mils, string Source) MinimumModuleOf(BarcodeSymbology symbology, bool gs1DigitalLink)
    {
        if (symbology == BarcodeSymbology.QrCode) return gs1DigitalLink ? Gs1QrMinimum : GenericQrMinimum;
        if (MinimumModules.TryGetValue(symbology, out var known)) return known;
        var category = Enum.IsDefined(symbology) ? BarcodeCatalog.Get(symbology).Category : BarcodeCategory.Linear;
        return category switch
        {
            BarcodeCategory.Matrix2D => (MilsFromMm(0.255), "Fallback (matrix 2D family): GS1 DataMatrix minimum X-dimension 0.255 mm; no figure found for this symbology"),
            BarcodeCategory.Stacked => (MilsFromMm(0.254), "Fallback (stacked family): PDF417 minimum X-dimension 0.254 mm; no figure found for this symbology"),
            BarcodeCategory.Postal => (15.0, "Fallback (postal family): POSTNET narrowest bar 0.015 in; no figure found for this symbology"),
            _ => (7.5, "Fallback (linear family): AIM USS-39 Code 39 minimum X-dimension 7.5 mils; no figure found for this symbology"),
        };
    }

    /// <summary>
    /// The smallest module (X-dimension) in mils (1/1000 inch) at which <paramref name="symbology"/> can still be expected to scan, for generic
    /// content (a QR Code that is not a GS1 Digital Link). A symbology without its own citable figure gets a family fallback (see
    /// <see cref="MinimumModuleSourceOf(BarcodeSymbology)"/> for which).
    /// </summary>
    public static double MinimumModuleMils(BarcodeSymbology symbology) => MinimumModuleOf(symbology, gs1DigitalLink: false).Mils;

    /// <summary>
    /// The smallest module in mils for <paramref name="symbology"/> carrying the given kind of content. Only QR Code differs: a GS1 Digital
    /// Link takes the GS1 QR Code figure (0.375 mm), any other content the matrix-2D fallback (0.255 mm).
    /// </summary>
    /// <param name="symbology">The symbology.</param>
    /// <param name="gs1DigitalLink">True when the content is a GS1 Digital Link URI (see <see cref="HasGs1DigitalLinkContent"/>).</param>
    public static double MinimumModuleMils(BarcodeSymbology symbology, bool gs1DigitalLink) => MinimumModuleOf(symbology, gs1DigitalLink).Mils;

    /// <summary>
    /// The standard or specification behind <see cref="MinimumModuleMils(BarcodeSymbology)"/> (plain text, English), or a "Fallback" note when it is
    /// a family default.
    /// </summary>
    public static string MinimumModuleSourceOf(BarcodeSymbology symbology) => MinimumModuleOf(symbology, gs1DigitalLink: false).Source;

    /// <summary>The source behind <see cref="MinimumModuleMils(BarcodeSymbology, bool)"/> for the given kind of content (plain text, English).</summary>
    /// <param name="symbology">The symbology.</param>
    /// <param name="gs1DigitalLink">True when the content is a GS1 Digital Link URI.</param>
    public static string MinimumModuleSourceOf(BarcodeSymbology symbology, bool gs1DigitalLink) => MinimumModuleOf(symbology, gs1DigitalLink).Source;

    /// <summary>
    /// True when <paramref name="barcode"/> carries GS1 Digital Link content: a QR Code or Data Matrix with the Digital Link option (its data is
    /// checked as a GS1 Digital Link URI), or a QR Code made with the content builder's GS1 Digital Link kind (the builder does not set the
    /// option, so both are read). A content definition that cannot be read counts as generic content.
    /// </summary>
    /// <param name="barcode">The barcode.</param>
    /// <exception cref="ArgumentNullException"><paramref name="barcode"/> is null.</exception>
    public static bool HasGs1DigitalLinkContent(BarcodeElement barcode)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        if (barcode.Symbology is not (BarcodeSymbology.QrCode or BarcodeSymbology.DataMatrix)) return false;
        if (barcode.DigitalLink) return true;
        return barcode.Symbology == BarcodeSymbology.QrCode
            && QrContent.QrContentSpec.TryParse(barcode.QrContentSpec, out var spec)
            && spec.Kind == QrContent.QrContentKind.GS1DigitalLink;
    }

    /// <summary>
    /// Judges a module of <paramref name="moduleDots"/> device dots at <paramref name="dpi"/> for generic content (see
    /// <see cref="ModuleSafetyOf(BarcodeSymbology, int, int, bool)"/>).
    /// </summary>
    /// <param name="symbology">The symbology (its minimum comes from <see cref="MinimumModuleMils(BarcodeSymbology)"/>).</param>
    /// <param name="moduleDots">Module (narrow bar) width in device dots.</param>
    /// <param name="dpi">Resolution of the printer the label will be printed on.</param>
    public static ModuleSafety ModuleSafetyOf(BarcodeSymbology symbology, int moduleDots, int dpi) => ModuleSafetyOf(symbology, moduleDots, dpi, gs1DigitalLink: false);

    /// <summary>
    /// Judges a module of <paramref name="moduleDots"/> device dots at <paramref name="dpi"/>. The check is on the physical width in mils, not on the
    /// dot count: a 2-dot module is plenty at 203 dpi (9.9 mils) but only 1.7 mils at 1200 dpi, where no reader will resolve it.
    /// A single dot is reported on its own because it has no tolerance for dot gain at any resolution. The minimum is the same one
    /// <see cref="MinimumModuleMils(BarcodeSymbology, bool)"/> returns for this content, so a caller that shows the minimum next to the verdict
    /// shows the figure the verdict was made against.
    /// </summary>
    /// <param name="symbology">The symbology.</param>
    /// <param name="moduleDots">Module (narrow bar) width in device dots.</param>
    /// <param name="dpi">Resolution of the printer the label will be printed on.</param>
    /// <param name="gs1DigitalLink">True when the content is a GS1 Digital Link URI (only QR Code has a different minimum then).</param>
    public static ModuleSafety ModuleSafetyOf(BarcodeSymbology symbology, int moduleDots, int dpi, bool gs1DigitalLink)
    {
        // Validate first so a bad resolution is never hidden by the single-dot shortcut.
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        if (moduleDots <= 1) return ModuleSafety.SingleDot;
        // The tolerance keeps a module that lands exactly on the minimum from being rejected by floating point noise.
        return MilsOf(moduleDots, dpi) + 1e-9 < MinimumModuleMils(symbology, gs1DigitalLink) ? ModuleSafety.BelowMinimum : ModuleSafety.Safe;
    }

    /// <summary>
    /// The module of the symbol in dots: the ^BY width for symbologies that have <see cref="BarcodeOption.ModuleWidth"/>, the magnification for
    /// those with <see cref="BarcodeOption.ModuleSize"/>, otherwise null (MaxiCode has a fixed size).
    /// </summary>
    public static int? ModuleDotsOf(BarcodeElement b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (!Enum.IsDefined(b.Symbology)) return null;
        var info = BarcodeCatalog.Get(b.Symbology);
        if (info.Supports(BarcodeOption.ModuleWidth)) return b.ModuleWidth;
        if (info.Supports(BarcodeOption.ModuleSize)) return b.ModuleSize;
        return null;
    }

    /// <summary>X-dimension in mils (1/1000 inch) of a module of <paramref name="moduleDots"/> dots at <paramref name="dpi"/>.</summary>
    public static double MilsOf(int moduleDots, int dpi)
    {
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        return moduleDots * 1000.0 / dpi;
    }

    /// <summary>Length in millimetres of <paramref name="dots"/> dots at <paramref name="dpi"/> (the X-dimension when given the module width).</summary>
    public static double MmOf(double dots, int dpi)
    {
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        return dots * 25.4 / dpi;
    }

    /// <summary>
    /// Describes the size of <paramref name="b"/> printing <paramref name="data"/> (variables already resolved) at <paramref name="dpi"/>.
    /// Returns null for a symbology without an adjustable module. The symbol width is measured here for the symbologies whose width follows
    /// from their rules (Code 128, Code 39, Code 93, Interleaved 2 of 5, EAN/UPC, QR Code); for the others the caller may pass the module
    /// count from the preview encoder as <paramref name="encodedModules"/>, otherwise the width stays null and nothing is claimed about it.
    /// </summary>
    /// <param name="b">The barcode.</param>
    /// <param name="data">The data it prints.</param>
    /// <param name="dpi">Printer resolution (203, 300 or 600).</param>
    /// <param name="encodedModules">Symbol width in modules from an encoder, used only when this class cannot measure the symbol itself.</param>
    public static BarcodeSizeReport? Analyze(BarcodeElement b, string data, int dpi, int? encodedModules = null)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(data);
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        if (ModuleDotsOf(b) is not { } module) return null;
        module = Math.Max(1, module);

        var info = BarcodeCatalog.Get(b.Symbology);
        // ZPL derives the wide bar from ^BY: wide = narrow x ratio, rounded to whole dots, and never thinner than the narrow bar.
        int? wide = info.Supports(BarcodeOption.WideBarRatio)
            ? Math.Max(module, (int)Math.Round(module * b.WideBarRatio, MidpointRounding.AwayFromZero))
            : null;

        int? width = MeasureDots(b, data, module, wide) ?? (encodedModules is { } m and > 0 ? m * module : null);
        var q = QuietZoneOf(b.Symbology);
        int left = q.LeftModules * module, right = q.RightModules * module;
        return new BarcodeSizeReport(dpi, module, MilsOf(module, dpi), MmOf(module, dpi), module, wide,
            width, width is { } w ? MmOf(w, dpi) : null, q, left, right, MmOf(left, dpi), MmOf(right, dpi));
    }

    /// <summary>
    /// Printed width of the symbol in dots where the symbology's own rules fix it, null otherwise. Each formula counts the elements of the
    /// symbology (see the comments); the ones with wide bars use the wide width in dots, as ZPL does.
    /// </summary>
    private static int? MeasureDots(BarcodeElement b, string data, int n, int? wide)
    {
        int w = wide ?? n;
        switch (b.Symbology)
        {
            case BarcodeSymbology.Code128:
            {
                // Only the automatic mode is measured: the other modes change the character sets the printer starts in. GS1-128 data
                // (parentheses) is turned into an FNC1 sequence by the printer, so its length is not data.Length.
                if (b.Code128Mode != Code128Mode.Automatic || data.Contains('(')) return null;
                var values = Code128.Encode(data);
                // Every symbol character is 11 modules wide; the stop character is 13 (it ends with the 2-module termination bar).
                return values is null ? null : (11 * (values.Length - 1) + 13) * n;
            }
            case BarcodeSymbology.Code39 or BarcodeSymbology.Logmars:
            {
                // Start and stop '*' plus an optional mod 43 check character (LOGMARS always has it). A character is 9 elements, 3 of them wide,
                // so 6 narrow + 3 wide; one narrow inter-character gap sits between characters.
                int chars = data.Length + 2 + (b.CheckDigit || b.Symbology == BarcodeSymbology.Logmars ? 1 : 0);
                return chars * (6 * n + 3 * w) + (chars - 1) * n;
            }
            case BarcodeSymbology.Code93:
            {
                // Start, data, two check characters (C and K), stop: 9 modules each, plus the 1-module termination bar.
                return (data.Length + 4) * 9 * n + n;
            }
            case BarcodeSymbology.Interleaved2Of5:
            {
                // Digits are paired (5 bars of the first digit interleaved with 5 spaces of the second): 10 elements, 4 wide and 6 narrow.
                // Start pattern is 4 narrow elements, stop is wide bar + 2 narrow. The printer's check digit counts as a digit.
                int digits = data.Length + (b.CheckDigit ? 1 : 0);
                if (digits % 2 != 0) return null;
                return 4 * n + digits / 2 * (6 * n + 4 * w) + (w + 2 * n);
            }
            // The EAN/UPC family has a fixed number of modules whatever the data.
            case BarcodeSymbology.Ean13 or BarcodeSymbology.UpcA: return 95 * n;
            case BarcodeSymbology.Ean8: return 67 * n;
            case BarcodeSymbology.UpcE: return 51 * n;
            case BarcodeSymbology.QrCode:
            {
                if (b.QrModel != 2 || data.Length == 0) return null;
                var fit = QrSizing.Analyze(data, b.QrErrorCorrection);
                return fit.Fits ? fit.Modules * n : null;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The quiet zone around the symbol in dots, per side of the label, for a field printed with the effective rotation
    /// <paramref name="rotation"/>. A linear symbol needs the margin only along its reading direction: a symbol rotated 90 degrees reads top
    /// to bottom, so its margins are above and below. 2D and stacked symbols need it on all four sides.
    /// </summary>
    public static QuietZoneDots QuietZoneBox(BarcodeElement b, FieldRotation rotation)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (ModuleDotsOf(b) is not { } module) return default;
        module = Math.Max(1, module);
        var q = QuietZoneOf(b.Symbology);
        int start = q.LeftModules * module, end = q.RightModules * module;
        if (q.AllSides) return new QuietZoneDots(start, start, end, end);
        return rotation switch
        {
            FieldRotation.Normal => new QuietZoneDots(start, 0, end, 0),
            FieldRotation.Inverted180 => new QuietZoneDots(end, 0, start, 0),
            // Rotated 90 degrees clockwise the start of the symbol is at the top; rotated 270 it is at the bottom.
            FieldRotation.Rotated90 => new QuietZoneDots(0, start, 0, end),
            _ => new QuietZoneDots(0, end, 0, start),
        };
    }

    /// <summary>
    /// Checks the printer reality and the quiet zone of one barcode. All findings are warnings: the label still prints, it may just not scan.
    /// </summary>
    /// <param name="b">The barcode.</param>
    /// <param name="data">Its data, variables resolved.</param>
    /// <param name="doc">The label (resolution, width and default rotation).</param>
    public static List<ValidationIssue> Check(BarcodeElement b, string data, LabelDocument doc)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(doc);
        var issues = new List<ValidationIssue>();
        int dpi = (int)doc.Dpi;
        // An out-of-range module is already an error from BarcodeValidator; do not pile a warning on top of it.
        if (ModuleDotsOf(b) is not { } moduleDots || moduleDots < 1 || moduleDots > 10) return issues;
        var report = Analyze(b, data, dpi);
        if (report is null) return issues;
        var info = BarcodeCatalog.Get(b.Symbology);

        string dots = moduleDots.ToString(CultureInfo.InvariantCulture);
        string mils = report.XDimensionMils.ToString("0.#", CultureInfo.InvariantCulture);
        string mm = report.XDimensionMm.ToString("0.###", CultureInfo.InvariantCulture);
        string dpiText = dpi.ToString(CultureInfo.InvariantCulture);

        if (moduleDots == 1 && dpi >= HighDpi)
            // At 300 dpi and above one dot is 0.085 mm or less: below what most scanners resolve, and any dot gain closes the gaps.
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, OneDotKey, b.Id, dots, mils, mm, dpiText));
        else if (info.Category == BarcodeCategory.Linear && dpi < HighDpi && moduleDots < MinNarrowDots203)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, ThinBarKey, b.Id, dots, mils, mm, dpiText));

        // Quiet-zone fit: only along the label width and only when the symbol is printed upright (its position is then the left edge); a
        // rotated field's extent depends on the origin rules of the rotation and is left to the canvas guide.
        var rotation = b.Rotation ?? doc.DefaultRotation;
        if (rotation == FieldRotation.Normal && report.SymbolWidthDots is { } symbol && b.X >= 0 && b.X <= 32000)
        {
            int from = b.X - report.QuietLeftDots, to = b.X + symbol + report.QuietRightDots;
            if (from < 0 || to > doc.WidthDots)
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, QuietZoneKey, b.Id,
                    MmOf(from, dpi).ToString("0.#", CultureInfo.InvariantCulture),
                    MmOf(to, dpi).ToString("0.#", CultureInfo.InvariantCulture),
                    MmOf(doc.WidthDots, dpi).ToString("0.#", CultureInfo.InvariantCulture)));
        }
        return issues;
    }
}
