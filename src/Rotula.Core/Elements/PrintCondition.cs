using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Core.Elements;

/// <summary>How a <see cref="PrintCondition"/> compares the value of its field with its text.</summary>
public enum ConditionOperator
{
    /// <summary>The field equals the text (ignoring case and surrounding spaces).</summary>
    Equals,
    /// <summary>The field differs from the text (ignoring case and surrounding spaces).</summary>
    NotEquals,
    /// <summary>The field is empty or only spaces; the text is not used.</summary>
    IsEmpty,
    /// <summary>The field has something other than spaces in it; the text is not used.</summary>
    IsNotEmpty,
    /// <summary>The field contains the text (ignoring case).</summary>
    Contains,
}

/// <summary>
/// A simple rule that decides, record by record, whether an object is printed: "print only when <see cref="Field"/>
/// <see cref="Operator"/> <see cref="Value"/>". Evaluated against the same values that fill the <c>{{field}}</c>
/// placeholders of that record, so data columns, declared variables and resolved dates/counters all work.
/// </summary>
public sealed class PrintCondition
{
    /// <summary>Name of the data field or variable to look at (the name used inside <c>{{ }}</c>, without the braces).</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>The comparison.</summary>
    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;

    /// <summary>The text to compare with. Ignored by <see cref="ConditionOperator.IsEmpty"/> and <see cref="ConditionOperator.IsNotEmpty"/>.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Longest field name or value text a file may carry.</summary>
    public const int MaxTextLength = 255;

    /// <summary>True when <see cref="Operator"/> compares with <see cref="Value"/> (so a UI knows whether to ask for it).</summary>
    public bool UsesValue => Operator is ConditionOperator.Equals or ConditionOperator.NotEquals or ConditionOperator.Contains;

    /// <summary>A copy that can be changed without touching this one.</summary>
    public PrintCondition Clone() => new() { Field = Field, Operator = Operator, Value = Value };

    /// <summary>
    /// The field name as it should be looked up: surrounding spaces removed and, when a whole <c>{{name}}</c> placeholder
    /// was typed or picked, the braces removed. Why: a field picker may hand over a template, and a rule on "{{Status}}"
    /// should mean the field "Status".
    /// </summary>
    public static string NormalizeField(string? field)
    {
        string name = (field ?? string.Empty).Trim();
        if (name.Length >= 4 && name.StartsWith("{{", StringComparison.Ordinal) && name.EndsWith("}}", StringComparison.Ordinal))
            name = name[2..^2].Trim();
        return name;
    }

    /// <summary>Applies the operator to the already-looked-up text of the field (null or empty counts as empty).</summary>
    /// <remarks>
    /// Comparisons ignore case and the spaces around the text, because spreadsheets and databases routinely differ in both
    /// ("Yes", "yes ", "YES") and a label should not change because of it. Contains with an empty text is always true.
    /// </remarks>
    public bool Matches(string? fieldValue)
    {
        string actual = (fieldValue ?? string.Empty).Trim();
        string expected = (Value ?? string.Empty).Trim();
        return Operator switch
        {
            ConditionOperator.Equals => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.IsEmpty => actual.Length == 0,
            ConditionOperator.IsNotEmpty => actual.Length > 0,
            ConditionOperator.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }
}

/// <summary>Decides whether an element is part of the printed label for one record.</summary>
public static class ElementPrinting
{
    /// <summary>Message key of the warning raised when a condition names a field that no record column and no variable provides.</summary>
    public const string ConditionFieldMissing = "Element.Condition.FieldMissing";

    /// <summary>
    /// True when the element is part of the printed label: it is not marked "do not print" and its condition (if any) holds.
    /// The field is looked up exactly as a <c>{{field}}</c> placeholder is (record data first, then the declared sample value);
    /// a field found nowhere counts as empty and, when <paramref name="issues"/> is given, adds a warning.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="data">The values of the current record (after variable resolution), or null for the samples only.</param>
    /// <param name="declared">The label's declared variables.</param>
    /// <param name="issues">Receives the missing-field warning, or null to stay silent.</param>
    public static bool IsPrinted(LabelElement element, IReadOnlyDictionary<string, string>? data, IEnumerable<LabelVariable> declared, ICollection<ValidationIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.NotPrintable) return false;
        if (element.PrintCondition is not { } condition) return true;
        string field = PrintCondition.NormalizeField(condition.Field);
        // A rule that has no field yet is unfinished, not "false": hiding the object because the user has not picked a field would be a surprise.
        if (field.Length == 0) return true;
        if (!VariableResolver.TryLookup(field, data, declared, out string value))
        {
            issues?.Add(new ValidationIssue(ValidationSeverity.Warning, ConditionFieldMissing, element.Id, field));
            value = string.Empty;
        }
        return condition.Matches(value);
    }

    /// <summary>True when the label has an element that is switched off by a flag or a condition (so a design view needs its own render).</summary>
    public static bool AnyConditional(LabelDocument doc) => doc.Elements.Any(e => e.NotPrintable || e.PrintCondition is not null);
}
