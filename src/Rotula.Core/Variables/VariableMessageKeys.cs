namespace Rotula.Core.Variables;

/// <summary>
/// Message keys raised by the variable engine (dates, counters, prompts, expressions). Core never carries UI text: the UI looks a key up
/// in its resources as <c>Issue.&lt;key&gt;</c> and formats it with the positional arguments listed on each constant. All arguments are
/// culture-neutral strings. <see cref="All"/> lists every key so translators and tests can check them. The keys under <c>Variable.Expr.*</c> always start with the variable name and the 1-based position in the expression (0 when the problem is not tied to one place); the arguments documented on each constant come after those two.
/// </summary>
public static class VariableMessageKeys
{
    // ---- definitions ---------------------------------------------------------------------------

    /// <summary>The variable name is empty, too long or has characters other than letters, digits and underscore. Args: name.</summary>
    public const string NameInvalid = "Variable.Name.Invalid";
    /// <summary>Two variables share a name (names are compared ignoring case). Args: name.</summary>
    public const string NameDuplicate = "Variable.Name.Duplicate";
    /// <summary>A data column of the same name is replaced by the variable's own value. Args: name, kind.</summary>
    public const string RecordOverridden = "Variable.Record.Overridden";

    // ---- date ----------------------------------------------------------------------------------

    /// <summary>The date format pattern is empty, too long, or not usable. Args: variable, format.</summary>
    public const string DateFormatInvalid = "Variable.Date.Format.Invalid";
    /// <summary>The culture name is not known on this computer. Args: variable, culture.</summary>
    public const string DateCultureUnknown = "Variable.Date.Culture.Unknown";
    /// <summary>An offset amount is outside the allowed range or there are too many offsets. Args: variable, limit.</summary>
    public const string DateOffsetRange = "Variable.Date.Offset.Range";
    /// <summary>The computed date is outside the range of dates the program can handle. Args: variable.</summary>
    public const string DateOutOfRange = "Variable.Date.OutOfRange";
    /// <summary>A fixed date variable has no date. Args: variable.</summary>
    public const string DateFixedMissing = "Variable.Date.Fixed.Missing";
    /// <summary>Printer clock not possible: business days cannot be counted by a printer. The computer clock is used. Args: variable.</summary>
    public const string PrinterBusinessDays = "Variable.Date.Printer.BusinessDays";
    /// <summary>Printer clock not possible: rounding to the start or end of a period needs the computer clock. Args: variable.</summary>
    public const string PrinterRounding = "Variable.Date.Printer.Rounding";
    /// <summary>Printer clock not possible: UTC time needs the computer clock. Args: variable.</summary>
    public const string PrinterUtc = "Variable.Date.Printer.Utc";
    /// <summary>Printer clock not possible: the format uses something the printer cannot print. Args: variable, the part of the format.</summary>
    public const string PrinterFormat = "Variable.Date.Printer.Format";
    /// <summary>Printer clock not possible: month and day names would be printed in the printer's language, not the chosen culture. Args: variable, culture.</summary>
    public const string PrinterCulture = "Variable.Date.Printer.Culture";
    /// <summary>Printer clock not possible: a label can offset the printer clock in only two different ways and both are taken. Args: variable.</summary>
    public const string PrinterSlotsFull = "Variable.Date.Printer.SlotsFull";
    /// <summary>Printer clock not possible: the fixed-date source is not a clock. Args: variable.</summary>
    public const string PrinterFixed = "Variable.Date.Printer.Fixed";
    /// <summary>Printer clock not possible inside a barcode; the computer clock is used. Args: variable.</summary>
    public const string PrinterBarcode = "Variable.Date.Printer.Barcode";
    /// <summary>The field has its own clock indicators but none is free for this variable; the computer clock is used. Args: variable.</summary>
    public const string PrinterNoIndicator = "Variable.Date.Printer.NoIndicator";
    /// <summary>No clock indicator character is free for this field; the computer clock is used. Args: variable.</summary>
    public const string PrinterNoFreeIndicator = "Variable.Date.Printer.NoFreeIndicator";

    // ---- counter -------------------------------------------------------------------------------

    /// <summary>The counter settings are not valid. Args: variable, reason key (a <c>Merge.Counter.*</c> key).</summary>
    public const string CounterInvalid = "Variable.Counter.Invalid";
    /// <summary>The counter ran past its capacity and is set to stop instead of wrapping. Args: variable.</summary>
    public const string CounterOverflow = "Variable.Counter.Overflow";
    /// <summary>The counter's start, step, kind or padding was edited after numbers were already used. Printing is blocked until the change is accepted or the counter is reset. Args: variable, last value.</summary>
    public const string CounterSettingsChanged = "Variable.Counter.SettingsChanged";
    /// <summary>An earlier print reserved serial numbers and never finished; they were skipped. Args: variable, first, last.</summary>
    public const string CounterUnconfirmed = "Variable.Counter.Unconfirmed";
    /// <summary>The counters file is damaged. Nothing was changed; recover (the damaged file is kept aside) before printing. Args: file path.</summary>
    public const string CounterStoreCorrupt = "Variable.Counter.Store.Corrupt";
    /// <summary>The counters file was written by a newer Rotula. Args: file path, version.</summary>
    public const string CounterStoreNewer = "Variable.Counter.Store.NewerVersion";
    /// <summary>The counters file is locked by another program or cannot be written. Args: file path.</summary>
    public const string CounterStoreBusy = "Variable.Counter.Store.Busy";
    /// <summary>A counter that remembers its value needs the label to be saved first so it has an identity. Args: variable.</summary>
    public const string CounterNeedsLabelId = "Variable.Counter.NeedsLabelId";
    /// <summary>
    /// A counter that remembers its value was used without a counter store, so it can only count from its first value. A warning in a preview;
    /// an error when printing (a print must never silently start the numbers again). Args: variable.
    /// </summary>
    public const string CounterNoStore = "Variable.Counter.NoStore";

    // ---- prompt --------------------------------------------------------------------------------

    /// <summary>A required answer is missing. Args: variable.</summary>
    public const string PromptRequired = "Variable.Prompt.Required";
    /// <summary>The answer is shorter than allowed. Args: variable, minimum.</summary>
    public const string PromptTooShort = "Variable.Prompt.TooShort";
    /// <summary>The answer is longer than allowed. Args: variable, maximum.</summary>
    public const string PromptTooLong = "Variable.Prompt.TooLong";
    /// <summary>The answer does not match the required pattern. Args: variable.</summary>
    public const string PromptPattern = "Variable.Prompt.Pattern";
    /// <summary>The prompt's pattern is not a usable regular expression. Args: variable.</summary>
    public const string PromptPatternInvalid = "Variable.Prompt.Pattern.Invalid";
    /// <summary>The answer is not a number. Args: variable.</summary>
    public const string PromptNotNumber = "Variable.Prompt.NotNumber";
    /// <summary>The answer is not a date. Args: variable.</summary>
    public const string PromptNotDate = "Variable.Prompt.NotDate";
    /// <summary>The answer is not one of the choices. Args: variable.</summary>
    public const string PromptNotChoice = "Variable.Prompt.NotChoice";
    /// <summary>A choice prompt has no choices. Args: variable.</summary>
    public const string PromptNoChoices = "Variable.Prompt.NoChoices";
    /// <summary>No answer was given for a prompt while printing. Args: variable.</summary>
    public const string PromptMissingAnswer = "Variable.Prompt.Missing";

    // ---- computed / expressions ----------------------------------------------------------------

    /// <summary>The expression is empty. Args: variable.</summary>
    public const string ExpressionEmpty = "Variable.Expr.Empty";
    /// <summary>The expression is longer than allowed. Args: variable, limit.</summary>
    public const string ExpressionTooLong = "Variable.Expr.TooLong";
    /// <summary>The expression is nested too deeply. Args: variable, position, limit.</summary>
    public const string ExpressionTooDeep = "Variable.Expr.TooDeep";
    /// <summary>The expression has too many parts. Args: variable, limit.</summary>
    public const string ExpressionTooComplex = "Variable.Expr.TooComplex";
    /// <summary>A character that cannot be part of an expression. Args: variable, position, character.</summary>
    public const string ExpressionUnexpectedChar = "Variable.Expr.UnexpectedChar";
    /// <summary>Something else was expected here. Args: variable, position, what was found.</summary>
    public const string ExpressionUnexpectedToken = "Variable.Expr.UnexpectedToken";
    /// <summary>The expression ends before it is complete. Args: variable, position.</summary>
    public const string ExpressionUnexpectedEnd = "Variable.Expr.UnexpectedEnd";
    /// <summary>A quoted text is not closed. Args: variable, position.</summary>
    public const string ExpressionUnterminatedString = "Variable.Expr.UnterminatedString";
    /// <summary>A number in the expression is not valid. Args: variable, position, text.</summary>
    public const string ExpressionBadNumber = "Variable.Expr.BadNumber";
    /// <summary>A function name that does not exist. Args: variable, position, name.</summary>
    public const string ExpressionUnknownFunction = "Variable.Expr.UnknownFunction";
    /// <summary>A function was given the wrong number of arguments. Args: variable, position, name, fewest, most.</summary>
    public const string ExpressionArgumentCount = "Variable.Expr.ArgumentCount";
    /// <summary>An argument is not allowed, for example a negative length. Args: variable, function, argument number.</summary>
    public const string ExpressionBadArgument = "Variable.Expr.BadArgument";
    /// <summary>The expression uses a variable that is not declared and has no data. Args: variable, name.</summary>
    public const string ExpressionUnknownVariable = "Variable.Expr.UnknownVariable";
    /// <summary>Computed variables depend on each other in a circle. Args: variable, the chain.</summary>
    public const string ExpressionCycle = "Variable.Expr.Cycle";
    /// <summary>A result would be longer than the limit and was cut. Args: variable, limit.</summary>
    public const string ExpressionResultTooLong = "Variable.Expr.ResultTooLong";
    /// <summary>A value could not be read as a number. Args: variable, function, value.</summary>
    public const string ExpressionNotNumber = "Variable.Expr.NotNumber";
    /// <summary>A value could not be read as a date (use yyyy-MM-dd or yyyyMMdd). Args: variable, function, value.</summary>
    public const string ExpressionNotDate = "Variable.Expr.NotDate";
    /// <summary>A number or date format is not usable. Args: variable, function, format.</summary>
    public const string ExpressionBadFormat = "Variable.Expr.BadFormat";

    /// <summary>Every key above, for translators and tests.</summary>
    public static IReadOnlyList<string> All { get; } = typeof(VariableMessageKeys)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();
}
