using Rotula.App.Editing;
using Rotula.App.Services;
using Rotula.Core.Model;
using Rotula.Files.Station;
using Rotula.Printing;

namespace Rotula.App.ViewModels;

/// <summary>
/// The printer and the document of a print station label, built in one place for the station (<see cref="StationViewModel"/>) and for its
/// set-up tool (<see cref="StationSetupViewModel"/>). Picture-mode findings are acknowledged by a fingerprint that depends on the printer's
/// driver media and on the document, so the set-up must check exactly the target and the document the station later prints; sharing this code
/// is what guarantees it.
/// </summary>
internal static class StationPrinting
{
    /// <summary>
    /// Builds the printer of a station label: the printer named in station.json, or else a copy of the printer last used in the designer. The
    /// designer's own settings are never changed: they are copied (<see cref="PrintSettings.Copy"/>), including the per-printer print modes, so a
    /// Windows printer saved as "Windows driver (picture)" prints pictures at the station too. A Windows printer with no saved mode prints in
    /// printer language (ZPL), as every station did before picture mode, and the operator is never asked the mode question: the person setting
    /// up the station chooses the mode in the designer's Print dialog.
    /// </summary>
    /// <param name="service">The print service (listing printers is never needed: the station does no driver detection).</param>
    /// <param name="entry">The label as the configuration describes it.</param>
    /// <param name="doc">The label's design (its size and resolution seed the profile).</param>
    /// <param name="designer">The designer's printer settings, or null when there are none.</param>
    /// <param name="modeDefaulted">True when the printer is a Windows queue with no saved mode, so it was given printer language here.</param>
    /// <returns>The printer, or null when the label has none (no printer in station.json and none used in the designer yet).</returns>
    public static PrinterSettingsViewModel? BuildPrinter(IPrintService service, StationLabel entry, LabelDocument doc, PrintSettings? designer,
        out bool modeDefaulted)
    {
        modeDefaulted = false;
        PrintSettings settings;
        if (entry.Printer is { } p)
        {
            settings = new PrintSettings
            {
                Configured = true,
                Transport = p.Host is null ? "Spooler" : "Tcp",
                PrinterName = p.WindowsPrinter ?? string.Empty,
                Host = p.Host ?? string.Empty,
                Port = p.Port,
                Dpi = (p.Dpi is { } dpi ? (PrinterDpi)dpi : doc.Dpi).ToString(),
                MediaTracking = doc.MediaTracking.ToString(),
                Darkness = p.Darkness,
                SpeedIps = p.Speed,
                StatusCheckNetwork = p.CheckStatus,
                StatusCheckWindows = p.CheckStatus,
                // The mode of the station's queue is the one saved for that queue in the designer, read from a copy of the map.
                PrintModes = ModesOf(designer),
            };
        }
        else if (designer is { Configured: true } d)
        {
            // A copy: the station never changes the designer's own settings (its printer, its profile or its map of print modes).
            settings = d.Copy();
        }
        else return null;
        // No driver detection: the station prints at the resolution it was set up with, the same on every PC.
        var vm = new PrinterSettingsViewModel(service, settings, () => { }, () => doc, (_, _) => Task.FromResult(PrinterDetection.None));
        vm.UseDesignSize();
        if (vm.NeedsModeQuestion)
        {
            // Stored in the station's own copy of the map only. Without it the batch print would stop with the mode question, which an operator
            // cannot answer (and must not: the mode decides what reaches the printer).
            vm.Mode = PrinterOutputMode.PrinterLanguage;
            modeDefaulted = true;
        }
        return vm;
    }

    /// <summary>
    /// The private copy of the label that is printed: the quantity set, then converted to the printer's resolution in printer-language mode (in
    /// picture mode the label goes as designed). The picture-mode pre-flight must run on exactly this document, because the print service works
    /// the fingerprint out again from what it receives.
    /// </summary>
    /// <param name="printer">The label's printer.</param>
    /// <param name="source">The label the station keeps; never changed.</param>
    /// <param name="copies">The quantity to print.</param>
    /// <param name="error">Plain-language reason when the label cannot be converted; null otherwise.</param>
    /// <returns>The document to print, or null when <paramref name="error"/> is set.</returns>
    public static LabelDocument? PrepareDocument(PrinterSettingsViewModel printer, LabelDocument source, int copies, out string? error)
    {
        var doc = ElementCloner.CloneDocument(source);
        doc.Print.Quantity = copies;
        return printer.ConvertForPrinter(doc, out _, out error);
    }

    /// <summary>The quantity a label opens with: the station's setting, else the label's own quantity, within the label's maximum.</summary>
    public static int DefaultCopies(StationLabel entry, LabelDocument doc) => Math.Clamp(entry.DefaultCopies ?? Math.Max(1, doc.Print.Quantity), 1, entry.MaxCopies);

    /// <summary>True for a target printed as a picture through its Windows driver; every other target gets ZPL and is never pre-flighted.</summary>
    public static bool IsDriverTarget(PrinterTarget target) => target is { Transport: PrinterTransport.Spooler, Mode: PrinterOutputMode.WindowsDriver };

    /// <summary>
    /// A copy of the designer's map of print modes, brought up to date by the same rule the settings store applies when it loads a file (only a
    /// map that is absent is seeded). Empty when there are no designer settings.
    /// </summary>
    private static Dictionary<string, string> ModesOf(PrintSettings? designer)
    {
        if (designer is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copy = designer.Copy();
        copy.NormalizePrintModes();
        return copy.PrintModes;
    }
}
