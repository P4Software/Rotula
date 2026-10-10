using System.Collections;
using System.Globalization;
using System.Text;

namespace Rotula.Data;

/// <summary>
/// Turns database values into the strings of a <see cref="Rotula.Core.Merge.MergeTable"/>. The rules are culture-invariant so a
/// label never depends on the machine's regional settings, and they mirror the .xlsx reader where both apply:
/// <list type="bullet">
/// <item>NULL becomes the empty string.</item>
/// <item>Text is kept exactly as stored (not trimmed; padded CHAR(n) values keep their padding).</item>
/// <item>Dates at midnight become <c>yyyy-MM-dd</c>; with a time <c>yyyy-MM-dd HH:mm:ss</c>, plus a fraction (<c>.FFFFFFF</c>, trailing zeros dropped) only when present. Offsets add <c>+hh:mm</c>.</item>
/// <item>Integers and floating point numbers use the invariant culture (dot decimal separator, no grouping); <c>decimal</c> keeps its scale (12.50 stays <c>12.50</c>); doubles print their shortest round-trip form.</item>
/// <item>Booleans become <c>TRUE</c> or <c>FALSE</c>.</item>
/// <item>Binary values (<c>byte[]</c>) become <c>0x</c> followed by upper-case hexadecimal.</item>
/// <item>GUIDs become lower-case <c>D</c> format; time spans use the constant format (<c>[d.]hh:mm:ss[.fffffff]</c>).</item>
/// <item>Arrays and other sequences (PostgreSQL arrays) become their items formatted by these rules, joined with <c>", "</c>.</item>
/// </list>
/// </summary>
public static class DatabaseValueFormatter
{
    /// <summary>Formats one value according to the rules above.</summary>
    public static string Format(object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return string.Empty;
            case string s:
                return s;
            case char c:
                return c.ToString();
            case bool b:
                return b ? "TRUE" : "FALSE";
            case DateTime d:
                return d.TimeOfDay == TimeSpan.Zero
                    ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case DateTimeOffset o:
                return o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
            case DateOnly d:
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly t:
                return t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case TimeSpan ts:
                return ts.ToString("c", CultureInfo.InvariantCulture);
            case Guid g:
                return g.ToString("D", CultureInfo.InvariantCulture);
            case byte[] bytes:
                return ToHex(bytes);
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            case IEnumerable e:
                var parts = new List<string>();
                foreach (var item in e) parts.Add(Format(item));
                return string.Join(", ", parts);
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(2 + bytes.Length * 2);
        sb.Append("0x");
        foreach (byte x in bytes) sb.Append(x.ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
