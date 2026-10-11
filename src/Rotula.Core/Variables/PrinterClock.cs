using System.Globalization;
using Rotula.Core.Validation;

namespace Rotula.Core.Variables;

/// <summary>
/// An offset of the printer's secondary or third clock (ZPL <c>^SO</c>): months, days, years, hours, minutes and seconds, each -32000 to 32000.
/// </summary>
public readonly record struct PrinterOffset(int Months, int Days, int Years, int Hours, int Minutes, int Seconds)
{
    /// <summary>True when every part is zero (the primary clock, no <c>^SO</c> needed).</summary>
    public bool IsZero => Months == 0 && Days == 0 && Years == 0 && Hours == 0 && Minutes == 0 && Seconds == 0;
}

/// <summary>How a date variable is printed by the printer's own clock.</summary>
/// <param name="TokenFormat">The format with <c>%</c> before every clock code letter (a literal percent sign cannot occur).</param>
/// <param name="Offset">The offset the printer must apply.</param>
public sealed record PrinterClockPlan(string TokenFormat, PrinterOffset Offset);

/// <summary>
/// Decides whether a date variable can be printed by the printer's clock and, if so, how. The mapping onto ZPL:
/// </summary>
/// <remarks>
/// <para>
/// <b>Clock codes</b> (ZPL <c>^FC</c>; the indicator is <c>%</c> by default). Written in the field data, the printer replaces each with the
/// current date or time:
/// <c>%a</c> weekday abbreviation, <c>%A</c> weekday name, <c>%b</c> month abbreviation, <c>%B</c> month name, <c>%d</c> day 01-31,
/// <c>%H</c> hour 00-23, <c>%I</c> hour 01-12, <c>%j</c> day of the year 001-366, <c>%m</c> month 01-12, <c>%M</c> minute 00-59,
/// <c>%p</c> AM or PM, <c>%S</c> second 00-59, <c>%U</c> week of the year 00-53 (Sunday first), <c>%w</c> weekday 0-6 (Sunday is 0),
/// <c>%W</c> week of the year 00-53 (Monday first), <c>%y</c> year 00-99, <c>%Y</c> year 0000-9999. Names are printed in the printer's language.
/// </para>
/// <para>
/// <b>Offsets</b>. ZPL <c>^SO</c> defines the secondary clock (set 2) and the third clock (set 3) as the primary clock plus months, days, years,
/// hours, minutes and seconds. The second and third indicator characters of <c>^FC</c> select them. So one label can show the current time and
/// at most two different offset times. Weeks are sent as seven days each.
/// </para>
/// <para>
/// <b>Cannot be done by the printer</b>, so the variable falls back to the computer clock and a warning is raised: business days; rounding to the
/// start or end of a day, month or year; UTC; the fixed-date source; formats with parts that have no printer code (unpadded numbers, fractions
/// of a second, the ISO week, time zone names); month, day or AM/PM names in a culture other than English; a third distinct offset; any offset
/// beyond 32000 of a unit; and use inside a barcode (the data of a barcode is checked on the computer).
/// </para>
/// <para>
/// Mixed offsets: the printer adds the parts together in its own order, while the computer applies them one after another.
/// +1 month and +30 days can differ at month ends. Use a single unit if the exact day matters.
/// </para>
/// </remarks>
public static class PrinterClock
{
    private const int MaxPart = 32000;

    /// <summary>Tries to plan a date variable on the printer clock. On failure a warning describing why is returned in <paramref name="reason"/>.</summary>
    public static bool TryPlan(string variableName, DateVariableOptions o, out PrinterClockPlan? plan, out ValidationIssue? reason)
    {
        plan = null;
        reason = null;
        ValidationIssue Warn(string key, params string[] extra) =>
            new(ValidationSeverity.Warning, key, null, new[] { variableName }.Concat(extra).ToArray());

        if (o.Source == DateSource.Fixed) { reason = Warn(VariableMessageKeys.PrinterFixed); return false; }
        if (o.Utc) { reason = Warn(VariableMessageKeys.PrinterUtc); return false; }
        if (o.Rounding != DateRounding.None) { reason = Warn(VariableMessageKeys.PrinterRounding); return false; }
        if (o.Offsets.Any(x => x.Unit == DateUnit.BusinessDays && x.Amount != 0)) { reason = Warn(VariableMessageKeys.PrinterBusinessDays); return false; }

        long months = 0, days = 0, years = 0, hours = 0, minutes = 0, seconds = 0;
        foreach (var x in o.Offsets)
        {
            switch (x.Unit)
            {
                case DateUnit.Seconds: seconds += x.Amount; break;
                case DateUnit.Minutes: minutes += x.Amount; break;
                case DateUnit.Hours: hours += x.Amount; break;
                case DateUnit.Days: days += x.Amount; break;
                case DateUnit.Weeks: days += (long)x.Amount * 7; break;
                case DateUnit.Months: months += x.Amount; break;
                case DateUnit.Years: years += x.Amount; break;
            }
        }
        if (new[] { months, days, years, hours, minutes, seconds }.Any(v => Math.Abs(v) > MaxPart))
        {
            reason = Warn(VariableMessageKeys.DateOffsetRange, MaxPart.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        if (!DateFormats.TryMapToPrinter(o.Format, out string tokens, out bool usesNames, out string problem))
        {
            reason = Warn(VariableMessageKeys.PrinterFormat, problem);
            return false;
        }
        if (usesNames && !IsEnglish(o.Culture))
        {
            reason = Warn(VariableMessageKeys.PrinterCulture, o.Culture);
            return false;
        }
        plan = new PrinterClockPlan(tokens, new PrinterOffset((int)months, (int)days, (int)years, (int)hours, (int)minutes, (int)seconds));
        return true;
    }

    private static bool IsEnglish(string culture) =>
        string.IsNullOrEmpty(culture) || culture.Equals("en", StringComparison.OrdinalIgnoreCase) || culture.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>^SO</c> command for a clock set (2 or 3).</summary>
    public static string OffsetCommand(int clockSet, PrinterOffset o) =>
        string.Create(CultureInfo.InvariantCulture, $"^SO{clockSet},{o.Months},{o.Days},{o.Years},{o.Hours},{o.Minutes},{o.Seconds}");

    /// <summary>Candidate clock indicator characters, in the order they are tried. Letters and digits are excluded because they can follow an indicator.</summary>
    internal static readonly char[] IndicatorCandidates = { '%', '{', '*', '!', '@', '$', '&', '=', '+', '|', '`', ';' };

    /// <summary>Writes <paramref name="tokenFormat"/> (clock codes marked with <c>%</c>) using <paramref name="indicator"/> as the indicator character.</summary>
    internal static string Render(string tokenFormat, char indicator) => tokenFormat.Replace('%', indicator);
}
