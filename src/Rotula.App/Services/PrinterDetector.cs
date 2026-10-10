using Rotula.Core.Model;
using Rotula.Printing;

namespace Rotula.App.Services;

/// <summary>What the Windows printer driver says about a printer. All values are null when nothing could be read.</summary>
/// <param name="Dpi">Native resolution, when it is one of the supported values.</param>
/// <param name="PaperWidth">Paper (media) width reported by the driver.</param>
/// <param name="PaperHeight">Paper (media) height reported by the driver.</param>
public sealed record PrinterDetection(PrinterDpi? Dpi, Measure? PaperWidth, Measure? PaperHeight)
{
    /// <summary>Nothing detected.</summary>
    public static PrinterDetection None { get; } = new(null, null, null);
}

/// <summary>
/// The one place that asks the printing layer for driver information. The view-models call this adapter (or a replacement passed
/// in by tests), so the rest of the UI does not depend on the exact shape of the Rotula.Printing API.
/// </summary>
public static class PrinterDetector
{
    /// <summary>Reads resolution and paper size of an installed printer. Never throws; returns <see cref="PrinterDetection.None"/> on failure.</summary>
    public static Task<PrinterDetection> DetectAsync(IPrintService service, string printerName, CancellationToken cancellationToken = default)
    {
        return Read(service, printerName, cancellationToken);
    }

    private static async Task<PrinterDetection> Read(IPrintService service, string printerName, CancellationToken cancellationToken)
    {
        // The printing layer never throws for a failed read (it answers "unknown"), only for cancellation.
        var info = await service.GetPrinterInfoAsync(PrinterTarget.Spooler(printerName), cancellationToken);
        return new PrinterDetection(info.Dpi, info.PaperWidth, info.PaperHeight);
    }
}
