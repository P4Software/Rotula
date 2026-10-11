using System.Globalization;
using MySqlConnector;

namespace Rotula.Data;

/// <summary>
/// Reads the MySQL / MariaDB values that a plain .NET value cannot represent: <c>DECIMAL</c> wider than <see cref="decimal"/> (up to 65
/// digits), the zero date <c>0000-00-00</c> (the connection uses <c>AllowZeroDateTime</c>, so such values arrive as
/// <see cref="MySqlDateTime"/> instead of being turned into 0001-01-01) and <c>TIME</c> beyond 24 hours (range -838:59:59 to 838:59:59).
/// Every other type returns null and takes the shared path.
/// </summary>
internal static class MySqlValueFormatter
{
    /// <summary>The text of the cell, or null when the shared reading is exact for it.</summary>
    public static string? TryFormat(MySqlDataReader reader, int ordinal)
    {
        switch (reader.GetDataTypeName(ordinal))
        {
            case "DECIMAL" or "NEWDECIMAL":
                // The driver keeps the server's own text, so every digit and the declared scale survive (12.50 stays 12.50). The text is
                // plain ASCII digits with a dot, independent of the culture; System.Decimal would fail beyond 28 digits.
                return reader.GetMySqlDecimal(ordinal).ToString();
            case "DATE" or "DATETIME" or "TIMESTAMP" or "NEWDATE":
            {
                var value = reader.GetMySqlDateTime(ordinal);
                if (value.IsValidDateTime) return DatabaseValueFormatter.Format(value.GetDateTime());
                // A zero or partly zero date (0000-00-00, 2024-00-05): shown exactly as stored.
                return InvalidDateText(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Microsecond);
            }
            case "TIME":
                return TimeText(reader.GetTimeSpan(ordinal));
            default:
                return null;
        }
    }

    /// <summary>
    /// A date that does not exist in the calendar, as MariaDB stores it. Like valid values, the time is left out when it is midnight, so the zero
    /// date reads <c>0000-00-00</c> for both DATE and DATETIME columns.
    /// </summary>
    internal static string InvalidDateText(int year, int month, int day, int hour, int minute, int second, int microsecond)
    {
        string date = year.ToString("D4", CultureInfo.InvariantCulture) + "-" + month.ToString("D2", CultureInfo.InvariantCulture)
            + "-" + day.ToString("D2", CultureInfo.InvariantCulture);
        if (hour == 0 && minute == 0 && second == 0 && microsecond == 0) return date;
        string time = hour.ToString("D2", CultureInfo.InvariantCulture) + ":" + minute.ToString("D2", CultureInfo.InvariantCulture)
            + ":" + second.ToString("D2", CultureInfo.InvariantCulture);
        if (microsecond != 0) time += "." + microsecond.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        return date + " " + time;
    }

    /// <summary>
    /// <c>[-]hhh:mm:ss[.ffffff]</c> with the hours counted in total, not wrapped into days (<c>838:59:59</c>, not <c>34.22:59:59</c>), because a
    /// MySQL TIME is a duration of up to 838 hours.
    /// </summary>
    internal static string TimeText(TimeSpan value)
    {
        long ticks = value.Ticks;
        ulong magnitude = ticks < 0 ? (ulong)(-(ticks + 1)) + 1UL : (ulong)ticks;
        ulong totalSeconds = magnitude / (ulong)TimeSpan.TicksPerSecond;
        ulong fraction = magnitude % (ulong)TimeSpan.TicksPerSecond;
        string text = (ticks < 0 ? "-" : "") + (totalSeconds / 3600).ToString("D2", CultureInfo.InvariantCulture)
            + ":" + (totalSeconds / 60 % 60).ToString("D2", CultureInfo.InvariantCulture)
            + ":" + (totalSeconds % 60).ToString("D2", CultureInfo.InvariantCulture);
        // A tick is 100 ns; MariaDB keeps microseconds, so the seventh digit is always zero.
        if (fraction != 0) text += "." + fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return text;
    }
}
