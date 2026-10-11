// The syntax tests ("linters") named in the GS1 Barcode Syntax Dictionary, written in C#.
//
// Derived from the reference linters of the GS1 Barcode Syntax Dictionary, release 2026-01-27
// (https://github.com/gs1/gs1-syntax-dictionary, Apache-2.0):
//   Copyright (c) 2020-2021 BWIPP project, Copyright (c) 2020-2021 Zint Project, Copyright (c) 2021-2025 GS1 AISBL.
// Changes: transliterated from C to C#; error codes became message keys; the century of a two-digit year is chosen from an
// explicit reference year instead of a compile-time constant. See THIRD-PARTY-NOTICES.md.

using System.Globalization;
using Rotula.Core.Validation;

namespace Rotula.Core.Gs1;

/// <summary>Settings of the syntax tests. Kept apart from the clock so that tests give the same answer every day.</summary>
/// <param name="ReferenceYear">
/// The current year, two digits (for example 26). It only decides whether a two-digit year such as <c>00</c> means 1900, 2000 or 2100
/// when 29 February is checked (the GS1 rule: years up to 50 ahead are the future, the rest are the past).
/// </param>
public sealed record Gs1LintOptions(int ReferenceYear)
{
    /// <summary>The options for today's date.</summary>
    public static Gs1LintOptions Today => new(DateTime.UtcNow.Year % 100);
}

/// <summary>
/// The content tests of the GS1 Syntax Dictionary: check digits, dates, country and currency codes and so on. A test returns null when the
/// value is fine, otherwise an error issue whose first argument is the application identifier and whose message key is <c>Gs1.Lint.*</c>.
/// </summary>
public static class Gs1Linters
{
    private delegate ValidationIssue? Linter(string value, string ai, Gs1LintOptions options);

    private static readonly Dictionary<string, Linter> Table = new(StringComparer.Ordinal)
    {
        ["csum"] = (v, ai, _) => CheckDigit(v, ai),
        ["csumalpha"] = (v, ai, _) => CheckPair(v, ai),
        ["gcppos1"] = (v, ai, _) => GcpPosition(v, 0, ai),
        ["gcppos2"] = (v, ai, _) => GcpPosition(v, 1, ai),
        ["yymmd0"] = (v, ai, o) => TwoDigitYearDate(v, ai, o, dayZeroAllowed: true),
        ["yymmdd"] = (v, ai, o) => TwoDigitYearDate(v, ai, o, dayZeroAllowed: false),
        ["yyyymmd0"] = (v, ai, _) => FourDigitYearDate(v, ai, dayZeroAllowed: true),
        ["yyyymmdd"] = (v, ai, _) => FourDigitYearDate(v, ai, dayZeroAllowed: false),
        ["hh"] = (v, ai, _) => TimePart(v, ai, 23, "HH"),
        ["mi"] = (v, ai, _) => TimePart(v, ai, 59, "MI"),
        ["ss"] = (v, ai, _) => TimePart(v, ai, 59, "SS"),
        ["hhmi"] = (v, ai, _) => HourMinute(v, ai),
        ["hyphen"] = (v, ai, _) => v.Length > 0 && v.All(c => c == '-') ? null : Err("Gs1.Lint.Hyphen", ai),
        ["hasnondigit"] = (v, ai, _) => v.Any(c => c is < '0' or > '9') ? null : Err("Gs1.Lint.NeedsNonDigit", ai),
        ["nonzero"] = (v, ai, _) => NonZero(v, ai),
        ["nozeroprefix"] = (v, ai, _) => NoZeroPrefix(v, ai),
        ["zero"] = (v, ai, _) => v.Length > 0 && v.All(c => c == '0') ? null : Err("Gs1.Lint.NotZero", ai),
        ["winding"] = (v, ai, _) => v is "0" or "1" or "9" ? null : Err("Gs1.Lint.Winding", ai),
        ["yesno"] = (v, ai, _) => v is "0" or "1" ? null : Err("Gs1.Lint.YesNo", ai),
        ["iso5218"] = (v, ai, _) => v is "0" or "1" or "2" or "9" ? null : Err("Gs1.Lint.BiologicalSex", ai),
        ["iso3166"] = (v, ai, _) => Gs1CodeTables.IsCountryNumeric(v) ? null : Err("Gs1.Lint.Country", ai, v),
        ["iso3166999"] = (v, ai, _) => v == "999" || Gs1CodeTables.IsCountryNumeric(v) ? null : Err("Gs1.Lint.Country999", ai, v),
        ["iso3166alpha2"] = (v, ai, _) => Gs1CodeTables.IsCountryAlpha2(v) ? null : Err("Gs1.Lint.CountryAlpha2", ai, v),
        ["iso4217"] = (v, ai, _) => Gs1CodeTables.IsCurrencyNumeric(v) ? null : Err("Gs1.Lint.Currency", ai, v),
        ["packagetype"] = (v, ai, _) => Gs1CodeTables.IsPackageType(v) ? null : Err("Gs1.Lint.PackageType", ai, v),
        ["mediatype"] = (v, ai, _) => MediaType(v, ai),
        ["importeridx"] = (v, ai, _) => v.Length == 1 && (char.IsAsciiLetterOrDigit(v[0]) || v[0] is '-' or '_') ? null : Err("Gs1.Lint.ImporterIndex", ai),
        ["latitude"] = (v, ai, _) => Coordinate(v, ai, 1_800_000_000L, "Gs1.Lint.Latitude"),
        ["longitude"] = (v, ai, _) => Coordinate(v, ai, 3_600_000_000L, "Gs1.Lint.Longitude"),
        ["pieceoftotal"] = (v, ai, _) => PieceOfTotal(v, ai),
        ["posinseqslash"] = (v, ai, _) => PositionInSequence(v, ai),
        ["pcenc"] = (v, ai, _) => PercentEncoding(v, ai),
        ["iban"] = (v, ai, _) => Iban(v, ai),
    };

    /// <summary>
    /// Tests the dictionary names but this build does not run, so a value is never rejected because of them: the North American
    /// coupon formats (AI 8110 and 8112). They are listed here on purpose so that a new dictionary release cannot add a test unnoticed.
    /// </summary>
    public static IReadOnlyCollection<string> NotChecked { get; } = new[] { "couponcode", "couponposoffer" };

    /// <summary>
    /// Tests that run only in part: <c>gcppos1</c> and <c>gcppos2</c> check the shape of the GS1 Company Prefix position (digits, at
    /// least four) but cannot check that a prefix was really issued, which needs GS1's registry (a network lookup Rotula never makes).
    /// </summary>
    public static IReadOnlyCollection<string> PartiallyChecked { get; } = new[] { "gcppos1", "gcppos2" };

    /// <summary>The names of the tests this build runs.</summary>
    public static IReadOnlyCollection<string> Implemented => Table.Keys;

    /// <summary>True when the test named <paramref name="linter"/> is run (fully or in part).</summary>
    public static bool IsImplemented(string linter) => Table.ContainsKey(linter);

    /// <summary>
    /// Runs the test <paramref name="linter"/> on one component of the value of <paramref name="ai"/>.
    /// Returns null when the value passes, when the name is in <see cref="NotChecked"/>, or when it is unknown.
    /// </summary>
    public static ValidationIssue? Lint(string linter, string value, string ai, Gs1LintOptions? options = null) =>
        Table.TryGetValue(linter, out var f) ? f(value, ai, options ?? Gs1LintOptions.Today) : null;

    /// <summary>Tests that <paramref name="value"/> only has characters of <paramref name="set"/>.</summary>
    public static ValidationIssue? CheckCharacterSet(Gs1CharacterSet set, string value, string ai)
    {
        switch (set)
        {
            case Gs1CharacterSet.Numeric:
                foreach (char c in value) if (c is < '0' or > '9') return Err("Gs1.Lint.NonDigit", ai, c.ToString());
                return null;
            case Gs1CharacterSet.Cset82:
                foreach (char c in value) if (!IsCset82(c)) return Err("Gs1.Lint.Cset82", ai, c.ToString());
                return null;
            case Gs1CharacterSet.Cset39:
                foreach (char c in value) if (!IsCset39(c)) return Err("Gs1.Lint.Cset39", ai, c.ToString());
                return null;
            default:
                int pads = 0;
                while (pads < value.Length && value[value.Length - pads - 1] == '=') pads++;
                // Padding is only allowed to complete a group of three characters, and at most two of them.
                if (pads > 2 || (pads > 0 && value.Length % 3 != 0)) return Err("Gs1.Lint.Base64Padding", ai);
                for (int i = 0; i < value.Length - pads; i++)
                    if (!(char.IsAsciiLetterOrDigit(value[i]) || value[i] is '-' or '_')) return Err("Gs1.Lint.Base64", ai, value[i].ToString());
                return null;
        }
    }

    /// <summary>True when <paramref name="c"/> is in GS1 AI encodable character set 82.</summary>
    public static bool IsCset82(char c) =>
        c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' || "!\"%&'()*+,-./:;<=>?_".Contains(c);

    /// <summary>True when <paramref name="c"/> is in GS1 AI encodable character set 39 (digits, capital letters, # - /).</summary>
    public static bool IsCset39(char c) => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or '#' or '-' or '/';

    private static ValidationIssue Err(string key, string ai, params string[] more) =>
        new(ValidationSeverity.Error, key, null, new[] { ai }.Concat(more).ToArray());

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static bool Digits(string v) => v.All(c => c is >= '0' and <= '9');

    // ---- check digits -----------------------------------------------------------------------

    private static ValidationIssue? CheckDigit(string v, string ai)
    {
        if (v.Length == 0) return Err("Gs1.Lint.CheckDigit", ai, "?");
        if (!Digits(v)) return Err("Gs1.Lint.NonDigit", ai, v.First(c => c is < '0' or > '9').ToString());
        // Weights 3,1,3,1... counted from the digit next to the check digit (GS1 General Specifications 7.9.1).
        int weight = v.Length % 2 == 0 ? 3 : 1, sum = 0;
        for (int i = 0; i < v.Length - 1; i++)
        {
            sum += weight * (v[i] - '0');
            weight = 4 - weight;
        }
        int expected = (10 - sum % 10) % 10;
        return v[^1] - '0' == expected ? null : Err("Gs1.Lint.CheckDigit", ai, N(expected));
    }

    private static readonly int[] Primes =
    {
        2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89, 97, 101, 103, 107, 109, 113, 127, 131,
        137, 139, 149, 151, 157, 163, 167, 173, 179, 181, 191, 193, 197, 199, 211, 223, 227, 229, 233, 239, 241, 251, 257, 263, 269, 271,
        277, 281, 283, 293, 307, 311, 313, 317, 331, 337, 347, 349, 353, 359, 367, 373, 379, 383, 389, 397, 401, 409, 419, 421, 431, 433,
        439, 443, 449, 457, 461, 463, 467, 479, 487, 491, 499, 503, 509,
    };

    private const string Cset32 = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    // The weight of a character of set 82 is its position in this string plus one; the string is the set in ASCII order, which is what
    // the reference table lists ("!" is 1 ... "z" is 82).
    private const string Cset82Ordered = "!\"%&'()*+,-./0123456789:;<=>?ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";

    private static ValidationIssue? CheckPair(string v, string ai)
    {
        if (v.Length > Primes.Length + 2 || v.Length < 2) return Err("Gs1.Lint.CheckPair", ai);
        if (v.Length == 2) return v == "22" ? null : Err("Gs1.Lint.CheckPair", ai);
        uint sum = 0;
        for (int i = 0; i < v.Length - 2; i++)
        {
            int index = Cset82Ordered.IndexOf(v[i]);
            if (index < 0) return Err("Gs1.Lint.Cset82", ai, v[i].ToString());
            sum += (uint)(index * Primes[v.Length - 3 - i]);
        }
        sum %= 1021;
        return v[^2] == Cset32[(int)(sum >> 5)] && v[^1] == Cset32[(int)(sum & 31)] ? null : Err("Gs1.Lint.CheckPair", ai);
    }

    private static ValidationIssue? GcpPosition(string v, int skip, string ai)
    {
        // The shortest GS1 Company Prefix has four digits; a registry lookup would be needed for more (see PartiallyChecked).
        const int minGcp = 4;
        if (v.Length < skip + minGcp) return Err("Gs1.Lint.GcpPrefix", ai);
        for (int i = skip; i < skip + minGcp; i++)
            if (v[i] is < '0' or > '9') return Err("Gs1.Lint.GcpPrefix", ai);
        return null;
    }

    // ---- dates and times --------------------------------------------------------------------

    private static ValidationIssue? TwoDigitYearDate(string v, string ai, Gs1LintOptions o, bool dayZeroAllowed)
    {
        if (v.Length != 6) return Err("Gs1.Lint.DateFormat", ai, "YYMMDD");
        if (!Digits(v)) return Err("Gs1.Lint.NonDigit", ai, v.First(c => c is < '0' or > '9').ToString());
        int yy = (v[0] - '0') * 10 + (v[1] - '0'), diff = yy - o.ReferenceYear;
        // GS1 General Specifications 7.12: a two-digit year within 49 years back or 50 ahead of today is meant; the century only
        // matters for 29 February.
        string century = diff >= 51 ? "19" : diff > -50 ? "20" : "21";
        return FourDigitYearDate(century + v, ai, dayZeroAllowed);
    }

    private static ValidationIssue? FourDigitYearDate(string v, string ai, bool dayZeroAllowed)
    {
        if (v.Length != 8) return Err("Gs1.Lint.DateFormat", ai, "YYYYMMDD");
        if (!Digits(v)) return Err("Gs1.Lint.NonDigit", ai, v.First(c => c is < '0' or > '9').ToString());
        int year = int.Parse(v.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture);
        int month = int.Parse(v.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int day = int.Parse(v.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12) return Err("Gs1.Lint.DateMonth", ai, v.Substring(4, 2));
        int max = month == 2 ? ((year % 4 == 0 && year % 100 != 0) || year % 400 == 0 ? 29 : 28) : month is 4 or 6 or 9 or 11 ? 30 : 31;
        // Day "00" means "no day" (a date with only year and month), which the 0-flavoured tests accept.
        if (day > max) return Err("Gs1.Lint.DateDay", ai, v.Substring(6, 2));
        if (day == 0 && !dayZeroAllowed) return Err("Gs1.Lint.DateDay", ai, "00");
        return null;
    }

    // 'what' is the format code of the part (HH, MI or SS), which is also the argument of the format message.
    private static ValidationIssue? TimePart(string v, string ai, int max, string what)
    {
        if (v.Length != 2) return Err("Gs1.Lint.TimeFormat", ai, what);
        if (!Digits(v)) return Err("Gs1.Lint.NonDigit", ai, v.First(c => c is < '0' or > '9').ToString());
        int value = (v[0] - '0') * 10 + (v[1] - '0');
        if (value <= max) return null;
        return what switch
        {
            "HH" => Err("Gs1.Lint.Hour", ai, v),
            "MI" => Err("Gs1.Lint.Minute", ai, v),
            _ => Err("Gs1.Lint.Second", ai, v),
        };
    }

    private static ValidationIssue? HourMinute(string v, string ai)
    {
        if (v.Length != 4) return Err("Gs1.Lint.TimeFormat", ai, "HHMI");
        return TimePart(v[..2], ai, 23, "HH") ?? TimePart(v[2..], ai, 59, "MI");
    }

    // ---- numbers ----------------------------------------------------------------------------

    private static ValidationIssue? NonZero(string v, string ai)
    {
        if (!Digits(v)) return Err("Gs1.Lint.NonDigit", ai, v.First(c => c is < '0' or > '9').ToString());
        return v.Any(c => c != '0') ? null : Err("Gs1.Lint.ZeroValue", ai);
    }

    private static ValidationIssue? NoZeroPrefix(string v, string ai)
    {
        if (v.Length > 0 && v[0] == '0') return Err("Gs1.Lint.ZeroPrefix", ai);
        return Digits(v) ? null : Err("Gs1.Lint.NonDigit", ai, v.First(c => c is < '0' or > '9').ToString());
    }

    private static ValidationIssue? Coordinate(string v, string ai, long max, string key)
    {
        if (v.Length != 10) return Err(key, ai);
        if (!Digits(v)) return Err("Gs1.Lint.NonDigit", ai, v.First(c => c is < '0' or > '9').ToString());
        return long.Parse(v, NumberStyles.None, CultureInfo.InvariantCulture) <= max ? null : Err(key, ai);
    }

    private static ValidationIssue? PieceOfTotal(string v, string ai)
    {
        if (!Digits(v)) return Err("Gs1.Lint.NonDigit", ai, v.First(c => c is < '0' or > '9').ToString());
        if (v.Length == 0 || v.Length % 2 != 0) return Err("Gs1.Lint.PieceFormat", ai);
        string piece = v[..(v.Length / 2)], total = v[(v.Length / 2)..];
        if (piece.All(c => c == '0') || total.All(c => c == '0')) return Err("Gs1.Lint.PieceZero", ai);
        // Both numbers have the same number of digits, so comparing them as text compares their values.
        return string.CompareOrdinal(piece, total) > 0 ? Err("Gs1.Lint.PieceExceeds", ai) : null;
    }

    private static ValidationIssue? PositionInSequence(string v, string ai)
    {
        int slash = v.IndexOf('/');
        if (slash <= 0 || slash >= v.Length - 1) return Err("Gs1.Lint.SequenceFormat", ai);
        string pos = v[..slash], end = v[(slash + 1)..];
        if (!Digits(pos) || !Digits(end)) return Err("Gs1.Lint.SequenceFormat", ai);
        if (pos[0] == '0' || end[0] == '0') return Err("Gs1.Lint.ZeroPrefix", ai);
        bool exceeds = pos.Length != end.Length ? pos.Length > end.Length : string.CompareOrdinal(pos, end) > 0;
        return exceeds ? Err("Gs1.Lint.SequenceExceeds", ai) : null;
    }

    // ---- text -------------------------------------------------------------------------------

    private static ValidationIssue? PercentEncoding(string v, string ai)
    {
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] != '%') continue;
            if (i + 2 >= v.Length || !Uri.IsHexDigit(v[i + 1]) || !Uri.IsHexDigit(v[i + 2])) return Err("Gs1.Lint.PercentEncoding", ai);
            i += 2;
        }
        return null;
    }

    private static ValidationIssue? MediaType(string v, string ai)
    {
        // 00 is not used, 01-10 ICCBBA, 80-99 local or national use; the rest is reserved for later assignment.
        if (v.Length == 2 && Digits(v) && int.Parse(v, NumberStyles.None, CultureInfo.InvariantCulture) is var n && (n is >= 1 and <= 10 || n >= 80))
            return null;
        return Err("Gs1.Lint.MediaType", ai, v);
    }

    private static ValidationIssue? Iban(string v, string ai)
    {
        // The limits are the ones of the reference test: no clear minimum, 34 characters at most (ISO 13616).
        if (v.Length < 4 || v.Length <= 10 || v.Length > 34) return Err("Gs1.Lint.Iban", ai);
        if (!Gs1CodeTables.IsCountryAlpha2(v[..2])) return Err("Gs1.Lint.Iban", ai);
        int sum = 0;
        for (int i = 4; i < v.Length + 4; i++)
        {
            char c = v[i < v.Length ? i : i - v.Length];
            int weight = c is >= '0' and <= '9' ? c - '0' + 1 : c is >= 'A' and <= 'Z' ? c - 'A' + 11 : 0;
            if (weight == 0) return Err("Gs1.Lint.Iban", ai);
            sum = (sum * (weight <= 10 ? 10 : 100) + weight - 1) % 97;
        }
        return sum == 1 ? null : Err("Gs1.Lint.Iban", ai);
    }
}
