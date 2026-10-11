using Rotula.Core.Validation;

namespace Rotula.Core.Gs1;

/// <summary>
/// The data of a GS1 composite symbol as ZPL's <c>^BR</c> (types 7-12) takes it: the linear component, a vertical bar <c>|</c>, and the
/// two-dimensional (CC-A/B/C) component. The linear component is a GTIN for the EAN/UPC/DataBar families, or an element string for
/// GS1-128; the 2D component is always an element string written with parentheses. Rotula does not print composite symbols yet
/// (see <c>Barcode.DataBar.Composite</c>), but this class lets the data be checked and prepared.
/// </summary>
public static class Gs1Composite
{
    /// <summary>The separator between the linear and the 2D component in ZPL field data.</summary>
    public const char Separator = '|';

    /// <summary>Splits composite field data at the first vertical bar; false when there is none.</summary>
    public static bool TrySplit(string data, out string linear, out string composite)
    {
        ArgumentNullException.ThrowIfNull(data);
        int bar = data.IndexOf(Separator, StringComparison.Ordinal);
        if (bar < 0) { linear = data; composite = string.Empty; return false; }
        linear = data[..bar];
        composite = data[(bar + 1)..];
        return true;
    }

    /// <summary>Joins the two components with the separator.</summary>
    public static string Join(string linear, string composite) => linear + Separator + composite;

    /// <summary>
    /// Checks the 2D component and its relation to the linear one. The component must be a bracketed element string that passes
    /// <see cref="Gs1Validator"/>; it must not repeat an identifier the linear component already carries (a GTIN in a plain EAN/UPC/DataBar
    /// linear component counts as AI 01).
    /// </summary>
    /// <param name="linear">The linear component: digits (a GTIN, 8 to 14 of them) or a bracketed element string.</param>
    /// <param name="composite">The 2D component.</param>
    /// <param name="options">Settings of the content tests.</param>
    public static List<ValidationIssue> Validate(string linear, string composite, Gs1LintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(linear);
        ArgumentNullException.ThrowIfNull(composite);
        var issues = new List<ValidationIssue>();
        var twoD = Gs1Message.ParseBracketed(composite, out var parseIssues);
        issues.AddRange(parseIssues);
        if (twoD is null) return issues;

        Gs1Message? linearMessage = null;
        if (linear.Length > 0 && linear.All(char.IsAsciiDigit))
        {
            // A plain GTIN stands for AI 01 in the combined data; the composite component must then not carry another one.
            if (linear.Length is 8 or 12 or 13 or 14 && Gs1Keys.HasValidCheckDigit(linear)) linearMessage = new Gs1Message(new[] { new Gs1Element("01", linear.PadLeft(14, '0')) });
            else issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Composite.Linear", null, linear));
        }
        else
        {
            linearMessage = Gs1Message.ParseBracketed(linear, out var linearIssues);
            issues.AddRange(linearIssues);
        }

        var all = linearMessage is null ? twoD : new Gs1Message(linearMessage.Elements.Concat(twoD.Elements));
        issues.AddRange(Gs1Validator.Validate(all, options));
        if (linearMessage is not null)
            foreach (var dup in twoD.Elements.Select(e => e.Ai).Intersect(linearMessage.Elements.Select(e => e.Ai)))
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Composite.RepeatedAi", null, dup));
        return issues;
    }
}
