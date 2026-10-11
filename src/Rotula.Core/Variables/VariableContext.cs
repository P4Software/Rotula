using Rotula.Core.Validation;
using Rotula.Core.Variables.Counters;

namespace Rotula.Core.Variables;

/// <summary>
/// Everything outside the label design that the values of its variables depend on: the clock, the data record, the operator's answers and the
/// counter memory. The generator never reads the clock or a file by itself; it only sees what is in this object, so the same label and context
/// always give byte-identical ZPL.
/// </summary>
public sealed class VariableContext
{
    /// <summary>The clock and time zone (default: the computer's). Tests pass a fake <see cref="TimeProvider"/> with a fixed zone.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>
    /// A fixed instant to use instead of reading <see cref="Time"/>. A batch sets it once so every label carries the same date even when the run
    /// crosses midnight; leave it null to read the clock for each label.
    /// </summary>
    public DateTimeOffset? Now { get; set; }

    /// <summary>The raw data record (merge columns, case-insensitive). Null when the label is printed without data.</summary>
    public IReadOnlyDictionary<string, string>? Record { get; set; }

    /// <summary>The operator's answers by variable name. Null when nobody was asked.</summary>
    public IReadOnlyDictionary<string, string>? PromptAnswers { get; set; }

    /// <summary>
    /// When true a prompt without an answer is an error (<see cref="VariableMessageKeys.PromptMissingAnswer"/>); otherwise its default (or sample
    /// value) stands in, which is what a design-time preview wants.
    /// </summary>
    public bool RequirePromptAnswers { get; set; }

    /// <summary>Where counters that remember their value look up their position. Null means such counters count from their start.</summary>
    public ICounterStore? CounterStore { get; set; }

    /// <summary>Zero-based position of this label in the run (the record number of a batch). Added to a counter's first position.</summary>
    public long ItemIndex { get; set; }

    /// <summary>
    /// First position of every counter for this run, by counter name (case-insensitive); set by <see cref="VariableJob"/> after it reserved the block.
    /// A counter without an entry starts at the store's next position when it remembers its value (a preview), otherwise at 0.
    /// </summary>
    public IReadOnlyDictionary<string, long>? CounterBase { get; set; }

    /// <summary>The current instant.</summary>
    public DateTimeOffset UtcNow => Now ?? Time.GetUtcNow();

    /// <summary>A shallow copy (the collections are shared and are never modified by the engine).</summary>
    public VariableContext Clone() => (VariableContext)MemberwiseClone();

    /// <summary>A copy for the label at position <paramref name="itemIndex"/> with <paramref name="record"/> as its data.</summary>
    public VariableContext ForItem(long itemIndex, IReadOnlyDictionary<string, string>? record)
    {
        var copy = Clone();
        copy.ItemIndex = itemIndex;
        copy.Record = record;
        return copy;
    }
}

/// <summary>Where a printer-clock date variable lands on the printer.</summary>
/// <param name="VariableName">The variable.</param>
/// <param name="ClockSet">1 = the primary clock, 2 or 3 = the secondary or third clock defined with <c>^SO</c>.</param>
/// <param name="TokenFormat">The format with <c>%</c> before every clock code letter.</param>
public sealed record PrinterBinding(string VariableName, int ClockSet, string TokenFormat);

/// <summary>The outcome of resolving all variables of a label for one label of a run.</summary>
public sealed class VariableResolution
{
    internal VariableResolution(Dictionary<string, string> values, List<ValidationIssue> issues, Dictionary<string, PrinterBinding> printer, Dictionary<int, PrinterOffset> offsets)
    {
        Values = values;
        Issues = issues;
        PrinterBindings = printer;
        ClockOffsets = offsets;
    }

    /// <summary>
    /// The value of every variable (case-insensitive): the data record plus the resolved variables. For a printer-clock date this is the value the
    /// computer clock gives, which a preview shows; the printed ZPL carries clock codes instead.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>Problems found; errors mean the label should not be printed as it is.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>True when any issue is an error.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);

    /// <summary>Date variables that the printer's clock prints, by name.</summary>
    public IReadOnlyDictionary<string, PrinterBinding> PrinterBindings { get; }

    /// <summary>The <c>^SO</c> offset of clock set 2 and 3 when the label needs them.</summary>
    public IReadOnlyDictionary<int, PrinterOffset> ClockOffsets { get; }
}
