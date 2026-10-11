using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Core.Variables.Prompts;

/// <summary>Remembers the last answer of prompts that ask for it (<see cref="PromptVariableOptions.RememberLast"/>).</summary>
public interface IPromptMemory
{
    /// <summary>The last answer for the prompt <paramref name="name"/> of label <paramref name="labelId"/>, or null.</summary>
    string? Get(string labelId, string name);

    /// <summary>Stores answers (variable name to answer) for the label.</summary>
    void Remember(string labelId, IReadOnlyDictionary<string, string> answers);
}

/// <summary>A prompt memory that lives in memory only.</summary>
public sealed class MemoryPromptMemory : IPromptMemory
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? Get(string labelId, string name) => _values.TryGetValue(labelId + "/" + name.ToLowerInvariant(), out var v) ? v : null;

    /// <inheritdoc />
    public void Remember(string labelId, IReadOnlyDictionary<string, string> answers)
    {
        foreach (var (name, value) in answers) _values[labelId + "/" + name.ToLowerInvariant()] = value;
    }
}

/// <summary>One question to ask the operator.</summary>
/// <param name="Name">Variable name; the key of the answer.</param>
/// <param name="Label">The question text (the name when the design gave none).</param>
/// <param name="InputType">Kind of answer.</param>
/// <param name="InitialValue">What to show before the operator types: the remembered answer, else the default.</param>
/// <param name="Required">An empty answer is refused.</param>
/// <param name="HelpText">Longer explanation (may be empty).</param>
/// <param name="Choices">The allowed answers of a choice prompt (empty otherwise).</param>
/// <param name="Uppercase">The answer is converted to upper case.</param>
/// <param name="MaxLength">The most characters accepted.</param>
public sealed record PromptItem(string Name, string Label, PromptInputType InputType, string InitialValue, bool Required, string HelpText,
    IReadOnlyList<string> Choices, bool Uppercase, int MaxLength);

/// <summary>The checked answers of a <see cref="PromptSet"/>.</summary>
/// <param name="Values">Normalized answers by variable name, ready to use as <see cref="VariableContext.PromptAnswers"/>. Present for every item, also when invalid.</param>
/// <param name="Issues">Why answers were refused (message keys with the variable name as first argument); empty when everything is valid.</param>
public sealed record PromptValidation(IReadOnlyDictionary<string, string> Values, IReadOnlyList<ValidationIssue> Issues)
{
    /// <summary>True when every answer is acceptable.</summary>
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// The questions a label asks the operator at print time, and the checking of the answers. Typical use: build the set, show a form with one
/// field per <see cref="Items"/> entry (initial value, help, choices), call <see cref="Validate"/> with what was typed, show the issues next to the
/// fields, and once valid pass <see cref="PromptValidation.Values"/> as <see cref="VariableContext.PromptAnswers"/>.
/// </summary>
/// <remarks>
/// What <see cref="Validate"/> does to an answer, in this order: tabs and line breaks become a space and other control characters are dropped;
/// surrounding spaces are trimmed; upper case if asked; then required, minimum and maximum length, the pattern, and finally the type (a number
/// is digits with an optional minus sign and decimal point; a date is <c>yyyy-MM-dd</c> or <c>yyyyMMdd</c> and is rewritten with
/// <see cref="PromptVariableOptions.DateFormat"/>; a choice must equal an entry, ignoring case, and is replaced by the entry's own spelling).
/// An empty optional answer skips the other checks.
/// </remarks>
public sealed class PromptSet
{
    private readonly IReadOnlyList<(PromptItem Item, PromptVariableOptions Options)> _entries;
    private readonly string? _labelId;

    private PromptSet(IReadOnlyList<(PromptItem, PromptVariableOptions)> entries, string? labelId)
    {
        _entries = entries;
        _labelId = labelId;
    }

    /// <summary>The questions, in the order the variables are declared.</summary>
    public IReadOnlyList<PromptItem> Items => _entries.Select(e => e.Item).ToList();

    /// <summary>True when there is nothing to ask.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>
    /// Builds the set for <paramref name="doc"/>. A prompt is left out when the data <paramref name="record"/> already supplies its name
    /// (unless the prompt is set to override the record).
    /// </summary>
    public static PromptSet Build(LabelDocument doc, IReadOnlyDictionary<string, string>? record = null, IPromptMemory? memory = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var list = new List<(PromptItem, PromptVariableOptions)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in doc.Variables)
        {
            if (v.Kind != VariableKind.Prompt || !seen.Add(v.Name)) continue;
            var p = v.Prompt ?? new PromptVariableOptions();
            bool inRecord = record is not null && record.TryGetValue(v.Name, out var rv) && !string.IsNullOrEmpty(rv);
            if (inRecord && !p.OverrideRecord) continue;
            string initial = p.Default.Length > 0 ? p.Default : v.SampleValue ?? string.Empty;
            if (p.RememberLast && memory is not null && doc.Id is { } id && memory.Get(id, v.Name) is { } last) initial = last;
            int max = p.MaxLength > 0 ? Math.Min(p.MaxLength, VariableLimits.MaxValueLength) : VariableLimits.MaxValueLength;
            list.Add((new PromptItem(v.Name, p.Label.Length > 0 ? p.Label : v.Name, p.InputType, initial, p.Required, p.HelpText,
                p.InputType == PromptInputType.Choice ? p.Choices.ToList() : Array.Empty<string>(), p.Uppercase, max), p));
        }
        return new PromptSet(list, doc.Id);
    }

    /// <summary>Checks the answers (variable name to typed text; a missing name counts as empty).</summary>
    public PromptValidation Validate(IReadOnlyDictionary<string, string>? answers)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<ValidationIssue>();
        foreach (var (item, p) in _entries)
        {
            string raw = answers is not null && answers.TryGetValue(item.Name, out var a) ? a ?? string.Empty : string.Empty;
            values[item.Name] = Check(item, p, raw, issues);
        }
        return new PromptValidation(values, issues);
    }

    /// <summary>Remembers the answers of prompts that asked for it. Does nothing when the label has no id or the memory is null.</summary>
    public void Remember(IReadOnlyDictionary<string, string> validAnswers, IPromptMemory? memory)
    {
        if (memory is null || _labelId is null) return;
        var keep = new Dictionary<string, string>();
        foreach (var (item, p) in _entries)
            if (p.RememberLast && validAnswers.TryGetValue(item.Name, out var v) && v.Length > 0) keep[item.Name] = v;
        if (keep.Count > 0) memory.Remember(_labelId, keep);
    }

    private static string Check(PromptItem item, PromptVariableOptions p, string raw, List<ValidationIssue> issues)
    {
        string name = item.Name;
        void Fail(string key, params string[] extra) =>
            issues.Add(new ValidationIssue(ValidationSeverity.Error, key, null, new[] { name }.Concat(extra).ToArray()));

        string text = Clean(raw);
        if (item.Uppercase) text = text.ToUpperInvariant();
        if (text.Length == 0)
        {
            if (item.Required) Fail(VariableMessageKeys.PromptRequired);
            return string.Empty;
        }
        int min = Math.Max(0, p.MinLength);
        if (text.Length < min) { Fail(VariableMessageKeys.PromptTooShort, min.ToString(CultureInfo.InvariantCulture)); return text; }
        if (text.Length > item.MaxLength) { Fail(VariableMessageKeys.PromptTooLong, item.MaxLength.ToString(CultureInfo.InvariantCulture)); return text[..item.MaxLength]; }

        if (p.Pattern.Length > 0)
        {
            var regex = PromptRegex.Get(p.Pattern);
            if (regex is null) { Fail(VariableMessageKeys.PromptPatternInvalid); return text; }
            bool match;
            try { match = regex.IsMatch(text); }
            catch (RegexMatchTimeoutException) { match = false; }
            if (!match) { Fail(VariableMessageKeys.PromptPattern); return text; }
        }

        switch (p.InputType)
        {
            case PromptInputType.Number:
                if (text.Length > 40 || !NumberShape.IsMatch(text)) Fail(VariableMessageKeys.PromptNotNumber);
                break;
            case PromptInputType.Date:
                if (DateTime.TryParseExact(text, new[] { "yyyy-MM-dd", "yyyyMMdd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return DateFormats.TryFormat(date, p.DateFormat, CultureInfo.InvariantCulture) ?? text;
                Fail(VariableMessageKeys.PromptNotDate);
                break;
            case PromptInputType.Choice:
                if (p.Choices.Count == 0) { Fail(VariableMessageKeys.PromptNoChoices); break; }
                var hit = p.Choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase));
                if (hit is null) Fail(VariableMessageKeys.PromptNotChoice); else return hit;
                break;
        }
        return text;
    }

    private static readonly Regex NumberShape = new(@"\A-?[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Line breaks and tabs become one space, other control characters are dropped, and the ends are trimmed.</summary>
    internal static string Clean(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        raw = raw.Replace("\r\n", "\n", StringComparison.Ordinal); // one line break is one space, not two
        foreach (char c in raw)
        {
            if (c is '\r' or '\n' or '\t') sb.Append(' ');
            else if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}

/// <summary>
/// Compiles the regular expressions of prompts. They come from files and assistants, so they run on the non-backtracking engine (time grows
/// linearly with the answer, no catastrophic patterns), with a time limit and a length cap. Constructs that engine cannot do (look-around,
/// back-references, atomic groups) make the pattern invalid.
/// </summary>
public static class PromptRegex
{
    private static readonly ConcurrentDictionary<string, Regex?> Cache = new(StringComparer.Ordinal);

    /// <summary>The compiled whole-answer matcher for <paramref name="pattern"/>, or null when it is too long or not supported.</summary>
    public static Regex? Get(string pattern)
    {
        if (pattern.Length == 0 || pattern.Length > VariableLimits.MaxPatternLength) return null;
        if (Cache.Count > 64) Cache.Clear();
        return Cache.GetOrAdd(pattern, p =>
        {
            try
            {
                // \A(?:...)\z makes the pattern describe the entire answer, so "[0-9]+" does not accept "12abc".
                return new Regex(@"\A(?:" + p + @")\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
        });
    }

    /// <summary>True when the pattern can be used.</summary>
    public static bool IsValid(string pattern) => Get(pattern) is not null;
}
