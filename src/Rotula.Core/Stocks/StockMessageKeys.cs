using Rotula.Core.Validation;

namespace Rotula.Core.Stocks;

/// <summary>
/// Message keys raised by the stock library. Core never carries UI text: the UI looks a key up in its resources and
/// formats it with the positional arguments listed on each constant. All arguments are culture-neutral strings.
/// </summary>
public static class StockMessageKeys
{
    // ---- Files (loading catalogue files) ---------------------------------------------------------

    /// <summary>The file is larger than the allowed size and was not read. Args: file name, limit in bytes.</summary>
    public const string FileTooLarge = "Stocks.File.TooLarge";
    /// <summary>The file could not be opened or read. Args: file name.</summary>
    public const string FileUnreadable = "Stocks.File.Unreadable";
    /// <summary>The file is not valid JSON or does not have the catalogue shape. Args: file name.</summary>
    public const string FileInvalidJson = "Stocks.File.InvalidJson";
    /// <summary>The schema version is missing or newer than this Rotula understands. Args: file name, version found (empty if missing).</summary>
    public const string FileUnsupportedSchema = "Stocks.File.UnsupportedSchema";
    /// <summary>The file has an empty or missing <c>entries</c> list. Args: file name.</summary>
    public const string FileNoEntries = "Stocks.File.NoEntries";
    /// <summary>The file lists more entries than allowed; only the first ones are read. Args: file name, limit.</summary>
    public const string FileTooManyEntries = "Stocks.File.TooManyEntries";
    /// <summary>The stocks folder holds more files than allowed; the extra ones are ignored. Args: limit.</summary>
    public const string FileTooManyFiles = "Stocks.File.TooManyFiles";

    // ---- Entries -------------------------------------------------------------------------------

    /// <summary>An item of the <c>entries</c> list is not an object and was skipped. Args: file name, position (1-based).</summary>
    public const string EntryNotAnObject = "Stocks.Entry.NotAnObject";
    /// <summary>An entry has no id and was skipped. Args: file name, position (1-based).</summary>
    public const string EntryMissingId = "Stocks.Entry.MissingId";
    /// <summary>An entry id has characters that are not allowed or is too long; skipped. Args: file name, position, id.</summary>
    public const string EntryBadId = "Stocks.Entry.BadId";
    /// <summary>Two entries of one file share an id; the later one was skipped. Args: file name, id.</summary>
    public const string EntryDuplicateId = "Stocks.Entry.DuplicateId";
    /// <summary>An entry has no name; skipped. Args: entry id.</summary>
    public const string EntryMissingName = "Stocks.Entry.MissingName";
    /// <summary>A field has the wrong type or is too long; the entry was skipped. Args: entry id, field name.</summary>
    public const string EntryBadField = "Stocks.Entry.BadField";
    /// <summary>The entry has no usable width/height; skipped. Args: entry id.</summary>
    public const string EntryMissingSize = "Stocks.Entry.MissingSize";
    /// <summary>A size is outside the plausible range; skipped. Args: entry id, field name, value.</summary>
    public const string EntrySizeOutOfRange = "Stocks.Entry.SizeOutOfRange";
    /// <summary>The millimetre and inch figures of a size disagree by more than 0.6 mm; skipped. Args: entry id, field (width or height), mm, inches.</summary>
    public const string EntryUnitMismatch = "Stocks.Entry.UnitMismatch";
    /// <summary>A numeric field is outside its allowed range; skipped. Args: entry id, field name, value.</summary>
    public const string EntryValueOutOfRange = "Stocks.Entry.ValueOutOfRange";
    /// <summary>The source URL is not an https address; skipped. Args: entry id.</summary>
    public const string EntryBadUrl = "Stocks.Entry.BadUrl";
    /// <summary>The entry carries a price-like field; the field was ignored and never stored. Args: entry id, field name.</summary>
    public const string EntryPriceFieldIgnored = "Stocks.Entry.PriceFieldIgnored";

    // ---- Validator only (community contributions) ---------------------------------------------

    /// <summary>A verified entry lacks complete size data. Args: entry id, what is missing.</summary>
    public const string ValidateIncompleteVerified = "Stocks.Validate.IncompleteVerified";
    /// <summary>An entry has neither a source id nor an https source URL. Args: entry id.</summary>
    public const string ValidateNoProvenance = "Stocks.Validate.NoProvenance";
    /// <summary>The metadata block is missing or incomplete. Args: what is missing.</summary>
    public const string ValidateBadMetadata = "Stocks.Validate.BadMetadata";
    /// <summary>An entry cites a source id that the metadata does not declare (warning). Args: entry id, source id.</summary>
    public const string ValidateUnknownSource = "Stocks.Validate.UnknownSource";
    /// <summary>The verified flag disagrees with the counts in the metadata (warning). Args: stated, found.</summary>
    public const string ValidateCountMismatch = "Stocks.Validate.CountMismatch";

    // ---- Applying a stock to a label -------------------------------------------------------------

    /// <summary>The entry is not verified; check the size against the real roll. Args: entry id.</summary>
    public const string ApplyUnverified = "Stocks.Apply.Unverified";
    /// <summary>The gap between labels is unknown; calibrate the printer on this stock. Args: entry id.</summary>
    public const string ApplyGapUnknown = "Stocks.Apply.GapUnknown";
    /// <summary>A continuous roll has no fixed label height; the label height was left as it was. Args: entry id, roll length in metres (empty if unknown).</summary>
    public const string ApplyContinuousNoHeight = "Stocks.Apply.ContinuousNoHeight";
    /// <summary>The stock holds several labels across or down; only one label is laid out for now. Args: columns, rows.</summary>
    public const string ApplyMultiUpNotSupported = "Stocks.Apply.MultiUpNotSupported";
    /// <summary>Cut-sheet stock: the label size was applied but not the sheet layout. Args: entry id.</summary>
    public const string ApplySheetLayoutNotSupported = "Stocks.Apply.SheetLayoutNotSupported";
    /// <summary>The label already has content and its default field rotation was not changed. Args: entry id.</summary>
    public const string ApplyRotationKept = "Stocks.Apply.RotationKept";
    /// <summary>Round or oval stock: the label is rectangular in ZPL; the shape is not drawn. Args: entry id.</summary>
    public const string ApplyRoundShape = "Stocks.Apply.RoundShape";
    /// <summary>The label already holds elements that may now lie outside the new size. Args: number of elements.</summary>
    public const string ApplyElementsMayClip = "Stocks.Apply.ElementsMayClip";

    // ---- Importing a supplies list ----------------------------------------------------------------

    /// <summary>The file type is not supported (use .csv or .xlsx). Args: extension.</summary>
    public const string ImportUnsupportedFormat = "Stocks.Import.UnsupportedFormat";
    /// <summary>The file is larger than allowed. Args: limit in bytes.</summary>
    public const string ImportFileTooLarge = "Stocks.Import.FileTooLarge";
    /// <summary>The file could not be read. Args: file name.</summary>
    public const string ImportReadFailed = "Stocks.Import.ReadFailed";
    /// <summary>The list has no data rows.</summary>
    public const string ImportNoRows = "Stocks.Import.NoRows";
    /// <summary>The list has more rows than allowed; only the first ones were read. Args: limit.</summary>
    public const string ImportTooManyRows = "Stocks.Import.TooManyRows";
    /// <summary>No column holds part numbers; nothing was imported.</summary>
    public const string ImportNoPartColumn = "Stocks.Import.NoPartColumn";
    /// <summary>No column holds sizes (and no width/height pair was found); nothing was imported.</summary>
    public const string ImportNoSizeColumn = "Stocks.Import.NoSizeColumn";
    /// <summary>A column looks like a price column and was ignored. Args: column header.</summary>
    public const string ImportPriceColumnIgnored = "Stocks.Import.PriceColumnIgnored";
    /// <summary>A column was picked from its content rather than its header; please check it. Args: role (part, size), column header.</summary>
    public const string ImportColumnGuessed = "Stocks.Import.ColumnGuessed";
    /// <summary>A row has no part number and was skipped. Args: row number (1-based, header is row 1).</summary>
    public const string ImportRowNoPartNumber = "Stocks.Import.RowNoPartNumber";
    /// <summary>A row has no readable size and was skipped. Args: row number, part number.</summary>
    public const string ImportRowNoSize = "Stocks.Import.RowNoSize";
    /// <summary>A size has no unit (inch or mm) so it was not guessed; the row was skipped. Args: row, part number, size text.</summary>
    public const string ImportRowUnitAmbiguous = "Stocks.Import.RowUnitAmbiguous";
    /// <summary>A row states two sizes that disagree; skipped. Args: row, part number, text.</summary>
    public const string ImportRowSizeConflict = "Stocks.Import.RowSizeConflict";
    /// <summary>A row's size is outside the plausible range; skipped. Args: row, part number, text.</summary>
    public const string ImportRowSizeOutOfRange = "Stocks.Import.RowSizeOutOfRange";
    /// <summary>A part number already appeared earlier in the list; the later row was skipped. Args: row, part number.</summary>
    public const string ImportRowDuplicate = "Stocks.Import.RowDuplicate";

    internal static ValidationIssue Warn(string key, params string[] args) => new(ValidationSeverity.Warning, key, null, args);

    internal static ValidationIssue Error(string key, params string[] args) => new(ValidationSeverity.Error, key, null, args);
}
