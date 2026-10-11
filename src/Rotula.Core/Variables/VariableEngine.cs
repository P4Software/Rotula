using System.Globalization;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Variables.Expressions;
using Rotula.Core.Variables.Prompts;
using Rotula.Core.Zpl;

namespace Rotula.Core.Variables;

/// <summary>
/// Works out the value of every variable of a label for one label of a run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolution order</b>, lowest to highest: the data record (merge columns); then plain variables fall back to their sample value; then
/// prompts (the operator's answer, or default/sample value when none was asked); then counters; then dates; and last computed variables, which can
/// read everything before them and each other. A variable has one definition, so the order only decides which value a later step can see and which
/// wins over a same-named data column:
/// </para>
/// <list type="bullet">
/// <item>Prompt: the data column wins when it has a value, unless the prompt is set to override the record.</item>
/// <item>Counter, date and computed variables always replace a same-named data column (a warning says so).</item>
/// <item>A plain variable never replaces data; its sample value is used only where the data has nothing.</item>
/// </list>
/// <para>Time comes only from the <see cref="VariableContext"/>, so the result is deterministic for a given context.</para>
/// </remarks>
public static class VariableEngine
{
    /// <summary>True when any variable of the label has a definition (is not plain text).</summary>
    public static bool HasDefinitions(LabelDocument doc) => doc.Variables.Any(v => v.Kind != VariableKind.Text);

    /// <summary>Resolves all variables of <paramref name="doc"/> for one label of a run.</summary>
    public static VariableResolution Resolve(LabelDocument doc, VariableContext context)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<ValidationIssue>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.Record is { } rec) foreach (var kv in rec) values[kv.Key] = kv.Value;

        // The first declaration of a name wins, which is also what the placeholder resolver does with duplicates.
        var vars = new List<LabelVariable>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in doc.Variables) if (v.Name is { Length: > 0 } && names.Add(v.Name)) vars.Add(v);

        DateTimeOffset nowUtc = context.UtcNow;
        TimeZoneInfo zone = context.Time.LocalTimeZone;

        foreach (var v in vars.Where(v => v.Kind == VariableKind.Text))
            if (!values.ContainsKey(v.Name)) values[v.Name] = v.SampleValue ?? string.Empty;

        // The record value is looked at before any step replaces it, for the "overridden" warning.
        var recordHad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (context.Record is { } r0) foreach (var kv in r0) if (kv.Value.Length > 0) recordHad.Add(kv.Key);
        void Override(LabelVariable v)
        {
            if (recordHad.Contains(v.Name))
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.RecordOverridden, null, v.Name, v.Kind.ToString()));
        }

        // ---- prompts
        foreach (var v in vars.Where(v => v.Kind == VariableKind.Prompt))
        {
            var p = v.Prompt ?? new PromptVariableOptions();
            if (recordHad.Contains(v.Name) && !p.OverrideRecord) continue;
            string value;
            if (context.PromptAnswers is { } answers && answers.TryGetValue(v.Name, out var answer)) value = answer ?? string.Empty;
            else
            {
                if (context.RequirePromptAnswers)
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.PromptMissingAnswer, null, v.Name));
                value = p.Default.Length > 0 ? p.Default : v.SampleValue ?? string.Empty;
            }
            if (p.Uppercase) value = value.ToUpperInvariant();
            values[v.Name] = Clip(value);
        }

        // ---- counters
        foreach (var v in vars.Where(v => v.Kind == VariableKind.Counter))
        {
            Override(v);
            values[v.Name] = ResolveCounter(doc, v, context, issues);
        }

        // ---- dates
        var dateOk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vars.Where(v => v.Kind == VariableKind.Date))
        {
            Override(v);
            var d = v.Date ?? new DateVariableOptions();
            string? text = DateEvaluator.Evaluate(v.Name, d, nowUtc, zone, issues);
            values[v.Name] = text ?? v.SampleValue ?? string.Empty;
            if (text is not null) dateOk.Add(v.Name);
        }

        // ---- computed
        ResolveComputed(vars, values, issues, recordHad);

        // ---- printer clock bindings
        var bindings = new Dictionary<string, PrinterBinding>(StringComparer.OrdinalIgnoreCase);
        var offsets = new Dictionary<int, PrinterOffset>();
        PlanPrinterClock(doc, vars, dateOk, bindings, offsets, issues);

        return new VariableResolution(values, issues, bindings, offsets);
    }

    /// <summary>
    /// The variable values for a preview: counters show the next position of the store (without reserving it), prompts show their defaults and dates use
    /// the context's clock. The dictionary can be passed straight to <see cref="ZplGenerator.Generate(LabelDocument, IReadOnlyDictionary{string, string}?)"/>
    /// and to the preview renderer.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildPreviewData(LabelDocument doc, VariableContext context) => Resolve(doc, context).Values;

    // ------------------------------------------------------------------------------------ counters

    private static string ResolveCounter(LabelDocument doc, LabelVariable v, VariableContext context, List<ValidationIssue> issues)
    {
        var c = v.Counter ?? new CounterVariableOptions();
        var serial = c.ToSerialCounter(v.Name);
        string? invalid = serial.Validate();
        if (invalid is null && (c.Prefix.Length > VariableLimits.MaxAffixLength || c.Suffix.Length > VariableLimits.MaxAffixLength))
            invalid = "Merge.Counter.Name";
        if (invalid is not null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.CounterInvalid, null, v.Name, invalid));
            return string.Empty;
        }

        long baseIndex = 0;
        if (context.CounterBase is { } bases && bases.TryGetValue(v.Name, out long fixedBase)) baseIndex = fixedBase;
        else if (c.Remember && context.CounterStore is null)
        {
            // Not an error here: a design-time preview has no memory to read and shows the first value. VariableJob refuses to print in this state.
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.CounterNoStore, null, v.Name));
        }
        else if (c.Remember && context.CounterStore is { } store)
        {
            if (doc.Id is null)
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.CounterNeedsLabelId, null, v.Name));
            else if (SafeGet(store, CounterKey.For(doc.Id, v.Name), issues, v.Name) is { } rec)
            {
                baseIndex = rec.Next;
                if (!string.Equals(rec.Fingerprint, c.Fingerprint(), StringComparison.Ordinal))
                    issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.CounterSettingsChanged, null, v.Name, rec.LastValue ?? string.Empty));
            }
        }

        long index = baseIndex + context.ItemIndex;
        if (serial.TryGetValue(index, out string? value)) return Clip(value);
        issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.CounterOverflow, null, v.Name));
        return string.Empty;
    }

    private static CounterRecord? SafeGet(ICounterStore store, string key, List<ValidationIssue> issues, string variable)
    {
        try { return store.Get(key); }
        catch (CounterStoreException ex)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, ex.MessageKey, null, ex.Args));
            return null;
        }
    }

    // ------------------------------------------------------------------------------------ computed

    private static void ResolveComputed(List<LabelVariable> vars, Dictionary<string, string> values, List<ValidationIssue> issues, HashSet<string> recordHad)
    {
        var computed = vars.Where(v => v.Kind == VariableKind.Computed).ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        if (computed.Count == 0) return;
        var parsed = new Dictionary<string, ParsedExpression?>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 = being evaluated, 2 = done
        var chain = new List<string>();

        foreach (var v in computed.Values)
            if (recordHad.Contains(v.Name))
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.RecordOverridden, null, v.Name, v.Kind.ToString()));

        string? Lookup(string name)
        {
            if (computed.TryGetValue(name, out var target))
            {
                if (state.TryGetValue(name, out int s))
                {
                    if (s == 1)
                    {
                        // Report the cycle once, at the variable that closes it, and carry on with an empty value so one mistake gives one message.
                        issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.ExpressionCycle, null, name, "0",
                            string.Join(" > ", chain.SkipWhile(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).Append(name))));
                        return string.Empty;
                    }
                    return values.GetValueOrDefault(name) ?? string.Empty;
                }
                Evaluate(target);
                return values.GetValueOrDefault(name) ?? string.Empty;
            }
            return values.TryGetValue(name, out var found) ? found : null;
        }

        void Evaluate(LabelVariable v)
        {
            state[v.Name] = 1;
            chain.Add(v.Name);
            string result = v.SampleValue ?? string.Empty;
            var expr = (v.Computed ?? new ComputedVariableOptions()).Expression;
            if (!parsed.TryGetValue(v.Name, out var tree))
            {
                tree = ExpressionParser.Parse(expr, out var parseError);
                parsed[v.Name] = tree;
                if (parseError is not null) issues.Add(parseError.ToIssue(v.Name));
            }
            if (tree is not null)
            {
                var warnings = new List<ExpressionError>();
                string text = ExpressionEvaluator.Evaluate(tree, Lookup, out var evalError, warnings);
                foreach (var w in warnings) issues.Add(w.ToIssue(v.Name, ValidationSeverity.Warning));
                if (evalError is not null) issues.Add(evalError.ToIssue(v.Name));
                else result = text;
            }
            values[v.Name] = Clip(result);
            chain.RemoveAt(chain.Count - 1);
            state[v.Name] = 2;
        }

        foreach (var v in computed.Values)
            if (!state.ContainsKey(v.Name)) Evaluate(v);
    }

    // ------------------------------------------------------------------------------------ printer clock

    private static void PlanPrinterClock(LabelDocument doc, List<LabelVariable> vars, HashSet<string> dateOk,
        Dictionary<string, PrinterBinding> bindings, Dictionary<int, PrinterOffset> offsets, List<ValidationIssue> issues)
    {
        var printerDates = vars.Where(v => v.Kind == VariableKind.Date && v.Date is { Source: DateSource.PrinterClock } && dateOk.Contains(v.Name))
            .ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        if (printerDates.Count == 0) return;

        foreach (var name in BarcodePlaceholders(doc).Where(printerDates.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase))
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.PrinterBarcode, null, printerDates[name].Name));

        foreach (var name in TextPlaceholders(doc).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!printerDates.TryGetValue(name, out var v)) continue;
            if (!PrinterClock.TryPlan(v.Name, v.Date!, out var plan, out var reason))
            {
                issues.Add(reason!);
                continue;
            }
            int set;
            if (plan!.Offset.IsZero) set = 1;
            else
            {
                set = offsets.FirstOrDefault(kv => kv.Value == plan.Offset).Key; // 0 when no clock set has this offset yet
                if (set == 0)
                {
                    set = !offsets.ContainsKey(2) ? 2 : !offsets.ContainsKey(3) ? 3 : 0;
                    if (set == 0)
                    {
                        issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.PrinterSlotsFull, null, v.Name));
                        continue;
                    }
                    offsets[set] = plan.Offset;
                }
            }
            bindings[v.Name] = new PrinterBinding(v.Name, set, plan.TokenFormat);
        }
    }

    /// <summary>Placeholder names used by text elements, in order of appearance (duplicates kept).</summary>
    internal static IEnumerable<string> TextPlaceholders(LabelDocument doc) =>
        doc.Elements.OfType<TextElement>().SelectMany(t => VariableResolver.FindVariables(t.GetTemplate()));

    /// <summary>Placeholder names used by barcode elements.</summary>
    internal static IEnumerable<string> BarcodePlaceholders(LabelDocument doc) =>
        doc.Elements.OfType<BarcodeElement>().SelectMany(b => VariableResolver.FindVariables(b.Data));

    private static string Clip(string s) => s.Length > VariableLimits.MaxValueLength ? s[..VariableLimits.MaxValueLength] : s;

    // ------------------------------------------------------------------------------------ design-time validation

    /// <summary>
    /// Checks the variable definitions of a label without running them: names, date formats and offsets, counter settings, prompt patterns and
    /// choices, expressions (syntax, unknown functions, references to undeclared variables, cycles). Meant for the designer and for validation tools.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateDefinitions(LabelDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declared = new HashSet<string>(doc.Variables.Select(v => v.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in doc.Variables)
        {
            string name = v.Name ?? string.Empty;
            if (!IsValidName(name))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.NameInvalid, null, name));
                continue;
            }
            if (!seen.Add(name)) issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.NameDuplicate, null, name));
            switch (v.Kind)
            {
                case VariableKind.Date:
                    ValidateDate(name, v.Date ?? new DateVariableOptions(), issues);
                    break;
                case VariableKind.Counter:
                {
                    var c = v.Counter ?? new CounterVariableOptions();
                    string? bad = c.ToSerialCounter(name).Validate();
                    if (bad is null && (c.Prefix.Length > VariableLimits.MaxAffixLength || c.Suffix.Length > VariableLimits.MaxAffixLength)) bad = "Merge.Counter.Name";
                    if (bad is not null) issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.CounterInvalid, null, name, bad));
                    break;
                }
                case VariableKind.Prompt:
                {
                    var p = v.Prompt ?? new PromptVariableOptions();
                    if (p.Pattern.Length > 0 && !PromptRegex.IsValid(p.Pattern))
                        issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.PromptPatternInvalid, null, name));
                    if (p.InputType == PromptInputType.Choice && p.Choices.Count == 0)
                        issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.PromptNoChoices, null, name));
                    if (p.InputType == PromptInputType.Date && !DateFormats.IsValid(p.DateFormat))
                        issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.DateFormatInvalid, null, name, p.DateFormat));
                    break;
                }
                case VariableKind.Computed:
                {
                    var tree = ExpressionParser.Parse((v.Computed ?? new ComputedVariableOptions()).Expression, out var error);
                    if (error is not null) { issues.Add(error.ToIssue(name)); break; }
                    edges[name] = tree!.References;
                    foreach (var reference in tree.References)
                        if (!declared.Contains(reference) && !reference.StartsWith('#'))
                            // Not an error: the name may be a data column that only exists at merge time.
                            issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.ExpressionUnknownVariable, null, name, "0", reference));
                    break;
                }
            }
        }

        // A cycle can only run through computed variables, so a depth-first walk over those edges finds it.
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        void Walk(string node)
        {
            if (done.Contains(node)) return;
            int at = path.FindIndex(n => string.Equals(n, node, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.ExpressionCycle, null, node, "0", string.Join(" > ", path.Skip(at).Append(node))));
                return;
            }
            if (!edges.TryGetValue(node, out var next)) return;
            path.Add(node);
            foreach (var n in next) Walk(n);
            path.RemoveAt(path.Count - 1);
            done.Add(node);
        }
        foreach (var node in edges.Keys.ToList()) Walk(node);
        return issues;
    }

    private static void ValidateDate(string name, DateVariableOptions d, List<ValidationIssue> issues)
    {
        if (!DateFormats.IsValid(d.Format)) issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.DateFormatInvalid, null, name, d.Format ?? string.Empty));
        if (DateFormats.ResolveCulture(d.Culture) is null) issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.DateCultureUnknown, null, name, d.Culture));
        if (d.Offsets.Count > VariableLimits.MaxOffsets || d.Offsets.Any(o => Math.Abs((long)o.Amount) > VariableLimits.MaxOffsetAmount))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.DateOffsetRange, null, name, VariableLimits.MaxOffsetAmount.ToString(CultureInfo.InvariantCulture)));
        if (d.Source == DateSource.Fixed && d.FixedValue is null)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.DateFixedMissing, null, name));
        if (d.Source == DateSource.PrinterClock && !PrinterClock.TryPlan(name, d, out _, out var reason)) issues.Add(reason!);
    }

    /// <summary>True for a name usable in a <c>{{name}}</c> placeholder: letters, digits and underscore, not starting with a digit, at most 64 characters.</summary>
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= VariableLimits.MaxNameLength } && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
