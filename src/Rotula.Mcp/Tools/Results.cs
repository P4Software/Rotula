#pragma warning disable CS1573 // positional record parameters are documented where the meaning is not obvious from the name

using Rotula.Mcp.Hosting;
using Rotula.Mcp.Session;

namespace Rotula.Mcp.Tools;

/// <summary>Margins of the label in the label's unit.</summary>
public sealed record MarginInfo(double Left, double Top, double Right, double Bottom);

/// <summary>Everything about the label except its elements.</summary>
public sealed record LabelInfo(
    string Name, double Width, double Height, string Unit, int Dpi, int WidthDots, int HeightDots,
    string Orientation, MarginInfo Margins, string MediaType, string MediaTracking, string DefaultRotation,
    string? DefaultFont, string Encoding, int PrintQuantity, IReadOnlyList<VariableInfo> Variables, int ElementCount,
    string? File, bool UnsavedChanges, int UndoSteps);

/// <summary>A declared variable.</summary>
/// <param name="Name">Used as {{Name}} in text and barcode data.</param>
/// <param name="Sample">Value shown when no data is merged.</param>
public sealed record VariableInfo(string Name, string Sample);

/// <summary>Result of get_label.</summary>
public sealed record GetLabelResult(LabelInfo Label, IReadOnlyList<ElementInfo> Elements);

/// <summary>Result of a tool that changes the label.</summary>
public sealed record ChangeResult(string Message, LabelInfo Label, IReadOnlyList<ElementInfo> Elements, IReadOnlyList<IssueInfo> Issues);

/// <summary>Result of tools that return a short confirmation only.</summary>
public sealed record SimpleResult(string Message);

/// <summary>Result of generate_zpl.</summary>
public sealed record ZplResult(string Zpl, bool HasErrors, IReadOnlyList<IssueInfo> Issues, int Lines, int Characters, string? SavedTo);

/// <summary>Result of validate_label.</summary>
public sealed record ValidationResult(bool Printable, int Errors, int Warnings, IReadOnlyList<IssueInfo> Issues, string Summary);

/// <summary>Result of import_zpl.</summary>
public sealed record ImportResult(string Message, LabelInfo Label, IReadOnlyList<ElementInfo> Elements, IReadOnlyList<string> Warnings, int LabelsFound);

/// <summary>Result of convert_dpi.</summary>
public sealed record ConvertDpiResult(string Message, LabelInfo Label, IReadOnlyList<string> Notes);

/// <summary>A file in an allowed folder.</summary>
public sealed record FileEntry(string Path, long Bytes, DateTime ModifiedUtc);

/// <summary>Result of list_label_files.</summary>
public sealed record FileListResult(IReadOnlyList<string> AllowedFolders, IReadOnlyList<FileEntry> Files, bool Truncated);

/// <summary>One option of a barcode type.</summary>
/// <param name="Name">The name to use.</param>
/// <param name="Where">"argument" when add_barcode / update_element take it by this name, "options" when it goes inside the options object.</param>
/// <param name="Description">What it does and the values it accepts.</param>
public sealed record BarcodeOptionInfo(string Name, string Where, string Description);

/// <summary>Describes one supported barcode type.</summary>
/// <param name="Name">The value to pass as symbology.</param>
/// <param name="Zpl">The ZPL command the type prints with.</param>
/// <param name="Kind">linear, 2D, stacked or postal.</param>
/// <param name="DataRules">The characters and the length the data may have.</param>
/// <param name="Options">The option names that apply, comma separated (see OptionDetails for the meaning).</param>
/// <param name="ExampleData">Data that is valid with the default options.</param>
/// <param name="Preview">"exact" when the preview image is scannable, otherwise what differs in the preview (some types show a placeholder).</param>
/// <param name="SupportsRotation">False when ZPL has no rotation for the type, so rotation is ignored.</param>
/// <param name="OptionDetails">Every applicable option with where it goes and what it accepts.</param>
public sealed record SymbologyInfo(string Name, string Zpl, string Kind, string DataRules, string Options,
    string ExampleData = "", string Preview = "exact", bool SupportsRotation = true, IReadOnlyList<BarcodeOptionInfo>? OptionDetails = null);

/// <summary>Result of list_symbologies.</summary>
public sealed record SymbologyList(IReadOnlyList<SymbologyInfo> Symbologies);

/// <summary>Result of get_server_info.</summary>
public sealed record ServerInfoResult(
    string Version, IReadOnlyList<string> AllowedFolders, bool PrintingAllowed, IReadOnlyList<string> AllowedPrinters,
    bool DatabaseAllowed, int MaxElements, int MaxFileBytes, int MaxCopies, int MaxDatabaseRows, string Units, bool LabelOpen);

/// <summary>A printer known to Windows.</summary>
public sealed record PrinterEntry(string Name, string Port, string Driver, bool IsDefault, bool IsOffline, bool AllowedForPrinting);

/// <summary>Result of list_printers.</summary>
public sealed record PrinterListResult(IReadOnlyList<PrinterEntry> Printers, bool PrintingEnabled);

/// <summary>Result of get_printer_info.</summary>
public sealed record PrinterInfoResult(
    string Name, string? Driver, int? DriverDpi, int? SupportedDpi, double? PaperWidthMm, double? PaperHeightMm, string Source, string Note);

/// <summary>Result of merge_preview.</summary>
public sealed record MergePreviewResult(
    string Source, int RecordIndex, int RecordCount, IReadOnlyList<string> Columns, IReadOnlyDictionary<string, string> Record,
    IReadOnlyList<string> Unmapped, IReadOnlyList<IssueInfo> Issues, string Zpl, string Note);

/// <summary>Result of run_query.</summary>
public sealed record QueryResult(
    string Connection, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated, int RowLimit, string Note);

/// <summary>Result of print_label (dry run, refusal or the confirmed send).</summary>
public sealed record PrintResultInfo(
    string Status, string Message, string? Printer, int Copies, string? LabelSize, int? PrinterDpi, string? DpiConversion,
    IReadOnlyList<string> ZplPreview, string? ConfirmationToken, DateTimeOffset? TokenExpiresUtc, IReadOnlyList<IssueInfo> Issues,
    IReadOnlyList<string> RawZplBlocks);
