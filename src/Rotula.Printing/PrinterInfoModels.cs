using System.Globalization;
using System.Text.RegularExpressions;
using Rotula.Core.Model;

namespace Rotula.Printing;

/// <summary>Where the detected resolution of a printer came from, most to least trustworthy.</summary>
public enum PrinterInfoSource
{
    /// <summary>Nothing could be determined (TCP target, unknown queue, non-Windows, or every query failed).</summary>
    Unknown,
    /// <summary>The driver's device capabilities: GetDeviceCaps on a printer information context, or DeviceCapabilities.</summary>
    DeviceCaps,
    /// <summary>The driver's default DEVMODE (print quality / Y resolution).</summary>
    DevMode,
    /// <summary>Parsed from the driver or queue name (for example "ZDesigner ZD621R-300dpi ZPL").</summary>
    DriverName,
}

/// <summary>
/// What Rotula learned about an installed printer from its Windows driver, without printing anything.
/// Named <c>PrinterCapabilities</c> (not <c>PrinterInfo</c>) because <see cref="PrinterInfo"/> is already the
/// queue-list item returned by <see cref="IPrinterDiscovery.GetPrintersAsync"/>.
/// </summary>
/// <param name="Name">Queue name that was queried.</param>
/// <param name="DriverName">Installed driver name, or null when it could not be read.</param>
/// <param name="HorizontalDpi">Raw horizontal resolution reported by the driver (or parsed from a name), null when unknown.</param>
/// <param name="VerticalDpi">Raw vertical resolution, null when unknown.</param>
/// <param name="Dpi">The supported resolution (203, 300 or 600) that <paramref name="HorizontalDpi"/> is within 5% of; null when
/// unknown or when the raw value matches none (let the user choose and show <paramref name="HorizontalDpi"/>).</param>
/// <param name="PaperWidth">Current form width reported by the driver (millimetres), null when unknown.</param>
/// <param name="PaperHeight">Current form height reported by the driver (millimetres), null when unknown.</param>
/// <param name="DpiSource">Which query produced the resolution; <see cref="PrinterInfoSource.Unknown"/> when none did.</param>
public sealed record PrinterCapabilities(
    string Name,
    string? DriverName,
    int? HorizontalDpi,
    int? VerticalDpi,
    PrinterDpi? Dpi,
    Measure? PaperWidth,
    Measure? PaperHeight,
    PrinterInfoSource DpiSource)
{
    /// <summary>Info with nothing detected; what TCP targets, fakes and every failure path return.</summary>
    public static PrinterCapabilities Unknown(string name, string? driverName = null) =>
        new(name ?? string.Empty, driverName, null, null, null, null, null, PrinterInfoSource.Unknown);
}

/// <summary>Pure helpers that turn raw resolutions and printer names into a supported <see cref="PrinterDpi"/>.</summary>
public static class PrinterDpiMapper
{
    /// <summary>Relative distance within which a raw resolution is considered to be a supported one.</summary>
    public const double Tolerance = 0.05;

    private static readonly PrinterDpi[] Supported = [PrinterDpi.Dpi203, PrinterDpi.Dpi300, PrinterDpi.Dpi600];

    // "300dpi", "300 dpi", "300-dpi", "203 DPI": any 3-digit number glued to "dpi".
    private static readonly Regex DpiToken = new(@"(?<!\d)(\d{3})\s*[-_]?\s*dpi\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // "8dpmm", "12 dpmm", "24dpmm": dots per millimetre, Zebra's own unit.
    private static readonly Regex DpmmToken = new(@"(?<!\d)(\d{1,2})\s*[-_]?\s*dpmm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Weak hint, queue names like "ZT610-600" or "ZT610 300": a supported resolution as the last token after a separator.
    private static readonly Regex TrailingToken = new(@"[-_ ](203|300|600)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Maps a raw resolution to the nearest supported one, only when it is within <see cref="Tolerance"/> (5%).
    /// Why a tolerance: drivers report 305 or 600 for 12 and 24 dots/mm printers, but a generic 96 or 1200 must not
    /// be silently forced to a Zebra resolution.
    /// </summary>
    /// <param name="rawDpi">Resolution reported by the driver; null or non-positive gives null.</param>
    public static PrinterDpi? Map(int? rawDpi)
    {
        if (rawDpi is not > 0) return null;
        PrinterDpi? best = null;
        double bestDelta = double.MaxValue;
        foreach (var candidate in Supported)
        {
            double delta = Math.Abs(rawDpi.Value - (int)candidate) / (double)(int)candidate;
            if (delta <= Tolerance && delta < bestDelta) { best = candidate; bestDelta = delta; }
        }
        return best;
    }

    /// <summary>
    /// Looks for a resolution token ("300dpi", "300 dpi", "12dpmm", or a trailing "-600") in the given texts, in order,
    /// and returns the first raw value found. Pass the driver name before the queue name: it is the more reliable one.
    /// </summary>
    /// <returns>The raw resolution (for example 300) or null when no text contains a token.</returns>
    public static int? ParseFromName(params string?[] texts)
    {
        // Strong tokens ("dpi"/"dpmm") in any text win over the weak trailing number in an earlier one.
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = DpiToken.Match(text);
            if (m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var v and > 0) return v;
            m = DpmmToken.Match(text);
            if (m.Success)
            {
                int dpmm = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (dpmm > 0) return (int)Math.Round(dpmm * 25.4, MidpointRounding.AwayFromZero);
            }
        }
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = TrailingToken.Match(text);
            if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }
}
