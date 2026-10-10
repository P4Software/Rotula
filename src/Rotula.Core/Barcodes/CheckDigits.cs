using System.Text;

namespace Rotula.Core.Barcodes;

/// <summary>
/// Check digit and check character algorithms of the supported symbologies. They are in Core because both the validator
/// (is the typed check digit right?) and the preview encoders (what does the printer append?) need them.
/// </summary>
public static class CheckDigits
{
    /// <summary>
    /// Computes the GS1 check digit for a payload of digits (EAN-13: 12 digits, UPC-A: 11 digits).
    /// Weights alternate 3,1,3,1 starting with 3 at the rightmost payload digit.
    /// </summary>
    public static int Gs1(string payload)
    {
        int sum = 0;
        bool three = true;
        for (int i = payload.Length - 1; i >= 0; i--)
        {
            sum += (payload[i] - '0') * (three ? 3 : 1);
            three = !three;
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>True when the last digit of <paramref name="full"/> is the correct GS1 check digit for the rest.</summary>
    public static bool IsValidGs1(string full) =>
        full.Length >= 2 && Gs1(full[..^1]) == full[^1] - '0';

    /// <summary>The Code 39 character set in modulo 43 value order (the position is the character value).</summary>
    public const string Code39Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    /// <summary>
    /// The Code 39 modulo 43 check character of <paramref name="data"/> (upper-case characters of <see cref="Code39Alphabet"/>).
    /// Returns null when a character is outside the alphabet. Reference: the worked example in the ZPL guide,
    /// "2345ABCDE/" gives T.
    /// </summary>
    public static char? Mod43(string data)
    {
        int sum = 0;
        foreach (char c in data)
        {
            int index = Code39Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (index < 0) return null;
            sum += index;
        }
        return Code39Alphabet[sum % 43];
    }

    /// <summary>
    /// Code 11 check characters: the "C" character (weights 1 to 10 from the right) and, when <paramref name="count"/> is 2,
    /// the "K" character (weights 1 to 9 from the right, over the data plus C). A value of 10 is written as the hyphen.
    /// Source: ANSI/AIM / USD-8 definition as summarised in the public Code 11 description.
    /// </summary>
    public static string Code11(string data, int count)
    {
        int Value(char c) => c == '-' ? 10 : c - '0';
        char Symbol(int v) => v == 10 ? '-' : (char)('0' + v);

        int Weighted(string text, int cycle)
        {
            int sum = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int weight = (text.Length - 1 - i) % cycle + 1;
                sum += Value(text[i]) * weight;
            }
            return sum % 11;
        }

        var sb = new StringBuilder();
        char c1 = Symbol(Weighted(data, 10));
        sb.Append(c1);
        if (count >= 2) sb.Append(Symbol(Weighted(data + c1, 9)));
        return sb.ToString();
    }

    /// <summary>
    /// MSI modulo 10 check digit (IBM): starting at the rightmost digit every second digit is doubled, the digits of all
    /// products and the untouched digits are added, and the check digit completes the sum to a multiple of ten.
    /// </summary>
    public static int MsiMod10(string digits)
    {
        int sum = 0;
        bool doubled = true;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (doubled)
            {
                d *= 2;
                sum += d / 10 + d % 10;
            }
            else sum += d;
            doubled = !doubled;
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>
    /// MSI modulo 11 check value (IBM): weights 2 to 7 repeating from the rightmost digit. The result can be 10, which the
    /// symbology writes as the two digits "10"; callers use <see cref="MsiMod11Text"/> for the characters.
    /// </summary>
    public static int MsiMod11(string digits)
    {
        int sum = 0, weight = 2;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 7 ? 2 : weight + 1;
        }
        return (11 - sum % 11) % 11;
    }

    /// <summary>The characters MSI appends for <see cref="MsiMod11"/> ("10" when the value is ten).</summary>
    public static string MsiMod11Text(string digits) => MsiMod11(digits).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Plessey check: the 8-bit CRC with generator x^8+x^7+x^6+x^5+x^3+1 over the data bits (each hex character as four bits,
    /// least significant bit first). Returned as the eight bits in transmission order. Verified against the printed sample of
    /// "12345" in the ZPL guide (CRC bits 01100111) and against ZXing.Net's Plessey writer.
    /// </summary>
    public static bool[] PlesseyCrcBits(string hexData)
    {
        // Generator coefficients from the highest power (x^8) down to x^0.
        ReadOnlySpan<int> generator = stackalloc int[] { 1, 1, 1, 1, 0, 1, 0, 0, 1 };
        var bits = new int[hexData.Length * 4 + 8];
        for (int i = 0; i < hexData.Length; i++)
        {
            int v = Convert.ToInt32(hexData[i].ToString(), 16);
            for (int b = 0; b < 4; b++) bits[i * 4 + b] = (v >> b) & 1;
        }
        // Long division by the generator; the eight trailing bits end up holding the remainder.
        for (int i = 0; i < hexData.Length * 4; i++)
            if (bits[i] == 1)
                for (int j = 0; j < 9; j++) bits[i + j] ^= generator[j];
        var crc = new bool[8];
        for (int i = 0; i < 8; i++) crc[i] = bits[hexData.Length * 4 + i] == 1;
        return crc;
    }

    /// <summary>The Plessey check as two hex characters (each character's bits are least significant first).</summary>
    public static string PlesseyCheckText(string hexData)
    {
        var crc = PlesseyCrcBits(hexData);
        var sb = new StringBuilder(2);
        for (int c = 0; c < 2; c++)
        {
            int v = 0;
            for (int b = 0; b < 4; b++) if (crc[c * 4 + b]) v |= 1 << b;
            sb.Append("0123456789ABCDEF"[v]);
        }
        return sb.ToString();
    }
}

/// <summary>Why a UPC-E value was rejected by <see cref="UpcE.TryNormalize"/>.</summary>
public enum UpcEProblem
{
    /// <summary>The digit count is not one of the accepted spellings.</summary>
    Length,
    /// <summary>The number system digit is not 0 (ZPL's ^B9 only prints number system 0).</summary>
    NumberSystem,
    /// <summary>The 10-digit code cannot be zero-suppressed into six digits.</summary>
    NotCompressible,
    /// <summary>The typed check digit is wrong.</summary>
    CheckDigit,
}

/// <summary>UPC-E helpers: ZPL's <c>^B9</c> takes the 10-digit manufacturer + product code and prints the zero-suppressed form.</summary>
public static class UpcE
{
    /// <summary>
    /// Brings any accepted spelling of a UPC-E value to the 10-digit manufacturer (5) + product (5) code ZPL expects.
    /// Accepted: 6 digits (the printed UPC-E body), 7 (number system 0 + body), 8 (plus check digit), 10, 11 (number system 0 + 10)
    /// and 12 (plus check digit).
    /// </summary>
    /// <param name="data">The digits.</param>
    /// <param name="ten">The 10-digit code when this returns null.</param>
    /// <returns>Null on success, otherwise the reason the value was rejected.</returns>
    public static UpcEProblem? TryNormalize(string data, out string ten)
    {
        ten = string.Empty;
        string body; // number system + six digits (7 digits) or number system + ten digits (11 digits)
        switch (data.Length)
        {
            case 6: return FromSix(data, out ten);
            case 7: body = data; break;
            case 8: body = data[..7]; break;
            case 10: ten = data; return Compress(ten) is null ? UpcEProblem.NotCompressible : null;
            case 11: body = data; break;
            case 12: body = data[..11]; break;
            default: return UpcEProblem.Length;
        }
        if (body[0] != '0') return UpcEProblem.NumberSystem;
        if (data.Length <= 8)
        {
            if (FromSix(body[1..7], out ten) is { } problem) return problem;
        }
        else
        {
            ten = body[1..11];
            if (Compress(ten) is null) return UpcEProblem.NotCompressible;
        }
        if (data.Length is 8 or 12 && CheckDigit(ten) != data[^1] - '0') return UpcEProblem.CheckDigit;
        return null;
    }

    /// <summary>
    /// Expands a printed six-digit UPC-E body. A body whose last digit contradicts its other digits (for example 120003, whose
    /// third digit would have to be 3 to 9) expands to a code that compresses to a different body, so it is not a UPC-E symbol.
    /// </summary>
    private static UpcEProblem? FromSix(string body, out string ten)
    {
        ten = Expand(body);
        return Compress(ten) == body ? null : UpcEProblem.NotCompressible;
    }

    /// <summary>The check digit of the 10-digit code (the UPC-A check digit of number system 0 plus the code).</summary>
    public static int CheckDigit(string ten) => CheckDigits.Gs1("0" + ten);

    /// <summary>
    /// Expands a 6-digit UPC-E body to the 10-digit manufacturer + product code, following the zero-suppression table of the
    /// UPC specification (the last UPC-E digit says where the removed zeros were).
    /// </summary>
    public static string Expand(string body)
    {
        char last = body[5];
        return last switch
        {
            '0' or '1' or '2' => $"{body[0]}{body[1]}{last}00" + $"00{body[2]}{body[3]}{body[4]}",
            '3' => $"{body[0]}{body[1]}{body[2]}00" + $"000{body[3]}{body[4]}",
            '4' => $"{body[0]}{body[1]}{body[2]}{body[3]}0" + $"0000{body[4]}",
            _ => $"{body[0]}{body[1]}{body[2]}{body[3]}{body[4]}" + $"0000{last}",
        };
    }

    /// <summary>The 6-digit UPC-E body for a 10-digit code, or null when the code cannot be zero-suppressed.</summary>
    public static string? Compress(string ten)
    {
        if (ten.Length != 10 || ten.Any(c => c is < '0' or > '9')) return null;
        string m = ten[..5], p = ten[5..];
        if (m[2] <= '2' && m[3] == '0' && m[4] == '0' && p[0] == '0' && p[1] == '0')
            return $"{m[0]}{m[1]}{p[2]}{p[3]}{p[4]}{m[2]}";
        if (m[3] == '0' && m[4] == '0' && m[2] >= '3' && p[..3] == "000")
            return $"{m[0]}{m[1]}{m[2]}{p[3]}{p[4]}3";
        if (m[4] == '0' && m[3] != '0' && p[..4] == "0000")
            return $"{m[0]}{m[1]}{m[2]}{m[3]}{p[4]}4";
        if (m[4] != '0' && p[..4] == "0000" && p[4] >= '5')
            return $"{m}{p[4]}";
        return null;
    }
}
