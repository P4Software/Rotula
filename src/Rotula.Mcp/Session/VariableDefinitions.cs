using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Variables;

namespace Rotula.Mcp.Session;

/// <summary>
/// Turns the loosely typed <c>definition</c> object of set_variable into a validated <see cref="LabelVariable"/> definition, and describes a
/// variable in one plain sentence for tool replies. Everything is checked against <see cref="VariableLimits"/>; unknown keys are refused with
/// the valid ones, so a misspelled option never silently does nothing.
/// </summary>
public static class VariableDefinitions
{
    private static readonly string[] DateKeys = ["source", "fixedDate", "offsets", "format", "culture", "utc", "rounding", "holidays"];
    private static readonly string[] CounterKeys = ["kind", "start", "step", "padding", "prefix", "suffix", "overflow", "remember"];
    private static readonly string[] PromptKeys = ["label", "type", "default", "required", "pattern", "minLength", "maxLength", "choices", "uppercase", "rememberLast", "helpText", "overrideRecord", "dateFormat"];
    private static readonly string[] ComputedKeys = ["expression"];

    /// <summary>The keys each kind accepts, for tool descriptions.</summary>
    public static IReadOnlyList<string> KeysOf(VariableKind kind) => kind switch
    {
        VariableKind.Date => DateKeys,
        VariableKind.Counter => CounterKeys,
        VariableKind.Prompt => PromptKeys,
        VariableKind.Computed => ComputedKeys,
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Sets the kind and definition of <paramref name="variable"/>. When <paramref name="definition"/> is null the existing definition of the same kind is
    /// kept (or a default one is created); when the kind changes, the old definition is dropped. Throws <see cref="McpException"/> in plain language.
    /// </summary>
    public static void Apply(LabelVariable variable, VariableKind kind, IReadOnlyDictionary<string, JsonElement>? definition)
    {
        if (kind != variable.Kind)
        {
            variable.Date = null;
            variable.Counter = null;
            variable.Prompt = null;
            variable.Computed = null;
            variable.Kind = kind;
        }
        var values = definition ?? new Dictionary<string, JsonElement>();
        if (kind == VariableKind.Text)
        {
            if (values.Count > 0) throw new McpException("A text variable has no definition. Use kind date, counter, prompt or computed for those options.");
            return;
        }
        var valid = KeysOf(kind);
        foreach (var key in values.Keys)
            if (!valid.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new McpException($"A {kind.ToString().ToLowerInvariant()} variable has no setting \"{key}\". Valid settings: {string.Join(", ", valid)}.");
        var get = new Dictionary<string, JsonElement>(values, StringComparer.OrdinalIgnoreCase);

        switch (kind)
        {
            case VariableKind.Date: variable.Date = ApplyDate(variable.Date?.Clone() ?? new DateVariableOptions(), get); break;
            case VariableKind.Counter: variable.Counter = ApplyCounter(variable.Counter?.Clone() ?? new CounterVariableOptions(), get); break;
            case VariableKind.Prompt: variable.Prompt = ApplyPrompt(variable.Prompt?.Clone() ?? new PromptVariableOptions(), get); break;
            case VariableKind.Computed:
                var c = variable.Computed?.Clone() ?? new ComputedVariableOptions();
                if (get.TryGetValue("expression", out var e)) c.Expression = Text(e, "expression", VariableLimits.MaxExpressionLength);
                variable.Computed = c;
                break;
        }
    }

    private static DateVariableOptions ApplyDate(DateVariableOptions o, Dictionary<string, JsonElement> v)
    {
        if (v.TryGetValue("source", out var src))
            o.Source = Squash(ElementModel.ReadString(src, "source")) switch
            {
                "computer" or "computerclock" or "pc" => DateSource.ComputerClock,
                "printer" or "printerclock" => DateSource.PrinterClock,
                "fixed" => DateSource.Fixed,
                _ => throw new McpException("source must be computer, printer or fixed."),
            };
        if (v.TryGetValue("fixedDate", out var fx))
        {
            string text = Text(fx, "fixedDate", 32);
            if (!DateTime.TryParseExact(text, ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                throw new McpException("fixedDate must look like 2026-03-07 or 2026-03-07T14:30:00.");
            o.FixedValue = d;
        }
        if (v.TryGetValue("offsets", out var offs))
        {
            if (offs.ValueKind != JsonValueKind.Array) throw new McpException("offsets must be a list such as [\"+30 days\", \"-1 month\"].");
            if (offs.GetArrayLength() > VariableLimits.MaxOffsets) throw new McpException($"At most {VariableLimits.MaxOffsets} offsets are allowed.");
            o.Offsets.Clear();
            foreach (var item in offs.EnumerateArray()) o.Offsets.Add(Offset(item));
        }
        if (v.TryGetValue("format", out var f)) o.Format = Text(f, "format", VariableLimits.MaxFormatLength);
        if (v.TryGetValue("culture", out var cu)) o.Culture = Text(cu, "culture", 40);
        if (v.TryGetValue("utc", out var utc)) o.Utc = ElementModel.ReadBool(utc, "utc");
        if (v.TryGetValue("rounding", out var r)) o.Rounding = ElementModel.ParseEnum<DateRounding>(ElementModel.ReadString(r, "rounding"), "rounding");
        if (v.TryGetValue("holidays", out var hol))
        {
            if (hol.ValueKind != JsonValueKind.Array) throw new McpException("holidays must be a list of dates such as [\"2026-12-25\"].");
            if (hol.GetArrayLength() > VariableLimits.MaxHolidays) throw new McpException($"At most {VariableLimits.MaxHolidays} holidays are allowed.");
            o.Holidays.Clear();
            foreach (var item in hol.EnumerateArray())
            {
                if (!DateOnly.TryParseExact(ElementModel.ReadString(item, "holidays"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    throw new McpException("Each holiday must look like 2026-12-25.");
                o.Holidays.Add(day);
            }
        }
        return o;
    }

    private static readonly Regex OffsetText = new(@"^\s*([+-]?\d{1,7})\s*([A-Za-z ]{1,20})\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static DateOffset Offset(JsonElement item)
    {
        int amount;
        string unitText;
        if (item.ValueKind == JsonValueKind.String)
        {
            var m = OffsetText.Match(item.GetString() ?? string.Empty);
            if (!m.Success) throw new McpException("An offset must look like \"+30 days\", \"-1 month\" or \"2 business days\".");
            amount = int.Parse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            unitText = m.Groups[2].Value;
        }
        else if (item.ValueKind == JsonValueKind.Object)
        {
            amount = item.TryGetProperty("amount", out var a) ? ElementModel.ReadInt(a, "amount") : throw new McpException("An offset object needs amount and unit.");
            unitText = item.TryGetProperty("unit", out var u) ? ElementModel.ReadString(u, "unit") : throw new McpException("An offset object needs amount and unit.");
        }
        else throw new McpException("An offset must be text such as \"+30 days\" or an object {\"amount\":30,\"unit\":\"days\"}.");
        if (Math.Abs((long)amount) > VariableLimits.MaxOffsetAmount) throw new McpException($"An offset amount must be between -{VariableLimits.MaxOffsetAmount} and {VariableLimits.MaxOffsetAmount}.");
        string squashed = Squash(unitText);
        if (!squashed.EndsWith('s')) squashed += "s"; // "day" and "days" mean the same
        return new DateOffset(amount, ElementModel.ParseEnum<DateUnit>(squashed, "unit"));
    }

    private static CounterVariableOptions ApplyCounter(CounterVariableOptions o, Dictionary<string, JsonElement> v)
    {
        if (v.TryGetValue("kind", out var k)) o.Kind = ElementModel.ParseEnum<CounterKind>(ElementModel.ReadString(k, "kind"), "counter kind");
        if (v.TryGetValue("start", out var s)) o.Start = Text(s, "start", 40);
        if (v.TryGetValue("step", out var st)) o.Step = ElementModel.ReadInt(st, "step");
        if (v.TryGetValue("padding", out var p)) o.Padding = ElementModel.ReadInt(p, "padding");
        if (v.TryGetValue("prefix", out var pre)) o.Prefix = Text(pre, "prefix", VariableLimits.MaxAffixLength);
        if (v.TryGetValue("suffix", out var suf)) o.Suffix = Text(suf, "suffix", VariableLimits.MaxAffixLength);
        if (v.TryGetValue("overflow", out var ov)) o.Overflow = ElementModel.ParseEnum<CounterOverflow>(ElementModel.ReadString(ov, "overflow"), "overflow behaviour");
        if (v.TryGetValue("remember", out var rm)) o.Remember = ElementModel.ReadBool(rm, "remember");
        return o;
    }

    private static PromptVariableOptions ApplyPrompt(PromptVariableOptions o, Dictionary<string, JsonElement> v)
    {
        if (v.TryGetValue("label", out var l)) o.Label = Text(l, "label", VariableLimits.MaxPromptTextLength);
        if (v.TryGetValue("type", out var t)) o.InputType = ElementModel.ParseEnum<PromptInputType>(ElementModel.ReadString(t, "type"), "prompt type");
        if (v.TryGetValue("default", out var d)) o.Default = Text(d, "default", VariableLimits.MaxValueLength);
        if (v.TryGetValue("required", out var rq)) o.Required = ElementModel.ReadBool(rq, "required");
        if (v.TryGetValue("pattern", out var pa)) o.Pattern = Text(pa, "pattern", VariableLimits.MaxPatternLength);
        if (v.TryGetValue("minLength", out var mn)) o.MinLength = Range(ElementModel.ReadInt(mn, "minLength"), "minLength", 0, VariableLimits.MaxValueLength);
        if (v.TryGetValue("maxLength", out var mx)) o.MaxLength = Range(ElementModel.ReadInt(mx, "maxLength"), "maxLength", 0, VariableLimits.MaxValueLength);
        if (v.TryGetValue("choices", out var ch))
        {
            if (ch.ValueKind != JsonValueKind.Array) throw new McpException("choices must be a list of texts.");
            if (ch.GetArrayLength() > VariableLimits.MaxChoices) throw new McpException($"At most {VariableLimits.MaxChoices} choices are allowed.");
            o.Choices.Clear();
            foreach (var item in ch.EnumerateArray()) o.Choices.Add(Text(item, "choices", VariableLimits.MaxPromptTextLength));
        }
        if (v.TryGetValue("uppercase", out var up)) o.Uppercase = ElementModel.ReadBool(up, "uppercase");
        if (v.TryGetValue("rememberLast", out var rl)) o.RememberLast = ElementModel.ReadBool(rl, "rememberLast");
        if (v.TryGetValue("helpText", out var h)) o.HelpText = Text(h, "helpText", VariableLimits.MaxPromptTextLength);
        if (v.TryGetValue("overrideRecord", out var orr)) o.OverrideRecord = ElementModel.ReadBool(orr, "overrideRecord");
        if (v.TryGetValue("dateFormat", out var df)) o.DateFormat = Text(df, "dateFormat", VariableLimits.MaxFormatLength);
        return o;
    }

    private static string Text(JsonElement j, string name, int max) => ElementModel.Clip(ElementModel.ReadString(j, name), max);

    private static int Range(int value, string name, int min, int max) =>
        value < min || value > max ? throw new McpException($"{name} must be between {min} and {max}.") : value;

    private static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A short plain-language description of what a variable produces, for tool replies.</summary>
    public static string Describe(LabelVariable v)
    {
        switch (v.Kind)
        {
            case VariableKind.Date when v.Date is { } d:
                string source = d.Source switch { DateSource.PrinterClock => "printer clock", DateSource.Fixed => "fixed date", _ => "computer clock" };
                string offsets = d.Offsets.Count == 0 ? "" : ", " + string.Join(" ", d.Offsets.Select(o => $"{(o.Amount >= 0 ? "+" : "")}{o.Amount} {o.Unit.ToString().ToLowerInvariant()}"));
                string rounding = d.Rounding == DateRounding.None ? "" : $", rounded to {d.Rounding}";
                return $"date from the {source}{offsets}{rounding}, format {d.Format}";
            case VariableKind.Counter when v.Counter is { } c:
                return $"counter from {c.Prefix}{c.Start}{c.Suffix} step {c.Step}" + (c.Remember ? ", remembers its last value between sessions" : ", restarts every run");
            case VariableKind.Prompt when v.Prompt is { } p:
                return $"asked at print time ({p.InputType.ToString().ToLowerInvariant()}{(p.Required ? ", required" : "")})";
            case VariableKind.Computed when v.Computed is { } x:
                return "computed: " + (x.Expression.Length > 80 ? x.Expression[..80] + "..." : x.Expression);
            default:
                return "text";
        }
    }
}
