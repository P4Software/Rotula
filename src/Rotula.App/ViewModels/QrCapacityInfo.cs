using System.Globalization;
using Rotula.App.Localization;
using Rotula.Core.Barcodes;
using Rotula.Core.Model;

namespace Rotula.App.ViewModels;

/// <summary>How full a QR code is.</summary>
public enum CapacityState
{
    /// <summary>There is nothing to measure yet.</summary>
    Empty,
    /// <summary>It fits with room to spare.</summary>
    Fits,
    /// <summary>It fits, but so densely that phones may struggle to read it.</summary>
    Dense,
    /// <summary>It does not fit at this error correction level.</summary>
    Overflow,
}

/// <summary>What the capacity meter says about a payload.</summary>
/// <param name="State">How full the code is.</param>
/// <param name="Text">The headline, for example "Fits: 62% of capacity at level Q, version 5".</param>
/// <param name="Hint">What to do about it (lower the level, shorten the text, a suggested level); empty when nothing is needed.</param>
/// <param name="Fit">The measurement behind the text, or null when the payload is empty.</param>
public sealed record QrCapacityReport(CapacityState State, string Text, string Hint, QrFit? Fit)
{
    /// <summary>True when the payload can be printed in the symbol.</summary>
    public bool Fits => State is CapacityState.Fits or CapacityState.Dense;

    /// <summary>The share of the capacity in use, rounded to a whole percent; 0 when there is nothing to measure.</summary>
    public int Percent => Fit is null ? 0 : (int)Math.Round(Fit.UsedPercent, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The text of the QR capacity meter and of the printed-size line, shared by the property panel and the content builder so both always say
/// the same thing. The numbers come from <see cref="QrSizing"/>; this class only chooses the words.
/// </summary>
public static class QrCapacityInfo
{
    /// <summary>A symbol that uses at least this share of its capacity is called dense (it still scans, but with less margin).</summary>
    public const double DenseFromPercent = 85;

    /// <summary>The letter of an error correction level (L, M, Q or H).</summary>
    public static string LevelLetter(QrErrorCorrection level) => level switch
    {
        QrErrorCorrection.Low => "L",
        QrErrorCorrection.Medium => "M",
        QrErrorCorrection.Quartile => "Q",
        _ => "H",
    };

    /// <summary>Measures <paramref name="payload"/> (the longest text the code will carry) and words the result.</summary>
    /// <param name="payload">The text, or null/empty when there is none yet.</param>
    /// <param name="level">The error correction level of the symbol.</param>
    /// <param name="model">1 or 2.</param>
    public static QrCapacityReport Describe(string? payload, QrErrorCorrection level, int model)
    {
        if (string.IsNullOrEmpty(payload))
            return new QrCapacityReport(CapacityState.Empty, Loc.Get("Qr.Capacity.Empty"), string.Empty, null);

        var fit = QrSizing.Analyze(payload, level, model);
        string letter = LevelLetter(level);
        int percent = (int)Math.Round(fit.UsedPercent, MidpointRounding.AwayFromZero);
        if (!fit.Fits)
        {
            string text = Loc.Get("Qr.Capacity.Exceeded", letter, fit.Units.ToString(CultureInfo.CurrentCulture), fit.MaxUnits.ToString(CultureInfo.CurrentCulture));
            // A lower level holds more; name the highest one that makes it fit, or say the text must be shorter.
            var lower = new[] { QrErrorCorrection.Quartile, QrErrorCorrection.Medium, QrErrorCorrection.Low }
                .Where(l => l < level).Cast<QrErrorCorrection?>().FirstOrDefault(l => QrSizing.Analyze(payload, l!.Value, model).Fits);
            string hint = lower is { } fitting ? Loc.Get("Qr.Capacity.TryLevel", LevelLetter(fitting)) : Loc.Get("Qr.Capacity.Shorten");
            return new QrCapacityReport(CapacityState.Overflow, text, hint, fit);
        }

        bool dense = fit.UsedPercent >= DenseFromPercent;
        string headline = model == 1
            ? Loc.Get(dense ? "Qr.Capacity.DenseModel1" : "Qr.Capacity.FitsModel1", percent, letter)
            : Loc.Get(dense ? "Qr.Capacity.Dense" : "Qr.Capacity.Fits", percent, letter, fit.Version);
        string suggestion = string.Empty;
        // Only model 2 has the version tables the suggestion is based on.
        if (model == 2 && QrSizing.SuggestErrorCorrection(payload) is var suggested && suggested != level && QrSizing.Analyze(payload, suggested, model).Fits)
            suggestion = Loc.Get("Qr.Capacity.Suggest", LevelLetter(suggested));
        return new QrCapacityReport(dense ? CapacityState.Dense : CapacityState.Fits, headline, suggestion, fit);
    }

    /// <summary>
    /// The printed size of the symbol and of one module for the current resolution, and a warning when the module is below the 0.25 mm that phone
    /// cameras need. <paramref name="version"/> is 0 when it is not known (model 1, or content that does not fit), then only the module is described.
    /// </summary>
    public static RowDetail ModuleSize(int version, int moduleDots, int dpi)
    {
        double moduleMm = moduleDots * 25.4 / dpi;
        var lines = new List<string>();
        if (version is >= 1 and <= 40)
        {
            double mm = QrSizing.SymbolSizeMm(version, moduleDots, dpi);
            lines.Add(Loc.Get("Qr.Size.Printed", mm.ToString("0.#", CultureInfo.CurrentCulture), (mm / 25.4).ToString("0.##", CultureInfo.CurrentCulture), dpi, version));
        }
        else
            lines.Add(Loc.Get("Qr.Size.Module", moduleMm.ToString("0.###", CultureInfo.CurrentCulture), dpi));

        int minimum = QrSizing.MinimumModuleDots(dpi);
        bool tooSmall = moduleDots < minimum;
        if (tooSmall)
            lines.Add(Loc.Get("Qr.Size.TooSmall", moduleMm.ToString("0.###", CultureInfo.CurrentCulture), minimum, (minimum * 25.4 / dpi).ToString("0.###", CultureInfo.CurrentCulture), dpi));
        return new RowDetail(string.Join(Environment.NewLine, lines), tooSmall);
    }
}
