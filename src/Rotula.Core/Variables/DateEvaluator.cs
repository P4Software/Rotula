using System.Globalization;
using Rotula.Core.Validation;

namespace Rotula.Core.Variables;

/// <summary>
/// Computes the value of a date variable on the computer: take "now", apply the offsets in order, round, then format.
/// </summary>
/// <remarks>
/// Rules that matter at the edges:
/// <list type="bullet">
/// <item>Seconds, minutes and hours are exact elapsed time, so +2 hours across a daylight-saving change really is two hours later.</item>
/// <item>Days, weeks, months and years work on the wall-clock date and keep the time of day (a clock time that does not exist because the
/// clocks jumped forward moves one hour later; one that happens twice uses its first occurrence).</item>
/// <item>Months and years clamp to the last day of the target month: 31 January + 1 month = 28 February (29 in a leap year).</item>
/// <item>Business days count Monday to Friday and skip the variable's holidays; +0 business days leaves the date as it is.</item>
/// <item>Rounding is applied after all offsets.</item>
/// </list>
/// </remarks>
public static class DateEvaluator
{
    /// <summary>
    /// Evaluates the variable. Returns null (with an issue) when the definition is unusable or the result is outside the dates .NET can represent.
    /// </summary>
    /// <param name="variableName">Name used in issue arguments.</param>
    /// <param name="options">The definition.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="localZone">The time zone of the computer (or of the test).</param>
    /// <param name="issues">Receives problems.</param>
    public static string? Evaluate(string variableName, DateVariableOptions options, DateTimeOffset nowUtc, TimeZoneInfo localZone, ICollection<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(options);
        var culture = DateFormats.ResolveCulture(options.Culture);
        if (culture is null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.DateCultureUnknown, null, variableName, options.Culture));
            culture = CultureInfo.InvariantCulture;
        }
        if (!DateFormats.IsValid(options.Format))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.DateFormatInvalid, null, variableName, options.Format ?? string.Empty));
            return null;
        }
        if (options.Offsets.Count > VariableLimits.MaxOffsets || options.Offsets.Any(o => Math.Abs((long)o.Amount) > VariableLimits.MaxOffsetAmount))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.DateOffsetRange, null, variableName,
                VariableLimits.MaxOffsetAmount.ToString(CultureInfo.InvariantCulture)));
            return null;
        }

        TimeZoneInfo zone;
        DateTimeOffset t;
        try
        {
            if (options.Source == DateSource.Fixed)
            {
                if (options.FixedValue is not { } fixedValue)
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.DateFixedMissing, null, variableName));
                    return null;
                }
                // A fixed date is a wall-clock value with no zone; UTC keeps the arithmetic free of daylight-saving surprises.
                zone = TimeZoneInfo.Utc;
                t = new DateTimeOffset(DateTime.SpecifyKind(fixedValue, DateTimeKind.Unspecified), TimeSpan.Zero);
            }
            else
            {
                zone = options.Utc ? TimeZoneInfo.Utc : localZone;
                t = TimeZoneInfo.ConvertTime(nowUtc, zone);
            }

            var holidays = options.Holidays.Count == 0 ? null : new HashSet<DateOnly>(options.Holidays);
            foreach (var o in options.Offsets) t = Apply(t, o, zone, holidays);
            t = Round(t, options.Rounding, zone);
            string? text = DateFormats.TryFormat(t.DateTime, options.Format, culture);
            return text ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.DateOutOfRange, null, variableName));
            return null;
        }
        catch (FormatException)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.DateFormatInvalid, null, variableName, options.Format ?? string.Empty));
            return null;
        }
    }

    private static DateTimeOffset Apply(DateTimeOffset t, DateOffset o, TimeZoneInfo zone, HashSet<DateOnly>? holidays)
    {
        long n = o.Amount;
        switch (o.Unit)
        {
            case DateUnit.Seconds: return TimeZoneInfo.ConvertTime(t.AddSeconds(n), zone);
            case DateUnit.Minutes: return TimeZoneInfo.ConvertTime(t.AddMinutes(n), zone);
            case DateUnit.Hours: return TimeZoneInfo.ConvertTime(t.AddHours(n), zone);
            case DateUnit.Days: return FromWall(t.DateTime.AddDays(n), zone);
            case DateUnit.Weeks: return FromWall(t.DateTime.AddDays(n * 7), zone);
            case DateUnit.Months: return FromWall(t.DateTime.AddMonths((int)n), zone);
            case DateUnit.Years: return FromWall(t.DateTime.AddYears((int)n), zone);
            case DateUnit.BusinessDays: return FromWall(AddBusinessDays(t.DateTime, (int)n, holidays), zone);
            default: throw new ArgumentOutOfRangeException(nameof(o));
        }
    }

    /// <summary>Moves by whole business days, keeping the time of day. Each step lands on the next day that is not a weekend or holiday.</summary>
    internal static DateTime AddBusinessDays(DateTime wall, int amount, HashSet<DateOnly>? holidays)
    {
        int step = Math.Sign(amount);
        int left = Math.Abs(amount);
        var d = wall;
        // A day count is bounded (VariableLimits.MaxOffsetAmount) and every skipped run is at most a few days, so this loop is short.
        // A holiday list covering every day would loop forever, hence the iteration guard.
        long guard = (long)left * 8 + 4000;
        while (left > 0)
        {
            d = d.AddDays(step);
            if (--guard < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (holidays is not null && holidays.Contains(DateOnly.FromDateTime(d))) continue;
            left--;
        }
        return d;
    }

    private static DateTimeOffset Round(DateTimeOffset t, DateRounding r, TimeZoneInfo zone)
    {
        var w = t.DateTime;
        DateTime result = r switch
        {
            DateRounding.StartOfDay => w.Date,
            DateRounding.EndOfDay => w.Date.AddDays(1).AddSeconds(-1),
            DateRounding.StartOfMonth => new DateTime(w.Year, w.Month, 1),
            DateRounding.EndOfMonth => new DateTime(w.Year, w.Month, 1).AddMonths(1).AddSeconds(-1),
            DateRounding.StartOfYear => new DateTime(w.Year, 1, 1),
            DateRounding.EndOfYear => new DateTime(w.Year, 1, 1).AddYears(1).AddSeconds(-1),
            _ => w,
        };
        return r == DateRounding.None ? t : FromWall(result, zone);
    }

    /// <summary>Builds the instant for a wall-clock time in <paramref name="zone"/>, resolving gaps and repeated hours.</summary>
    internal static DateTimeOffset FromWall(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        // The clocks jumped forward over this time: it does not exist, so use the same minute one hour later.
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        TimeSpan offset = zone.IsAmbiguousTime(wall)
            ? zone.GetAmbiguousTimeOffsets(wall).Max() // the first of the two occurrences (daylight time, the larger offset)
            : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }
}
