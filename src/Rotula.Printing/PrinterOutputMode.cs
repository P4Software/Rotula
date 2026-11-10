namespace Rotula.Printing;

/// <summary>How a label reaches a printer: as the printer's own language, or as a picture drawn through the Windows driver.</summary>
public enum PrinterOutputMode
{
    /// <summary>The label is sent as ZPL (today's behaviour, and the only mode a raw TCP target has).</summary>
    PrinterLanguage,
    /// <summary>The label is drawn as a picture and handed to the installed Windows driver (non-Zebra label printers, office printers).</summary>
    WindowsDriver,
}

/// <summary>
/// Suggests a <see cref="PrinterOutputMode"/> from the names Windows reports for a printer. Only a suggestion: it pre-selects an answer the person
/// confirms, and nothing in the library switches a printer's mode because of it (a ZPL printer with an unusual name must not be flipped silently).
/// </summary>
public static class PrinterOutputModeSuggestion
{
    /// <summary>Name fragments that identify Zebra-style ZPL queues, matched case-insensitively.</summary>
    private static readonly string[] ZplNameMarkers = ["ZDesigner", "Zebra", "ZPL"];

    /// <summary>The driver Windows ships for queues that pass text through untouched, which is how ZPL printers are usually installed without a vendor driver.</summary>
    private const string TextOnlyDriver = "Generic / Text Only";

    /// <summary>
    /// The suggested mode: <see cref="PrinterOutputMode.PrinterLanguage"/> when the queue or driver name contains ZDesigner, Zebra or ZPL, or the driver
    /// is Generic / Text Only; otherwise <see cref="PrinterOutputMode.WindowsDriver"/>.
    /// </summary>
    /// <param name="queueName">Windows queue name.</param>
    /// <param name="driverName">Installed driver name.</param>
    /// <param name="portName">Port name; accepted for symmetry with <see cref="PrinterInfo"/> but not used (a USB port says nothing about the language).</param>
    public static PrinterOutputMode For(string queueName, string driverName, string portName)
    {
        // Null-safe on purpose: a driver that reports nothing must not break the print dialog.
        queueName ??= string.Empty;
        driverName ??= string.Empty;

        foreach (var marker in ZplNameMarkers)
        {
            if (queueName.Contains(marker, StringComparison.OrdinalIgnoreCase) || driverName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return PrinterOutputMode.PrinterLanguage;
        }

        return driverName.Contains(TextOnlyDriver, StringComparison.OrdinalIgnoreCase)
            ? PrinterOutputMode.PrinterLanguage
            : PrinterOutputMode.WindowsDriver;
    }
}
