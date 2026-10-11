using System.Globalization;
using Rotula.Core.Validation;

namespace Rotula.Core.Gs1;

/// <summary>
/// Checks a <see cref="Gs1Message"/> against the GS1 Syntax Dictionary: known identifiers, value lengths and characters, the content tests
/// (check digits, dates, country codes, ...), repeated identifiers, and the combinations the dictionary forbids or requires.
/// The result is a list of issues with message keys and culture-neutral arguments; the first argument is the application identifier.
/// </summary>
/// <remarks>
/// A missing required companion (<c>req=</c> in the dictionary) is a warning, not an error, because the dictionary says the association is
/// judged over all the barcodes of the item together and a label often carries them in separate symbols (for example an SSCC symbol and a
/// symbol with the contents).
/// </remarks>
public static class Gs1Validator
{
    /// <summary>The most data characters a GS1-128 symbol may encode (GS1 General Specifications 5.4.4.3).</summary>
    public const int MaxGs128DataCharacters = 48;

    /// <summary>Checks the elements of <paramref name="message"/> and the relations between them.</summary>
    public static List<ValidationIssue> Validate(Gs1Message message, Gs1LintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var issues = new List<ValidationIssue>();
        foreach (var element in message.Elements) issues.AddRange(ValidateElement(element, options));
        issues.AddRange(ValidateRelations(message));
        return issues;
    }

    /// <summary>
    /// Checks <paramref name="message"/> and then the limit of GS1-128: 48 data characters including identifiers and the FNC1 separators.
    /// </summary>
    public static List<ValidationIssue> ValidateForGs128(Gs1Message message, Gs1LintOptions? options = null)
    {
        var issues = Validate(message, options);
        int count = message.DataCharacterCount;
        if (count > MaxGs128DataCharacters)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Gs128.TooLong", null,
                count.ToString(CultureInfo.InvariantCulture), MaxGs128DataCharacters.ToString(CultureInfo.InvariantCulture)));
        return issues;
    }

    /// <summary>Checks one element: the identifier exists, the value has the right length, characters and content.</summary>
    public static List<ValidationIssue> ValidateElement(Gs1Element element, Gs1LintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        var issues = new List<ValidationIssue>();
        var def = Gs1Dictionary.Find(element.Ai);
        if (def is null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Ai.Unknown", null, element.Ai));
            return issues;
        }

        string value = element.Value;
        if (value.Length == 0)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Value.TooShort", null, def.Ai, Num(Math.Max(1, def.MinValueLength))));
            return issues;
        }

        // The dictionary applies the components in order, each taking a prefix of its length; optional ones may be skipped once the
        // data is used up, and variable-length ones take what remains up to their maximum.
        int pos = 0;
        foreach (var c in def.Components)
        {
            int remaining = value.Length - pos;
            if (remaining == 0)
            {
                if (c.Optional) continue;
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Value.TooShort", null, def.Ai, Num(def.MinValueLength)));
                return issues;
            }
            int take = c.IsFixedLength ? c.MaxLength : Math.Min(remaining, c.MaxLength);
            if (take > remaining || take < c.MinLength)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Value.TooShort", null, def.Ai, Num(def.MinValueLength)));
                return issues;
            }
            string part = value.Substring(pos, take);
            pos += take;
            var charIssue = Gs1Linters.CheckCharacterSet(c.CharacterSet, part, def.Ai);
            if (charIssue is not null) { issues.Add(charIssue); return issues; }
            foreach (var linter in c.Linters)
            {
                var problem = Gs1Linters.Lint(linter, part, def.Ai, options);
                if (problem is not null) { issues.Add(problem); return issues; }
            }
        }
        if (pos < value.Length)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Value.TooLong", null, def.Ai, Num(def.MaxValueLength)));
        return issues;
    }

    /// <summary>Checks repeated identifiers and the <c>ex=</c> and <c>req=</c> relations of the dictionary over the elements of the message.</summary>
    public static List<ValidationIssue> ValidateRelations(Gs1Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var issues = new List<ValidationIssue>();
        var els = message.Elements;

        foreach (var group in els.GroupBy(e => e.Ai).Where(g => g.Count() > 1))
        {
            if (group.Select(e => e.Value).Distinct(StringComparer.Ordinal).Count() > 1)
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Message.Duplicate", null, group.Key));
            else
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Gs1.Message.DuplicateSame", null, group.Key));
        }

        var present = els.Select(e => e.Ai).Distinct(StringComparer.Ordinal).ToList();
        var reportedPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ai in present)
        {
            var def = Gs1Dictionary.Find(ai);
            if (def is null) continue;

            foreach (var pattern in def.Excludes)
                foreach (var other in present.Where(o => o != ai && Gs1Dictionary.Matches(pattern, o)))
                {
                    // Report a mutually exclusive pair once, whichever side states the rule.
                    string pair = string.CompareOrdinal(ai, other) < 0 ? ai + "/" + other : other + "/" + ai;
                    if (reportedPairs.Add(pair))
                        issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Message.Excludes", null, ai, other));
                }

            foreach (var requirement in def.Requires)
            {
                bool met = requirement.Any(alternative => alternative.All(pattern => present.Any(p => Gs1Dictionary.Matches(pattern, p))));
                if (!met)
                    issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Gs1.Message.Requires", null, ai,
                        string.Join(',', requirement.Select(alt => string.Join('+', alt)))));
            }
        }
        return issues;
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
