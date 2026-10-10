using Rotula.Core.Barcodes;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Core.Zpl.Import;

/// <summary>Options for <see cref="ZplParser"/>.</summary>
public sealed class ZplParseOptions
{
    /// <summary>
    /// Printhead resolution of the imported label. ZPL does not carry it, so the caller (or the user) supplies it;
    /// it only matters for <c>^MU</c> unit conversion and for how the document reports its size.
    /// </summary>
    public PrinterDpi Dpi { get; set; } = PrinterDpi.Dpi203;
}

/// <summary>
/// Something worth telling the user about an import: an unknown command that was kept as raw ZPL, a recall that
/// cannot be resolved, a value that was out of range. Core never carries UI text; the UI looks
/// <see cref="MessageKey"/> up in its resources and formats it with <see cref="Args"/>.
/// </summary>
public sealed class ZplImportWarning
{
    internal ZplImportWarning(string messageKey, int position, int line, int column, string? command, string? elementId, IReadOnlyList<string> args)
    {
        MessageKey = messageKey;
        Position = position;
        Line = line;
        Column = column;
        Command = command;
        ElementId = elementId;
        Args = args;
    }

    /// <summary>Stable resource key, for example <c>Import.Command.Unknown</c>.</summary>
    public string MessageKey { get; }

    /// <summary>Positional format arguments for the localized message (culture-neutral strings).</summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>Zero-based offset into the input (characters for text input, bytes for byte input); -1 when unknown.</summary>
    public int Position { get; }

    /// <summary>One-based line of <see cref="Position"/>; 0 when unknown.</summary>
    public int Line { get; }

    /// <summary>One-based column of <see cref="Position"/>; 0 when unknown.</summary>
    public int Column { get; }

    /// <summary>The command the warning is about including its prefix (for example <c>^IS</c>), when there is one.</summary>
    public string? Command { get; }

    /// <summary>Id of the element created for this part of the file, when there is one.</summary>
    public string? ElementId { get; }

    /// <summary>The warning as a <see cref="ValidationIssue"/> so the existing issue list can show it.</summary>
    public ValidationIssue ToValidationIssue() => new(ValidationSeverity.Warning, MessageKey, ElementId, Args.ToArray());

    /// <inheritdoc />
    public override string ToString() => $"{MessageKey}({string.Join(", ", Args)}) at {Line}:{Column}";
}

/// <summary>Outcome of parsing ZPL. Parsing never throws; problems are listed in <see cref="Warnings"/>.</summary>
public sealed class ZplParseResult
{
    internal ZplParseResult(IReadOnlyList<LabelDocument> documents, IReadOnlyList<ZplImportWarning> warnings, IReadOnlyList<BarPatternMatch> barPatterns)
    {
        Documents = documents;
        Warnings = warnings;
        BarPatterns = barPatterns;
    }

    /// <summary>The first label of the file (an empty label when the input had none).</summary>
    public LabelDocument Document => Documents[0];

    /// <summary>Every <c>^XA..^XZ</c> label in the file, in order; never empty.</summary>
    public IReadOnlyList<LabelDocument> Documents { get; }

    /// <summary>Everything that was kept raw, approximated or could not be resolved, in file order.</summary>
    public IReadOnlyList<ZplImportWarning> Warnings { get; }

    /// <summary>Barcodes drawn as bars in <see cref="Document"/>; use <see cref="BarPatternRecognizer"/> to convert them.</summary>
    public IReadOnlyList<BarPatternMatch> BarPatterns { get; }

    /// <summary>True when there is at least one warning.</summary>
    public bool HasWarnings => Warnings.Count > 0;
}
