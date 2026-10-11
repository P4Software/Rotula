using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Npgsql;

namespace Rotula.Data;

/// <summary>
/// Reads the PostgreSQL values that Npgsql cannot hand over as a faithful .NET value, straight from the wire format
/// (<c>NpgsqlDataReader.GetStream</c> returns the raw bytes of any column). The cases are numeric (<c>NaN</c>, <c>Infinity</c>, more digits
/// than <see cref="decimal"/> holds), interval (months and years do not fit a <see cref="TimeSpan"/>), dates and timestamps before year 1,
/// after year 9999 or infinite (Npgsql silently turns <c>infinity</c> into 9999-12-31 or 0001-01-01), <c>time</c> 24:00:00, <c>timetz</c> (whose
/// .NET value carries a made-up date) and bit strings. Everything else returns null and takes the shared path, so ordinary values are
/// unaffected. All text is culture-invariant. The decoders are pure functions of the bytes so they can be tested without a server.
/// </summary>
internal static class PostgresValueDecoder
{
    private const uint OidNumeric = 1700, OidDate = 1082, OidTime = 1083, OidTimeTz = 1266, OidTimestamp = 1114, OidTimestampTz = 1184,
        OidInterval = 1186, OidBit = 1560, OidVarBit = 1562;

    private const long MicrosPerDay = 86_400_000_000L;

    /// <summary>PostgreSQL counts days from 2000-01-01; the civil-date algorithm below counts from 1970-01-01.</summary>
    private const int DaysFrom1970To2000 = 10_957;

    /// <summary>The text of the cell, or null when the shared reading (<c>GetValue</c> and the invariant formatter) is exact for it.</summary>
    public static string? TryFormat(DbDataReader reader, int ordinal)
    {
        if (reader is not NpgsqlDataReader pg) return null;
        uint oid = pg.GetDataTypeOID(ordinal);
        if (oid is not (OidNumeric or OidDate or OidTime or OidTimeTz or OidTimestamp or OidTimestampTz or OidInterval or OidBit or OidVarBit)) return null;

        byte[] raw = ReadRaw(pg, ordinal);
        return oid switch
        {
            OidNumeric => FormatNumeric(raw),
            OidInterval => FormatInterval(raw),
            OidTimeTz => FormatTimeTz(raw),
            OidBit or OidVarBit => FormatBits(raw),
            OidDate => FormatDate(raw),
            OidTime => FormatTime(raw),
            OidTimestamp => FormatTimestamp(raw, withZone: false),
            _ => FormatTimestamp(raw, withZone: true),
        };
    }

    private static byte[] ReadRaw(NpgsqlDataReader reader, int ordinal)
    {
        using var stream = reader.GetStream(ordinal);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // ---------------------------------------------------------------- numeric

    /// <summary>
    /// Decodes <c>numeric</c>: int16 digit count, int16 weight (of the first base-10000 digit), uint16 sign, int16 display scale, then the
    /// digits. The result keeps the declared scale (<c>12.50</c> stays <c>12.50</c>) and every digit.
    /// </summary>
    internal static string FormatNumeric(ReadOnlySpan<byte> raw)
    {
        int count = BinaryPrimitives.ReadInt16BigEndian(raw);
        int weight = BinaryPrimitives.ReadInt16BigEndian(raw[2..]);
        ushort sign = BinaryPrimitives.ReadUInt16BigEndian(raw[4..]);
        int scale = BinaryPrimitives.ReadInt16BigEndian(raw[6..]);
        switch (sign)
        {
            case 0xC000: return "NaN";
            case 0xD000: return "Infinity";
            case 0xF000: return "-Infinity";
        }

        var sb = new StringBuilder();
        if (sign == 0x4000) sb.Append('-');
        if (weight < 0) sb.Append('0');
        else
            for (int i = 0; i <= weight; i++)
                sb.Append(i == 0 ? Digit(raw, count, i).ToString(CultureInfo.InvariantCulture) : Digit(raw, count, i).ToString("D4", CultureInfo.InvariantCulture));

        if (scale > 0)
        {
            sb.Append('.');
            // Digit i has the weight (weight - i); the fraction starts at weight -1, that is at index (weight + 1).
            int written = 0;
            for (int i = weight + 1; written < scale; i++)
            {
                string group = Digit(raw, count, i).ToString("D4", CultureInfo.InvariantCulture);
                int take = Math.Min(4, scale - written);
                sb.Append(group, 0, take);
                written += take;
            }
        }
        return sb.ToString();
    }

    /// <summary>One base-10000 digit of a numeric, or 0 outside the stored digits (they are stored without leading and trailing zero groups).</summary>
    private static int Digit(ReadOnlySpan<byte> raw, int count, int index)
        => index >= 0 && index < count ? BinaryPrimitives.ReadInt16BigEndian(raw[(8 + 2 * index)..]) : 0;

    // ---------------------------------------------------------------- interval

    /// <summary>
    /// Decodes <c>interval</c> (int64 microseconds, int32 days, int32 months) the way PostgreSQL itself prints it:
    /// <c>1 year 2 mons 3 days 04:05:06.7</c>. Hours are not limited to 24 and a negative time keeps its sign.
    /// </summary>
    internal static string FormatInterval(ReadOnlySpan<byte> raw)
    {
        long micros = BinaryPrimitives.ReadInt64BigEndian(raw);
        int days = BinaryPrimitives.ReadInt32BigEndian(raw[8..]);
        int months = BinaryPrimitives.ReadInt32BigEndian(raw[12..]);

        var parts = new List<string>();
        int years = months / 12, mons = months % 12;
        if (years != 0) parts.Add(years.ToString(CultureInfo.InvariantCulture) + (years == 1 ? " year" : " years"));
        if (mons != 0) parts.Add(mons.ToString(CultureInfo.InvariantCulture) + (mons == 1 ? " mon" : " mons"));
        if (days != 0) parts.Add(days.ToString(CultureInfo.InvariantCulture) + (days == 1 ? " day" : " days"));
        if (micros != 0 || parts.Count == 0)
        {
            // |long.MinValue| does not fit a long, hence the unsigned magnitude.
            ulong magnitude = micros < 0 ? (ulong)(-(micros + 1)) + 1UL : (ulong)micros;
            parts.Add((micros < 0 ? "-" : "") + Clock(magnitude));
        }
        return string.Join(' ', parts);
    }

    // ---------------------------------------------------------------- date and time

    /// <summary><c>date</c>: int32 days since 2000-01-01. Null for the years 1 to 9999, which .NET holds exactly.</summary>
    internal static string? FormatDate(ReadOnlySpan<byte> raw)
    {
        int days = BinaryPrimitives.ReadInt32BigEndian(raw);
        if (days == int.MaxValue) return "infinity";
        if (days == int.MinValue) return "-infinity";
        var (year, month, day) = CivilFromDays((long)days + DaysFrom1970To2000);
        if (year is >= 1 and <= 9999) return null;
        return DateText(year, month, day, out bool bc) + (bc ? " BC" : "");
    }

    /// <summary>
    /// <c>timestamp</c> / <c>timestamptz</c>: int64 microseconds since 2000-01-01. Null for the years 1 to 9999. A timestamptz is always UTC
    /// on the wire and is written with a trailing <c>Z</c> (the same rule as for a <see cref="DateTime"/> of kind Utc).
    /// </summary>
    internal static string? FormatTimestamp(ReadOnlySpan<byte> raw, bool withZone)
    {
        long micros = BinaryPrimitives.ReadInt64BigEndian(raw);
        if (micros == long.MaxValue) return "infinity";
        if (micros == long.MinValue) return "-infinity";
        long days = Math.DivRem(micros, MicrosPerDay, out long timeOfDay);
        if (timeOfDay < 0) { days--; timeOfDay += MicrosPerDay; }
        var (year, month, day) = CivilFromDays(days + DaysFrom1970To2000);
        if (year is >= 1 and <= 9999) return null;
        return DateText(year, month, day, out bool bc) + " " + Clock((ulong)timeOfDay) + (withZone ? "Z" : "") + (bc ? " BC" : "");
    }

    /// <summary><c>time</c>: int64 microseconds since midnight. Null for ordinary times; PostgreSQL also allows exactly <c>24:00:00</c>.</summary>
    internal static string? FormatTime(ReadOnlySpan<byte> raw)
    {
        long micros = BinaryPrimitives.ReadInt64BigEndian(raw);
        return micros is >= 0 and < MicrosPerDay ? null : Clock((ulong)Math.Max(0, micros));
    }

    /// <summary>
    /// <c>timetz</c>: int64 microseconds since midnight and the zone in seconds WEST of UTC (so <c>+02</c> is stored as -7200).
    /// Written as <c>10:20:30+02:00</c>; Npgsql would hand over a DateTimeOffset on the made-up date 0001-01-02.
    /// </summary>
    internal static string FormatTimeTz(ReadOnlySpan<byte> raw)
    {
        long micros = BinaryPrimitives.ReadInt64BigEndian(raw);
        int east = -BinaryPrimitives.ReadInt32BigEndian(raw[8..]);
        int magnitude = Math.Abs(east);
        string offset = (east < 0 ? "-" : "+") + (magnitude / 3600).ToString("D2", CultureInfo.InvariantCulture)
            + ":" + (magnitude / 60 % 60).ToString("D2", CultureInfo.InvariantCulture)
            + (magnitude % 60 != 0 ? ":" + (magnitude % 60).ToString("D2", CultureInfo.InvariantCulture) : "");
        return Clock((ulong)Math.Max(0, micros)) + offset;
    }

    // ---------------------------------------------------------------- bit strings

    /// <summary><c>bit</c> / <c>varbit</c>: int32 bit count, then the bits, most significant first. Written as the text <c>1010</c>.</summary>
    internal static string FormatBits(ReadOnlySpan<byte> raw)
    {
        int bits = BinaryPrimitives.ReadInt32BigEndian(raw);
        var sb = new StringBuilder(bits);
        for (int i = 0; i < bits; i++)
            sb.Append((raw[4 + i / 8] & (0x80 >> (i % 8))) != 0 ? '1' : '0');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary><c>HH:mm:ss</c> with a fraction only when present (trailing zeros dropped); hours are not limited to 24.</summary>
    private static string Clock(ulong micros)
    {
        ulong hours = micros / 3_600_000_000UL, minutes = micros / 60_000_000UL % 60, seconds = micros / 1_000_000UL % 60, fraction = micros % 1_000_000UL;
        string text = hours.ToString("D2", CultureInfo.InvariantCulture) + ":" + minutes.ToString("D2", CultureInfo.InvariantCulture)
            + ":" + seconds.ToString("D2", CultureInfo.InvariantCulture);
        if (fraction != 0) text += "." + fraction.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        return text;
    }

    /// <summary>
    /// <c>yyyy-MM-dd</c> for an astronomical year (year 0 is 1 BC, as in the PostgreSQL calendar). A year of 0 or less is written as its BC
    /// number and <paramref name="bc"/> is set so the caller can add the suffix after the time.
    /// </summary>
    private static string DateText(long year, int month, int day, out bool bc)
    {
        bc = year < 1;
        long shown = bc ? 1 - year : year;
        return shown.ToString("D4", CultureInfo.InvariantCulture) + "-" + month.ToString("D2", CultureInfo.InvariantCulture)
            + "-" + day.ToString("D2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Proleptic Gregorian date from a day count since 1970-01-01 (H. Hinnant's civil_from_days). Needed because <see cref="DateOnly"/> stops at
    /// year 1 and PostgreSQL goes from 4713 BC to 5874897 AD.
    /// </summary>
    internal static (long Year, int Month, int Day) CivilFromDays(long daysSince1970)
    {
        long z = daysSince1970 + 719_468;
        long era = (z >= 0 ? z : z - 146_096) / 146_097;
        long dayOfEra = z - era * 146_097;
        long yearOfEra = (dayOfEra - dayOfEra / 1_460 + dayOfEra / 36_524 - dayOfEra / 146_096) / 365;
        long dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
        long monthIndex = (5 * dayOfYear + 2) / 153;
        int day = (int)(dayOfYear - (153 * monthIndex + 2) / 5 + 1);
        int month = (int)(monthIndex < 10 ? monthIndex + 3 : monthIndex - 9);
        return (yearOfEra + era * 400 + (month <= 2 ? 1 : 0), month, day);
    }
}
