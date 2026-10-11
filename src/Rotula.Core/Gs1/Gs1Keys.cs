using System.Globalization;
using Rotula.Core.Barcodes;
using Rotula.Core.Validation;

namespace Rotula.Core.Gs1;

/// <summary>Why a GS1 identification key could not be built.</summary>
public sealed class Gs1KeyException : ArgumentException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="messageKey">The localisable key of the problem, for example <c>Gs1.Key.CompanyPrefix</c>.</param>
    /// <param name="message">English text for logs.</param>
    /// <param name="args">Culture-neutral arguments of the localised message.</param>
    public Gs1KeyException(string messageKey, string message, params string[] args) : base(message)
    {
        MessageKey = messageKey;
        Args = args;
    }

    /// <summary>The message key, <c>Gs1.Key.*</c>.</summary>
    public string MessageKey { get; }

    /// <summary>Arguments of the localised message.</summary>
    public IReadOnlyList<string> Args { get; }
}

/// <summary>
/// Builders and checks for the GS1 identification keys: GTIN-8/12/13/14, SSCC, GLN, GSRN, GSIN, GRAI, GDTI, GIAI and GINC. The check
/// digit follows GS1 General Specifications 7.9.1 (modulus 10, weights 3 and 1 from the right).
/// </summary>
public static class Gs1Keys
{
    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static bool AllDigits(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');

    /// <summary>The GS1 check digit (0-9) for <paramref name="payload"/>, the key without its last digit.</summary>
    /// <exception cref="Gs1KeyException">The text is empty or has a character that is not a digit.</exception>
    public static int CheckDigit(string payload)
    {
        if (!AllDigits(payload)) throw new Gs1KeyException("Gs1.Key.Digits", "Only digits are allowed.");
        return CheckDigits.Gs1(payload);
    }

    /// <summary>True when the last digit of <paramref name="key"/> is the correct check digit for the others.</summary>
    public static bool HasValidCheckDigit(string key) => key.Length >= 2 && AllDigits(key) && CheckDigits.IsValidGs1(key);

    /// <summary><paramref name="payload"/> followed by its check digit.</summary>
    public static string AppendCheckDigit(string payload) => payload + CheckDigit(payload).ToString(CultureInfo.InvariantCulture);

    // ---- GTIN --------------------------------------------------------------------------------------

    /// <summary>True when <paramref name="digits"/> is a GTIN-8, GTIN-12, GTIN-13 or GTIN-14 with a correct check digit.</summary>
    public static bool IsValidGtin(string digits) => digits.Length is 8 or 12 or 13 or 14 && HasValidCheckDigit(digits);

    /// <summary>
    /// Completes a GTIN: a text of 7, 11, 12 or 13 digits gets its check digit; a text of 8, 12, 13 or 14 digits must already end in a
    /// correct one. (12 and 13 digits are ambiguous: both are tried as the shorter key without a check digit first only when the full
    /// length is not already valid, so a valid GTIN-12 or GTIN-13 is returned unchanged.)
    /// </summary>
    /// <exception cref="Gs1KeyException">The digits are not a GTIN.</exception>
    public static string CompleteGtin(string digits)
    {
        if (!AllDigits(digits)) throw new Gs1KeyException("Gs1.Key.Digits", "Only digits are allowed.");
        if (IsValidGtin(digits)) return digits;
        if (digits.Length is 7 or 11 or 12 or 13) return AppendCheckDigit(digits);
        if (digits.Length is 8 or 14) throw new Gs1KeyException("Gs1.Key.CheckDigit", "The check digit is wrong.", Num(CheckDigit(digits[..^1])));
        throw new Gs1KeyException("Gs1.Key.GtinLength", "A GTIN has 8, 12, 13 or 14 digits.");
    }

    /// <summary>The GTIN-14 form of a GTIN-8, 12, 13 or 14 (zeros added on the left), as AI 01 and ITF-14 carry it.</summary>
    /// <exception cref="Gs1KeyException">The digits are not a valid GTIN.</exception>
    public static string ToGtin14(string gtin)
    {
        if (!AllDigits(gtin) || gtin.Length is not (8 or 12 or 13 or 14)) throw new Gs1KeyException("Gs1.Key.GtinLength", "A GTIN has 8, 12, 13 or 14 digits.");
        if (!HasValidCheckDigit(gtin)) throw new Gs1KeyException("Gs1.Key.CheckDigit", "The check digit is wrong.", Num(CheckDigit(gtin[..^1])));
        return gtin.PadLeft(14, '0');
    }

    /// <summary>
    /// Builds the GTIN-14 of a carton or case from the GTIN-13 (or 12, or 8) of the trade items inside: the packaging indicator digit
    /// 1-8 (9 means variable measure), then the item's number without its check digit, then a new check digit.
    /// </summary>
    /// <param name="indicator">The packaging indicator digit, 0 to 9.</param>
    /// <param name="innerGtin">The inner GTIN with its check digit, or without it when it has 7, 11 or 12 digits.</param>
    /// <exception cref="Gs1KeyException">The indicator is not a digit or the inner GTIN is wrong.</exception>
    public static string Gtin14FromInner(int indicator, string innerGtin)
    {
        if (indicator is < 0 or > 9) throw new Gs1KeyException("Gs1.Key.Indicator", "The indicator digit is 0 to 9.");
        string full = CompleteGtin(innerGtin);
        if (full.Length == 14) throw new Gs1KeyException("Gs1.Key.GtinLength", "The inner key must be a GTIN-8, 12 or 13.");
        // Without its check digit the inner key has at most 12 digits; shorter keys are padded with zeros on the left so that the
        // 14-digit layout (indicator, 12 digits, check digit) holds.
        string payload = indicator.ToString(CultureInfo.InvariantCulture) + full[..^1].PadLeft(12, '0');
        return AppendCheckDigit(payload);
    }

    // ---- keys made of a GS1 Company Prefix and a reference -----------------------------------------

    private static string Compose(string companyPrefix, string reference, int totalWithoutCheck, string prefixDigit, bool addCheckDigit, string keyName)
    {
        if (!AllDigits(companyPrefix) || companyPrefix.Length is < 4 or > 12)
            throw new Gs1KeyException("Gs1.Key.CompanyPrefix", "A GS1 Company Prefix has 4 to 12 digits.");
        if (!AllDigits(reference)) throw new Gs1KeyException("Gs1.Key.Reference", "The reference must be digits.");
        int refLength = totalWithoutCheck - prefixDigit.Length - companyPrefix.Length;
        if (refLength < 1) throw new Gs1KeyException("Gs1.Key.CompanyPrefixTooLong", $"The company prefix leaves no room for a {keyName} reference.");
        if (reference.Length > refLength)
            throw new Gs1KeyException("Gs1.Key.ReferenceTooLong", $"With this company prefix the {keyName} reference has at most {refLength} digits.", Num(refLength));
        string payload = prefixDigit + companyPrefix + reference.PadLeft(refLength, '0');
        return addCheckDigit ? AppendCheckDigit(payload) : payload;
    }

    /// <summary>
    /// Builds an SSCC (AI 00, 18 digits): the extension digit, the GS1 Company Prefix, the serial reference padded with zeros on the left to
    /// fill 16 digits in all, and the check digit (GS1 General Specifications 3.3).
    /// </summary>
    /// <param name="extensionDigit">The extension digit 0-9 that the company assigns freely to extend the serial capacity.</param>
    /// <param name="companyPrefix">The GS1 Company Prefix, 4 to 12 digits.</param>
    /// <param name="serialReference">The serial reference as digits; as many as 16 minus the prefix length.</param>
    /// <exception cref="Gs1KeyException">An input does not fit.</exception>
    public static string Sscc(int extensionDigit, string companyPrefix, string serialReference)
    {
        if (extensionDigit is < 0 or > 9) throw new Gs1KeyException("Gs1.Key.Extension", "The extension digit is 0 to 9.");
        return Compose(companyPrefix, serialReference, 17, extensionDigit.ToString(CultureInfo.InvariantCulture), true, "serial");
    }

    /// <summary>SSCC overload for a numeric serial reference.</summary>
    public static string Sscc(int extensionDigit, string companyPrefix, long serialReference) =>
        Sscc(extensionDigit, companyPrefix, serialReference < 0 ? "x" : serialReference.ToString(CultureInfo.InvariantCulture));

    /// <summary>Builds a GLN (13 digits): the GS1 Company Prefix, the location reference padded with zeros, and the check digit.</summary>
    public static string Gln(string companyPrefix, string locationReference) => Compose(companyPrefix, locationReference, 12, string.Empty, true, "location");

    /// <summary>Builds a GSRN (AI 8017 or 8018, 18 digits): the GS1 Company Prefix, the service reference, and the check digit.</summary>
    public static string Gsrn(string companyPrefix, string serviceReference) => Compose(companyPrefix, serviceReference, 17, string.Empty, true, "service");

    /// <summary>Builds a GSIN (AI 402, 17 digits): the GS1 Company Prefix, the shipper reference, and the check digit.</summary>
    public static string Gsin(string companyPrefix, string shipperReference) => Compose(companyPrefix, shipperReference, 16, string.Empty, true, "shipper");

    /// <summary>
    /// Builds the value of a GRAI (AI 8003): a zero, the GS1 Company Prefix and asset type padded to 12 digits, the check digit, and an
    /// optional serial number of up to 16 characters of the GS1 character set.
    /// </summary>
    public static string Grai(string companyPrefix, string assetType, string serial = "")
    {
        string head = Compose(companyPrefix, assetType, 12, string.Empty, true, "asset type");
        CheckSerial(serial, 16);
        return "0" + head + serial;
    }

    /// <summary>
    /// Builds the value of a GDTI (AI 253): the GS1 Company Prefix and document type padded to 12 digits, the check digit, and an optional
    /// serial number of up to 17 characters of the GS1 character set.
    /// </summary>
    public static string Gdti(string companyPrefix, string documentType, string serial = "")
    {
        string head = Compose(companyPrefix, documentType, 12, string.Empty, true, "document type");
        CheckSerial(serial, 17);
        return head + serial;
    }

    /// <summary>Builds the value of a GIAI (AI 8004): the GS1 Company Prefix followed by the individual asset reference, 30 characters at most.</summary>
    public static string Giai(string companyPrefix, string assetReference)
    {
        if (!AllDigits(companyPrefix) || companyPrefix.Length is < 4 or > 12) throw new Gs1KeyException("Gs1.Key.CompanyPrefix", "A GS1 Company Prefix has 4 to 12 digits.");
        if (assetReference.Length == 0) throw new Gs1KeyException("Gs1.Key.Reference", "The reference must not be empty.");
        CheckSerial(assetReference, 30 - companyPrefix.Length);
        return companyPrefix + assetReference;
    }

    /// <summary>Builds the value of a GINC (AI 401): the GS1 Company Prefix followed by the consignment reference, 30 characters at most.</summary>
    public static string Ginc(string companyPrefix, string consignmentReference) => Giai(companyPrefix, consignmentReference);

    private static void CheckSerial(string serial, int max)
    {
        if (serial.Length > max) throw new Gs1KeyException("Gs1.Key.ReferenceTooLong", $"At most {max} characters are allowed.", Num(max));
        foreach (char c in serial)
            if (!Gs1Linters.IsCset82(c)) throw new Gs1KeyException("Gs1.Key.Characters", "A character is not in the GS1 character set.", c.ToString());
    }

    // ---- variable measures -------------------------------------------------------------------------

    private static decimal Pow10(int n)
    {
        decimal r = 1;
        for (int i = 0; i < n; i++) r *= 10;
        return r;
    }

    /// <summary>
    /// Builds the element for a measure such as a net weight: AI 310 plus the number of decimals (3103 = three decimals), with the value
    /// written as six digits without a decimal point (GS1 General Specifications 3.2, decimal point indicator in the fourth digit).
    /// </summary>
    /// <param name="aiWithoutDecimals">The first three digits of the identifier, for example <c>310</c> (net weight in kg).</param>
    /// <param name="value">The measure; it is rounded to <paramref name="decimals"/> places.</param>
    /// <param name="decimals">Digits after the decimal point, 0 to 5 (0 to 9 for amounts such as 390).</param>
    /// <exception cref="Gs1KeyException">The identifier is not a measure, or the value does not fit in six digits.</exception>
    public static Gs1Element Measure(string aiWithoutDecimals, decimal value, int decimals)
    {
        string ai = aiWithoutDecimals + decimals.ToString(CultureInfo.InvariantCulture);
        var def = decimals is >= 0 and <= 9 ? Gs1Dictionary.Find(ai) : null;
        if (def is null || !def.DecimalPlacesInAi || aiWithoutDecimals.Length != 3 || !def.IsNumeric || def.Components.Count != 1)
            throw new Gs1KeyException("Gs1.Key.NotMeasure", "The identifier does not carry a decimal point indicator.", ai);
        if (value < 0) throw new Gs1KeyException("Gs1.Key.MeasureRange", "The measure cannot be negative.", Num(def.MaxValueLength));
        string digits = Math.Round(value * Pow10(decimals), MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        if (digits.Length > def.MaxValueLength) throw new Gs1KeyException("Gs1.Key.MeasureRange", "The measure has too many digits.", Num(def.MaxValueLength));
        // Measures of fixed length (6 digits) are filled with zeros on the left; amounts of variable length (AI 390n) are written as they are.
        return new Gs1Element(ai, def.HasFixedValueLength ? digits.PadLeft(def.MaxValueLength, '0') : digits);
    }

    /// <summary>The measure of an element built by <see cref="Measure"/>, with the decimal point put back; null when the element is not a measure.</summary>
    public static decimal? ReadMeasure(Gs1Element element)
    {
        var def = Gs1Dictionary.Find(element.Ai);
        if (def is null || !def.DecimalPlacesInAi || def.Components.Count != 1 || !AllDigits(element.Value)) return null;
        return decimal.Parse(element.Value, NumberStyles.None, CultureInfo.InvariantCulture) / Pow10(element.Ai[^1] - '0');
    }
}
