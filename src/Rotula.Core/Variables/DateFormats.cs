using System.Globalization;
using System.Text;

namespace Rotula.Core.Variables;

/// <summary>
/// A named date format. <see cref="PrinterTokens"/> is the same format written with the printer's clock codes (<c>%</c> followed by a
/// <c>^FC</c> code letter), or null when the printer cannot print it.
/// </summary>
/// <param name="Id">Id used in <see cref="DateVariableOptions.Format"/> (not case sensitive).</param>
/// <param name="Example">What 2026-03-07 14:05:09 looks like.</param>
/// <param name="Description">One-line description in English.</param>
/// <param name="Render">Formats a date on the computer.</param>
/// <param name="PrinterTokens">The same layout as printer clock codes, or null.</param>
public sealed record DatePreset(string Id, string Example, string Description, Func<DateTime, CultureInfo, string> Render, string? PrinterTokens);

/// <summary>The built-in date format presets.</summary>
public static class DatePresets
{
    private static string Pat(DateTime d, CultureInfo c, string pattern) => d.ToString(pattern, c);

    /// <summary>Week number as C's <c>%U</c> (weeks start on Sunday, days before the first Sunday are week 0) or <c>%W</c> (Monday).</summary>
    internal static int WeekOfYear(DateTime d, DayOfWeek firstDay)
    {
        int shift = firstDay == DayOfWeek.Sunday ? (int)d.DayOfWeek : ((int)d.DayOfWeek + 6) % 7;
        return (d.DayOfYear - 1 + 7 - shift) / 7;
    }

    /// <summary>All presets in documentation order.</summary>
    public static IReadOnlyList<DatePreset> All { get; } = new DatePreset[]
    {
        new("iso", "2026-03-07", "ISO 8601 date", (d, c) => Pat(d, CultureInfo.InvariantCulture, "yyyy-MM-dd"), "%Y-%m-%d"),
        new("isoDateTime", "2026-03-07T14:05:09", "ISO 8601 date and time", (d, c) => Pat(d, CultureInfo.InvariantCulture, "yyyy-MM-dd'T'HH:mm:ss"), "%Y-%m-%dT%H:%M:%S"),
        new("compact", "20260307", "Year, month and day without separators", (d, c) => Pat(d, CultureInfo.InvariantCulture, "yyyyMMdd"), "%Y%m%d"),
        new("gs1", "260307", "GS1 date YYMMDD (application identifiers 11, 13, 15, 17)", (d, c) => Pat(d, CultureInfo.InvariantCulture, "yyMMdd"), "%y%m%d"),
        new("julian", "066", "Day of the year, three digits (001-366)", (d, c) => d.DayOfYear.ToString("000", CultureInfo.InvariantCulture), "%j"),
        new("julianYear", "26066", "Two-digit year and day of the year (YYDDD)", (d, c) => Pat(d, CultureInfo.InvariantCulture, "yy") + d.DayOfYear.ToString("000", CultureInfo.InvariantCulture), "%y%j"),
        new("week", "10", "ISO 8601 week number, two digits (week 1 contains the first Thursday)", (d, c) => ISOWeek.GetWeekOfYear(d).ToString("00", CultureInfo.InvariantCulture), null),
        new("isoWeek", "2026-W10", "ISO 8601 week with its week-based year", (d, c) => ISOWeek.GetYear(d).ToString("0000", CultureInfo.InvariantCulture) + "-W" + ISOWeek.GetWeekOfYear(d).ToString("00", CultureInfo.InvariantCulture), null),
        new("weekSunday", "09", "Week number, weeks start on Sunday (00-53, like the printer's %U)", (d, c) => WeekOfYear(d, DayOfWeek.Sunday).ToString("00", CultureInfo.InvariantCulture), "%U"),
        new("weekMonday", "09", "Week number, weeks start on Monday (00-53, like the printer's %W)", (d, c) => WeekOfYear(d, DayOfWeek.Monday).ToString("00", CultureInfo.InvariantCulture), "%W"),
        new("monthDayYear", "Mar 07 2026", "Abbreviated month name, day and year", (d, c) => Pat(d, c, "MMM dd yyyy"), "%b %d %Y"),
        new("expiry", "03/2026", "Month and year, as used for expiry dates", (d, c) => Pat(d, CultureInfo.InvariantCulture, "MM/yyyy"), "%m/%Y"),
        new("dmy", "07/03/2026", "Day/month/year", (d, c) => Pat(d, CultureInfo.InvariantCulture, "dd/MM/yyyy"), "%d/%m/%Y"),
        new("mdy", "03/07/2026", "Month/day/year", (d, c) => Pat(d, CultureInfo.InvariantCulture, "MM/dd/yyyy"), "%m/%d/%Y"),
        new("time", "14:05", "Hours and minutes, 24-hour clock", (d, c) => Pat(d, CultureInfo.InvariantCulture, "HH:mm"), "%H:%M"),
        new("timeSeconds", "14:05:09", "Hours, minutes and seconds, 24-hour clock", (d, c) => Pat(d, CultureInfo.InvariantCulture, "HH:mm:ss"), "%H:%M:%S"),
    };

    private static readonly Dictionary<string, DatePreset> ById = All.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a preset by id (not case sensitive), or null.</summary>
    public static DatePreset? Find(string? id) => id is null ? null : ById.GetValueOrDefault(id);
}

/// <summary>Formatting of dates by preset id or .NET custom pattern, shared by date variables, prompts and the <c>date</c> function.</summary>
public static class DateFormats
{
    /// <summary>
    /// Formats <paramref name="date"/>. <paramref name="format"/> is a preset id or a .NET custom date pattern; returns null when it is empty, too
    /// long or not a valid pattern. A pattern of one letter is treated as a custom pattern (so <c>d</c> is the day, not the short date).
    /// </summary>
    public static string? TryFormat(DateTime date, string? format, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(format) || format.Length > VariableLimits.MaxFormatLength) return null;
        if (DatePresets.Find(format) is { } preset) return preset.Render(date, culture);
        try
        {
            return date.ToString(format.Length == 1 ? "%" + format : format, culture);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="format"/> is a preset or a valid pattern.</summary>
    public static bool IsValid(string? format) => TryFormat(new DateTime(2026, 3, 7, 14, 5, 9), format, CultureInfo.InvariantCulture) is not null;

    /// <summary>
    /// Finds the culture for <paramref name="name"/>. An empty name is the invariant culture. The calendar is forced to Gregorian so a culture such as
    /// <c>th-TH</c> does not turn 2026 into 2569 on a label. Returns null when the name is unknown.
    /// </summary>
    public static CultureInfo? ResolveCulture(string? name)
    {
        if (string.IsNullOrEmpty(name)) return CultureInfo.InvariantCulture;
        if (name.Length > 40) return null;
        CultureInfo culture;
        // predefinedOnly: otherwise .NET invents a culture for any well-formed name, and a typo would silently format with invariant data.
        try { culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true); }
        catch (CultureNotFoundException) { return null; }
        if (culture.DateTimeFormat.Calendar is GregorianCalendar) return culture;
        var copy = (CultureInfo)culture.Clone();
        try
        {
            copy.DateTimeFormat.Calendar = copy.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault() ?? new GregorianCalendar();
            return copy;
        }
        catch (ArgumentException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>
    /// Translates a format into the printer's clock codes. Succeeds only when every part has a printer code and the text stays English
    /// (<paramref name="usesNames"/> reports whether month, day or AM/PM names are used). On failure <paramref name="problem"/> names the part that
    /// cannot be printed.
    /// </summary>
    public static bool TryMapToPrinter(string format, out string tokens, out bool usesNames, out string problem)
    {
        tokens = string.Empty;
        usesNames = false;
        problem = string.Empty;
        if (DatePresets.Find(format) is { } preset)
        {
            if (preset.PrinterTokens is null) { problem = preset.Id; return false; }
            tokens = preset.PrinterTokens;
            usesNames = preset.PrinterTokens.Contains("%b", StringComparison.Ordinal);
            return true;
        }
        if (!IsValid(format)) { problem = format; return false; }
        string p = format.Length == 1 ? "%" + format : format;
        var sb = new StringBuilder();
        for (int i = 0; i < p.Length;)
        {
            char c = p[i];
            if (c is '\'' or '"')
            {
                int end = p.IndexOf(c, i + 1);
                string literal = p[(i + 1)..end]; // IsValid already proved the quote is closed
                if (literal.Contains('%')) { problem = literal; return false; }
                sb.Append(literal);
                i = end + 1;
                continue;
            }
            if (c == '\\')
            {
                char next = p[i + 1];
                if (next == '%') { problem = "\\%"; return false; }
                sb.Append(next);
                i += 2;
                continue;
            }
            if (c == '%')
            {
                // "%d" is the one-letter custom pattern; the printer has no unpadded forms.
                problem = i + 1 < p.Length ? p.Substring(i, 2) : "%";
                return false;
            }
            if ("dfFghHKmMstyz".Contains(c))
            {
                int run = 1;
                while (i + run < p.Length && p[i + run] == c) run++;
                string? code = (c, run) switch
                {
                    ('d', 2) => "%d",
                    ('d', 3) => "%a",
                    ('d', 4) => "%A",
                    ('M', 2) => "%m",
                    ('M', 3) => "%b",
                    ('M', 4) => "%B",
                    ('y', 2) => "%y",
                    ('y', 4) => "%Y",
                    ('H', 2) => "%H",
                    ('h', 2) => "%I",
                    ('m', 2) => "%M",
                    ('s', 2) => "%S",
                    ('t', 2) => "%p",
                    _ => null,
                };
                if (code is null) { problem = new string(c, run); return false; }
                if (code is "%a" or "%A" or "%b" or "%B" or "%p") usesNames = true;
                sb.Append(code);
                i += run;
                continue;
            }
            sb.Append(c);
            i++;
        }
        tokens = sb.ToString();
        return true;
    }
}
