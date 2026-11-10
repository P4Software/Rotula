using System.Globalization;
using Rotula.App.Localization;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Rendering.Barcodes;

namespace Rotula.App.ViewModels;

/// <summary>
/// Turns the sizing figures of <see cref="BarcodeSizing"/> into the plain-language lines under the module setting of a barcode:
/// "Module: 7.5 mil (0.19 mm) - symbol 48.3 mm wide" and the quiet zone note. No arithmetic lives here; this class only chooses the
/// wording and, where the Core rules cannot measure a symbol (Data Matrix, PDF417, ...), asks the preview encoder for its module count.
/// </summary>
public static class BarcodeSizeInfo
{
    /// <summary>
    /// The size report for <paramref name="b"/> at the label's resolution, or null when the symbology has no adjustable module.
    /// <paramref name="data"/> is the text the symbol prints (the longest sample when the data comes from fields).
    /// </summary>
    public static BarcodeSizeReport? Report(BarcodeElement b, string? data, int dpi)
    {
        data ??= b.Data;
        var report = BarcodeSizing.Analyze(b, data, dpi);
        if (report is null || report.SymbolWidthDots is not null || data.Length == 0) return report;

        // Core could not measure it: use the preview encoder's module count where that is true to the printer. A symbol with wide bars
        // is drawn with the fixed ratio 3 (see LinearEncoders), so its count is only used when the element asks for the same ratio.
        var info = BarcodeCatalog.Get(b.Symbology);
        if (info.Supports(BarcodeOption.WideBarRatio) && Math.Abs(b.WideBarRatio - 3.0) > 1e-9) return report;
        var matrix = BarcodeEncoder.TryEncode(b.Symbology, data, new BarcodeEncodeOptions
        {
            QrErrorCorrection = b.QrErrorCorrection,
            Pdf417SecurityLevel = b.Pdf417SecurityLevel,
            Pdf417Truncate = b.Pdf417Truncate,
            CheckDigit = b.CheckDigit,
            Code11CheckDigits = b.Code11CheckDigitCount,
            MsiCheckDigit = b.MsiCheckDigit,
            MsiCheckDigitInText = b.MsiCheckDigitInText,
            CodabarStart = b.CodabarStart,
            CodabarStop = b.CodabarStop,
            DataBarType = b.DataBarType,
            DataBarSegmentsPerRow = b.DataBarSegmentsPerRow,
            DataBarSeparatorHeight = b.DataBarSeparatorHeight,
            AztecErrorControl = b.AztecErrorControl,
            Gs1 = b.DataMatrixGs1,
        }, out _);
        // Hexagonal and postal layouts are not a plain module grid, so their width in modules is not a width in dots.
        return matrix is { Layout: not (BarcodeRowLayout.Hexagonal or BarcodeRowLayout.Proportional) }
            ? BarcodeSizing.Analyze(b, data, dpi, matrix.Width)
            : report;
    }

    /// <summary>
    /// The rectangle (label dots) that holds the symbol and its quiet zone, for the dotted guide on the design canvas: the element's frame grown
    /// by the quiet zone on the sides that need it. Null when the symbology has no adjustable module (nothing to say about its margin).
    /// </summary>
    public static Editing.RectD? QuietZoneFrame(Editing.RectD frame, BarcodeElement b, FieldRotation defaultRotation)
    {
        if (BarcodeSizing.ModuleDotsOf(b) is null) return null;
        var q = BarcodeSizing.QuietZoneBox(b, b.Rotation ?? defaultRotation);
        return new Editing.RectD(frame.X - q.Left, frame.Y - q.Top, frame.Width + q.Left + q.Right, frame.Height + q.Top + q.Bottom);
    }

    /// <summary>The "Module: ..." line. <paramref name="withSymbol"/> adds the printed width of the symbol when it is known.</summary>
    public static string ReadoutLine(BarcodeSizeReport r, bool withSymbol = true)
    {
        string mil = r.XDimensionMils.ToString("0.#", CultureInfo.CurrentCulture);
        string mm = r.XDimensionMm.ToString("0.##", CultureInfo.CurrentCulture);
        return withSymbol && r.SymbolWidthMm is { } w
            ? Loc.Get("Prop.Size.Readout", mil, mm, w.ToString("0.#", CultureInfo.CurrentCulture))
            : Loc.Get("Prop.Size.ReadoutNoSymbol", mil, mm);
    }

    /// <summary>The quiet zone note: one value when both sides are equal, two when they differ (EAN-13), a short sentence when none is needed.</summary>
    public static string QuietZoneLine(BarcodeSizeReport r)
    {
        var q = r.QuietZone;
        if (q.LeftModules == 0 && q.RightModules == 0) return Loc.Get("Prop.Size.QuietNone");
        string left = r.QuietLeftMm.ToString("0.#", CultureInfo.CurrentCulture), right = r.QuietRightMm.ToString("0.#", CultureInfo.CurrentCulture);
        return q.LeftModules == q.RightModules
            ? Loc.Get("Prop.Size.QuietBoth", left, q.LeftModules)
            : Loc.Get("Prop.Size.QuietLeftRight", left, right, q.LeftModules, q.RightModules);
    }

    /// <summary>
    /// The second line of the module row: readout plus quiet zone note, in the warning colour when the printer reality check complains about
    /// the module. The wording of that complaint itself belongs to the problems list, so it is not repeated here.
    /// </summary>
    public static RowDetail? Describe(BarcodeElement b, string? data, LabelDocument doc, bool withSymbol = true)
    {
        var report = Report(b, data, (int)doc.Dpi);
        if (report is null) return null;
        bool warn = BarcodeSizing.Check(b, data ?? b.Data, doc).Any(i => i.MessageKey is BarcodeSizing.OneDotKey or BarcodeSizing.ThinBarKey);
        return new RowDetail(ReadoutLine(report, withSymbol) + Environment.NewLine + QuietZoneLine(report), warn);
    }
}
