using System.Globalization;
using Rotula.Core.Merge;

namespace Rotula.Core.Variables;

/// <summary>What kind of value a declared variable produces.</summary>
public enum VariableKind
{
    /// <summary>A plain variable: the data record supplies the value, otherwise the sample value is used (the original behaviour).</summary>
    Text,
    /// <summary>A date or time computed from a clock (computer clock, printer clock or a fixed date), with offsets and a format.</summary>
    Date,
    /// <summary>A serial number that counts up per label and can optionally remember its last value between sessions.</summary>
    Counter,
    /// <summary>A value the operator types in at print time.</summary>
    Prompt,
    /// <summary>A value built from other variables with a small, safe expression.</summary>
    Computed,
}

/// <summary>Where a date variable takes the current date and time from.</summary>
public enum DateSource
{
    /// <summary>The computer's clock when the label is generated; the ZPL carries the finished text.</summary>
    ComputerClock,
    /// <summary>
    /// The printer's own real-time clock: the ZPL carries clock codes (<c>^FC</c>) and the printer prints the date itself.
    /// Only some offsets and formats can be expressed this way; the rest falls back to the computer clock with a warning.
    /// </summary>
    PrinterClock,
    /// <summary>A fixed date and time typed into the design (offsets still apply).</summary>
    Fixed,
}

/// <summary>Unit of a <see cref="DateOffset"/>.</summary>
public enum DateUnit
{
    /// <summary>Seconds (exact elapsed time).</summary>
    Seconds,
    /// <summary>Minutes (exact elapsed time).</summary>
    Minutes,
    /// <summary>Hours (exact elapsed time, so a daylight-saving change is counted).</summary>
    Hours,
    /// <summary>Calendar days (the wall-clock time of day is kept across daylight-saving changes).</summary>
    Days,
    /// <summary>Business days: Monday to Friday, skipping the holidays of the variable. Cannot be done by the printer.</summary>
    BusinessDays,
    /// <summary>Calendar weeks (seven days each).</summary>
    Weeks,
    /// <summary>Calendar months; a day that does not exist in the target month moves to its last day (31 January + 1 month = 28 or 29 February).</summary>
    Months,
    /// <summary>Calendar years; 29 February moves to 28 February in a common year.</summary>
    Years,
}

/// <summary>Snaps a computed date to the start or end of a period, after the offsets.</summary>
public enum DateRounding
{
    /// <summary>No rounding.</summary>
    None,
    /// <summary>00:00:00 of the same day.</summary>
    StartOfDay,
    /// <summary>23:59:59 of the same day.</summary>
    EndOfDay,
    /// <summary>First day of the month, 00:00:00.</summary>
    StartOfMonth,
    /// <summary>Last day of the month, 23:59:59.</summary>
    EndOfMonth,
    /// <summary>First day of the year, 00:00:00.</summary>
    StartOfYear,
    /// <summary>Last day of the year, 23:59:59.</summary>
    EndOfYear,
}

/// <summary>One step of date arithmetic. Steps are applied in list order.</summary>
public sealed class DateOffset
{
    /// <summary>Creates an offset.</summary>
    public DateOffset(int amount = 0, DateUnit unit = DateUnit.Days)
    {
        Amount = amount;
        Unit = unit;
    }

    /// <summary>How many units to add; negative goes back in time.</summary>
    public int Amount { get; set; }

    /// <summary>The unit of <see cref="Amount"/>.</summary>
    public DateUnit Unit { get; set; }

    /// <summary>A copy of this offset.</summary>
    public DateOffset Clone() => new(Amount, Unit);
}

/// <summary>Settings of a <see cref="VariableKind.Date"/> variable.</summary>
public sealed class DateVariableOptions
{
    /// <summary>Where "now" comes from.</summary>
    public DateSource Source { get; set; } = DateSource.ComputerClock;

    /// <summary>The date used by <see cref="DateSource.Fixed"/> (wall-clock time, no time zone).</summary>
    public DateTime? FixedValue { get; set; }

    /// <summary>Date arithmetic applied in order, for example "+30 days".</summary>
    public List<DateOffset> Offsets { get; } = new();

    /// <summary>
    /// How the result is written: the id of a preset (see <see cref="DatePresets"/>, for example <c>iso</c>, <c>julian</c>, <c>gs1</c>) or a .NET custom
    /// date pattern such as <c>dd/MM/yyyy</c>. A single-letter pattern is read as a custom pattern, not as a .NET standard format.
    /// </summary>
    public string Format { get; set; } = "iso";

    /// <summary>Culture used for month and day names (for example <c>es-ES</c>); empty means the invariant culture (English names).</summary>
    public string Culture { get; set; } = string.Empty;

    /// <summary>Use UTC instead of the computer's local time zone (computer clock only).</summary>
    public bool Utc { get; set; }

    /// <summary>Snap the result to the start or end of a period after the offsets.</summary>
    public DateRounding Rounding { get; set; } = DateRounding.None;

    /// <summary>Days that do not count as business days (only used by <see cref="DateUnit.BusinessDays"/>); Saturday and Sunday never count.</summary>
    public List<DateOnly> Holidays { get; } = new();

    /// <summary>A deep copy.</summary>
    public DateVariableOptions Clone()
    {
        var copy = new DateVariableOptions
        {
            Source = Source, FixedValue = FixedValue, Format = Format, Culture = Culture, Utc = Utc, Rounding = Rounding,
        };
        copy.Offsets.AddRange(Offsets.Select(o => o.Clone()));
        copy.Holidays.AddRange(Holidays);
        return copy;
    }
}

/// <summary>Settings of a <see cref="VariableKind.Counter"/> variable: the serial-number rules plus the memory between sessions.</summary>
public sealed class CounterVariableOptions
{
    /// <summary>Numeric or alphanumeric counting.</summary>
    public CounterKind Kind { get; set; } = CounterKind.Numeric;

    /// <summary>First value (a decimal integer for numeric counters, letters and digits for alphanumeric ones).</summary>
    public string Start { get; set; } = "1";

    /// <summary>Increment per label.</summary>
    public int Step { get; set; } = 1;

    /// <summary>Minimum digits of a numeric counter (zero padded).</summary>
    public int Padding { get; set; }

    /// <summary>Text before the number.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Text after the number.</summary>
    public string Suffix { get; set; } = string.Empty;

    /// <summary>What happens when the counter runs past its capacity.</summary>
    public CounterOverflow Overflow { get; set; } = CounterOverflow.Wrap;

    /// <summary>
    /// Remember the last used value between sessions (stored per label and counter name in the user's counters file). Off by default:
    /// without it the counter starts again at <see cref="Start"/> every time.
    /// </summary>
    public bool Remember { get; set; }

    /// <summary>The serial-number generator these settings describe, named after the variable.</summary>
    public SerialCounter ToSerialCounter(string name) => new()
    {
        Name = name, Kind = Kind, Start = Start, Step = Step, Padding = Padding, Prefix = Prefix, Suffix = Suffix, Overflow = Overflow,
    };

    /// <summary>
    /// A short text that changes whenever the numbers the counter produces would change (kind, start, step, padding). The store keeps it
    /// next to the remembered position so that editing the counter later is noticed instead of silently re-mapping numbers.
    /// </summary>
    public string Fingerprint() => string.Join('|', Kind, Start, Step.ToString(CultureInfo.InvariantCulture), Padding.ToString(CultureInfo.InvariantCulture));

    /// <summary>A copy.</summary>
    public CounterVariableOptions Clone() => (CounterVariableOptions)MemberwiseClone();
}

/// <summary>The kind of answer a prompt asks for.</summary>
public enum PromptInputType
{
    /// <summary>Free text.</summary>
    Text,
    /// <summary>A number: optional minus sign, digits and an optional decimal point (no thousands separators).</summary>
    Number,
    /// <summary>A date typed as <c>yyyy-MM-dd</c> (also accepted: <c>yyyyMMdd</c>); written to the label with <see cref="PromptVariableOptions.DateFormat"/>.</summary>
    Date,
    /// <summary>One entry of <see cref="PromptVariableOptions.Choices"/>.</summary>
    Choice,
}

/// <summary>Settings of a <see cref="VariableKind.Prompt"/> variable: what to ask the operator at print time and how to check the answer.</summary>
public sealed class PromptVariableOptions
{
    /// <summary>The question shown to the operator; the variable name is used when this is empty.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Kind of answer.</summary>
    public PromptInputType InputType { get; set; } = PromptInputType.Text;

    /// <summary>The suggested answer.</summary>
    public string Default { get; set; } = string.Empty;

    /// <summary>The operator must give a non-empty answer.</summary>
    public bool Required { get; set; }

    /// <summary>
    /// Optional regular expression the whole answer must match. It is evaluated with the non-backtracking engine and a time limit, so a hostile pattern
    /// cannot hang the program; patterns that engine does not support are reported as invalid.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Fewest characters allowed (0 = no minimum).</summary>
    public int MinLength { get; set; }

    /// <summary>Most characters allowed (0 = the global limit).</summary>
    public int MaxLength { get; set; }

    /// <summary>The allowed answers of a <see cref="PromptInputType.Choice"/> prompt.</summary>
    public List<string> Choices { get; } = new();

    /// <summary>Convert the answer to upper case.</summary>
    public bool Uppercase { get; set; }

    /// <summary>Offer the last answer given for this label as the starting value next time.</summary>
    public bool RememberLast { get; set; }

    /// <summary>A longer explanation shown next to the question.</summary>
    public string HelpText { get; set; } = string.Empty;

    /// <summary>
    /// When the data record already has a column with this name, the record wins and the question is not asked (default). Set this to ask anyway
    /// and let the answer win over the record.
    /// </summary>
    public bool OverrideRecord { get; set; }

    /// <summary>Pattern used to write a <see cref="PromptInputType.Date"/> answer on the label (default <c>yyyy-MM-dd</c>); a preset id or a .NET custom pattern.</summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>A deep copy.</summary>
    public PromptVariableOptions Clone()
    {
        var copy = (PromptVariableOptions)MemberwiseClone();
        copy.Choices.Clear();
        copy.Choices.AddRange(Choices);
        return copy;
    }
}

/// <summary>Settings of a <see cref="VariableKind.Computed"/> variable.</summary>
public sealed class ComputedVariableOptions
{
    /// <summary>
    /// The expression, for example <c>join('-', upper(left(sku, 3)), padleft(qty, 4, '0'))</c>. See <see cref="Expressions.ExpressionParser"/> for the grammar.
    /// </summary>
    public string Expression { get; set; } = string.Empty;

    /// <summary>A copy.</summary>
    public ComputedVariableOptions Clone() => (ComputedVariableOptions)MemberwiseClone();
}

/// <summary>Hard limits that keep untrusted definitions (files, assistants) from using up memory or time.</summary>
public static class VariableLimits
{
    /// <summary>Longest variable name.</summary>
    public const int MaxNameLength = 64;
    /// <summary>Longest text value of any variable (answers, results, prefixes).</summary>
    public const int MaxValueLength = 10_000;
    /// <summary>Most offsets on one date variable.</summary>
    public const int MaxOffsets = 16;
    /// <summary>Largest absolute amount of one offset.</summary>
    public const int MaxOffsetAmount = 100_000;
    /// <summary>Most holidays on one date variable.</summary>
    public const int MaxHolidays = 400;
    /// <summary>Longest format pattern.</summary>
    public const int MaxFormatLength = 64;
    /// <summary>Longest expression, in characters.</summary>
    public const int MaxExpressionLength = 1000;
    /// <summary>Most choices of a prompt.</summary>
    public const int MaxChoices = 200;
    /// <summary>Longest regular expression of a prompt.</summary>
    public const int MaxPatternLength = 200;
    /// <summary>Longest label or help text of a prompt.</summary>
    public const int MaxPromptTextLength = 500;
    /// <summary>Longest prefix or suffix of a counter.</summary>
    public const int MaxAffixLength = 32;
}
