using Rotula.Core.Model;

namespace Rotula.Printing;

/// <summary>
/// Turns the raw driver answers of one queue into a <see cref="PrinterCapabilities"/>. Never throws: any failure
/// yields the best information gathered so far or <see cref="PrinterInfoSource.Unknown"/>.
/// </summary>
internal static class PrinterInfoDetector
{
    /// <summary>Reads and interprets one queue. Synchronous; callers run it on the thread pool.</summary>
    internal static PrinterCapabilities Detect(string printerName, IPrinterCapsReader reader)
    {
        string name = printerName ?? string.Empty;
        RawPrinterCaps? raw = null;
        try { raw = reader.Read(name); }
        catch { /* a misbehaving driver must not break the UI: fall through to name parsing */ }
        return Interpret(name, raw);
    }

    /// <summary>Pure decision logic (unit-tested with fabricated raw answers).</summary>
    internal static PrinterCapabilities Interpret(string name, RawPrinterCaps? raw)
    {
        string? driver = raw?.DriverName;

        // Paper size: tenths of a millimetre to Measure.
        Measure? paperW = null, paperH = null;
        if (raw is { PaperWidthTenthsMm: > 0, PaperLengthTenthsMm: > 0 })
        {
            paperW = Measure.Millimeters(raw.PaperWidthTenthsMm.Value / 10.0);
            paperH = Measure.Millimeters(raw.PaperLengthTenthsMm.Value / 10.0);
        }

        // Candidates from the driver, in trust order. The first one that maps to a supported resolution wins;
        // a driver value that maps to nothing (96 dpi virtual printers, 1200 dpi office lasers) is kept only as a raw hint.
        var candidates = new List<(int? X, int? Y, PrinterInfoSource Source)>();
        if (raw is not null)
        {
            candidates.Add((raw.IcXDpi, raw.IcYDpi, PrinterInfoSource.DeviceCaps));
            candidates.Add((raw.DevModeXDpi, raw.DevModeYDpi, PrinterInfoSource.DevMode));
            candidates.Add((raw.EnumDpi, raw.EnumDpi, PrinterInfoSource.DeviceCaps));
        }
        foreach (var (x, y, source) in candidates)
        {
            if (PrinterDpiMapper.Map(x) is { } dpi)
                return new PrinterCapabilities(name, driver, x, y ?? x, dpi, paperW, paperH, source);
        }

        // Fallback: tokens in the driver name, then the queue name ("ZDesigner ZD621R-300dpi ZPL").
        if (PrinterDpiMapper.ParseFromName(driver, name) is { } parsed)
        {
            var mapped = PrinterDpiMapper.Map(parsed);
            return new PrinterCapabilities(name, driver, parsed, parsed, mapped, paperW, paperH, PrinterInfoSource.DriverName);
        }

        // Nothing usable: keep an unmapped driver value so the UI can show it, with Dpi left null.
        foreach (var (x, y, source) in candidates)
        {
            if (x is > 0) return new PrinterCapabilities(name, driver, x, y ?? x, null, paperW, paperH, source);
        }

        return new PrinterCapabilities(name, driver, null, null, null, paperW, paperH, PrinterInfoSource.Unknown);
    }
}
