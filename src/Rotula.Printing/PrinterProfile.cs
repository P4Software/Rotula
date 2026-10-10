using System.Globalization;
using System.Text;
using Rotula.Core.Model;
using Rotula.Core.Zpl;

namespace Rotula.Printing;

/// <summary>Printer-specific settings that are not part of a label design.</summary>
public sealed record PrinterProfile
{
    /// <summary>Profile name shown in the UI.</summary>
    public string Name { get; init; } = "Default";
    /// <summary>Printhead resolution.</summary>
    public PrinterDpi Dpi { get; init; } = PrinterDpi.Dpi203;
    /// <summary>Label (print) width; sent as ^PW.</summary>
    public Measure Width { get; init; } = Measure.Millimeters(100);
    /// <summary>Label height, used when Rotula builds its own labels (test label).</summary>
    public Measure Height { get; init; } = Measure.Millimeters(50);
    /// <summary>Media tracking (^MN).</summary>
    public MediaTracking MediaTracking { get; init; } = MediaTracking.Web;
    /// <summary>Darkness 0-30, sent as ~SD. Null leaves the printer setting untouched.</summary>
    public int? Darkness { get; init; }
    /// <summary>Print speed in inches per second 1-14, sent as ^PR. Null leaves the printer setting untouched.</summary>
    public int? SpeedIps { get; init; }
    /// <summary>Character encoding used for the bytes sent when printing the test label.</summary>
    public ZplEncoding Encoding { get; init; } = ZplEncoding.Utf8;

    /// <summary>Returns message keys (<c>Print.Profile.*</c>) for every out-of-range value; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var keys = new List<string>();
        if (Darkness is < 0 or > 30) keys.Add("Print.Profile.Darkness.Range");
        if (SpeedIps is < 1 or > 14) keys.Add("Print.Profile.Speed.Range");
        int w = Width.ToDots(Dpi), h = Height.ToDots(Dpi);
        if (w is < 1 or > 9999) keys.Add("Print.Profile.Width.Range");
        if (h is < 1 or > 32000) keys.Add("Print.Profile.Height.Range");
        return keys;
    }
}

/// <summary>
/// Builds the printer set-up commands of a <see cref="PrinterProfile"/>. Label content, ^MT, ^CI and the
/// rest of the per-label header stay with Core's <see cref="ZplGenerator"/>; this class only adds what
/// the generator does not emit (~SD darkness, ^PR speed) plus ^MN and ^PW so a profile can be applied alone.
/// </summary>
public static class PrinterSetup
{
    /// <summary>
    /// Returns ZPL that applies the profile: <c>~SD</c> (outside a format, as the printer requires) followed by
    /// a small <c>^XA..^XZ</c> block with ^PR, ^MN and ^PW. Prepend it to a label to configure the printer for one job;
    /// the label's own ^MN/^PW (emitted by Core) still win for that label.
    /// </summary>
    /// <exception cref="ArgumentException">The profile is out of range; call <see cref="PrinterProfile.Validate"/> first.</exception>
    public static string BuildSetupZpl(PrinterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var bad = profile.Validate();
        if (bad.Count > 0) throw new ArgumentException("Invalid printer profile: " + string.Join(", ", bad), nameof(profile));

        var sb = new StringBuilder();
        // ~SD is a control command: it is only honoured outside ^XA..^XZ.
        if (profile.Darkness is { } d) sb.Append(string.Create(CultureInfo.InvariantCulture, $"~SD{d:00}\n"));
        sb.Append("^XA\n");
        if (profile.SpeedIps is { } s) sb.Append(string.Create(CultureInfo.InvariantCulture, $"^PR{s}\n"));
        sb.Append("^MN").Append(profile.MediaTracking switch { MediaTracking.Continuous => 'N', MediaTracking.Mark => 'M', _ => 'Y' }).Append('\n');
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"^PW{profile.Width.ToDots(profile.Dpi)}\n"));
        sb.Append("^XZ\n");
        return sb.ToString();
    }
}
