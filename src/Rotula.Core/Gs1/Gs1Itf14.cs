using Rotula.Core.Validation;

namespace Rotula.Core.Gs1;

/// <summary>
/// ITF-14 and EAN-14: the GTIN-14 of a case or carton as an Interleaved 2 of 5 symbol (ITF-14) or in a GS1-128 symbol with AI 01 (EAN-14).
/// </summary>
/// <remarks>
/// In Rotula an ITF-14 is an Interleaved 2 of 5 barcode whose data is the 14 digits including the check digit, with the check digit option
/// off (the printer must not add a 15th digit). ZPL's <c>^B2</c> has no parameter for the bearer bars (the frame around the symbol that
/// ITF-14 uses to even out printing pressure); draw them with a box element around the barcode. See docs/gs1.md.
/// </remarks>
public static class Gs1Itf14
{
    /// <summary>Checks the 14 digits of an ITF-14: digits only, exactly 14, correct GS1 check digit. Returns null when fine.</summary>
    public static ValidationIssue? Validate(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != 14 || !data.All(char.IsAsciiDigit)) return new ValidationIssue(ValidationSeverity.Error, "Gs1.Itf14.Length");
        if (!Gs1Keys.HasValidCheckDigit(data))
            return new ValidationIssue(ValidationSeverity.Error, "Gs1.Itf14.CheckDigit", null, Gs1Keys.CheckDigit(data[..^1]).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>The 14 digits to put into the Interleaved 2 of 5 barcode for any GTIN (8, 12, 13 or 14 digits with a correct check digit).</summary>
    public static string Data(string gtin) => Gs1Keys.ToGtin14(gtin);

    /// <summary>The ITF-14 digits for a carton that holds items with GTIN <paramref name="innerGtin"/>, with packaging indicator 0-9.</summary>
    public static string Data(int indicator, string innerGtin) => Gs1Keys.Gtin14FromInner(indicator, innerGtin);

    /// <summary>The element string of an EAN-14 symbol (GS1-128 with AI 01) for any GTIN, written <c>(01)...</c>.</summary>
    public static string Ean14(string gtin) => new Gs1Message(new[] { new Gs1Element("01", Gs1Keys.ToGtin14(gtin)) }).ToBracketed();
}
