using Rotula.Core.Imaging;
using Rotula.Core.Model;

namespace Rotula.App.Services;

// Every operating-system or UI facility that a view-model needs sits behind one of these interfaces, so the view-models
// run unchanged in unit tests with simple fakes (no windows, no dialogs, no clipboard).

/// <summary>The kind of file an export writes.</summary>
public enum ExportKind
{
    /// <summary>PNG image at printer resolution.</summary>
    Png,
    /// <summary>Single-page PDF the size of the label.</summary>
    Pdf,
    /// <summary>Raw ZPL text file.</summary>
    Zpl,
}

/// <summary>Answer of the "save changes?" question.</summary>
public enum SaveChangesAnswer
{
    /// <summary>Save, then continue.</summary>
    Save,
    /// <summary>Continue without saving.</summary>
    Discard,
    /// <summary>Stay where we are.</summary>
    Cancel,
}

/// <summary>File open/save pickers. Each returns the chosen path, or null when the user cancelled.</summary>
public interface IFileDialogService
{
    /// <summary>Asks for a .rotula file to open.</summary>
    string? PickOpenLabel();

    /// <summary>Asks where to save a .rotula file.</summary>
    string? PickSaveLabel(string suggestedFileName);

    /// <summary>Asks where to write an export.</summary>
    string? PickExportFile(ExportKind kind, string suggestedFileName);

    /// <summary>Asks for a ZPL file to import.</summary>
    string? PickOpenZpl();

    /// <summary>Asks for an image file to place on the label.</summary>
    string? PickOpenImage();

    /// <summary>Asks for a CSV or Excel (.xlsx) data file for the data merge.</summary>
    string? PickOpenData();

    /// <summary>Asks for a SQLite database file.</summary>
    string? PickOpenDatabaseFile();

    /// <summary>Asks for a folder (the folder of text files an ODBC preset reads). The default does not offer a picker.</summary>
    string? PickFolder(string title) => null;

    /// <summary>Asks for one file whose extension is in <paramref name="extensions"/> (lower case, with the dot). The default does not offer a picker.</summary>
    string? PickOpenFile(string title, IReadOnlyList<string> extensions) => null;
}

/// <summary>Message boxes.</summary>
public interface IMessageService
{
    /// <summary>Shows an error with what happened and what to do next.</summary>
    void ShowError(string title, string message);

    /// <summary>Shows information.</summary>
    void ShowInfo(string title, string message);

    /// <summary>Asks whether to save the changes before continuing.</summary>
    SaveChangesAnswer AskSaveChanges(string documentName);

    /// <summary>Asks an OK/Cancel question (for example before sending a large batch to a printer); true means go ahead.</summary>
    bool Confirm(string title, string message);
}

/// <summary>Text clipboard.</summary>
public interface IClipboardService
{
    /// <summary>The clipboard text, or null when there is none.</summary>
    string? GetText();

    /// <summary>Puts text on the clipboard.</summary>
    void SetText(string text);
}

/// <summary>Reads image files and flattens them to the luminance bitmap Core wants (transparency becomes white).</summary>
public interface IImageLoader
{
    /// <summary>Loads an image; throws <see cref="IOException"/> or <see cref="InvalidDataException"/> when the file cannot be used.</summary>
    GrayscaleBitmap Load(string path);
}

/// <summary>What the user chose in the New label dialog.</summary>
public interface INewLabelDialogService
{
    /// <summary>Shows the dialog; returns a blank document of the chosen size, or null when cancelled.</summary>
    LabelDocument? ShowNewLabelDialog();

    /// <summary>Shows the dialog with the application context (stock library, remembered favourites, printer resolution). The default ignores the context.</summary>
    LabelDocument? ShowNewLabelDialog(NewLabelContext context) => ShowNewLabelDialog();
}

/// <summary>Outcome of importing ZPL text.</summary>
/// <param name="Document">The imported design, or null when the import failed.</param>
/// <param name="MessageKey">Resource key of the explanation shown on failure (null on success).</param>
/// <param name="Warnings">Plain-language notes about parts that could not be imported.</param>
public sealed record ZplImportResult(LabelDocument? Document, string? MessageKey, IReadOnlyList<string> Warnings)
{
    /// <summary>A successful import.</summary>
    public static ZplImportResult Success(LabelDocument document, IReadOnlyList<string>? warnings = null) =>
        new(document, null, warnings ?? Array.Empty<string>());

    /// <summary>A failed import.</summary>
    public static ZplImportResult Failure(string messageKey) => new(null, messageKey, Array.Empty<string>());
}

/// <summary>Turns ZPL back into a label design.</summary>
public interface IZplImporter
{
    /// <summary>
    /// Parses the content of a ZPL file. Bytes are passed (not text) because the text inside fields must be decoded with the
    /// <c>^CI</c> that is active at that point, which a premature UTF-8 decoding of the whole file would corrupt.
    /// </summary>
    ZplImportResult Import(byte[] zpl);
}

/// <summary>Placeholder importer used until the ZPL parser in Rotula.Core is wired in.</summary>
public sealed class StubZplImporter : IZplImporter
{
    /// <inheritdoc />
    public ZplImportResult Import(byte[] zpl) => ZplImportResult.Failure("Import.NotAvailable");
}

/// <summary>Opens the tool windows that belong to the main window (print and data merge).</summary>
public interface IToolDialogService
{
    /// <summary>Shows the Print dialog modally; returns when it is closed.</summary>
    void ShowPrintDialog(Rotula.App.ViewModels.PrintViewModel viewModel);

    /// <summary>Shows the Data merge window (not modal, so the canvas preview can be watched); brings it forward if already open.</summary>
    void ShowDataWindow(Rotula.App.ViewModels.DataMergeViewModel viewModel);

    /// <summary>Shows a larger preview of the label as the printer will draw it.</summary>
    void ShowPreviewDialog(Rotula.App.Editing.PreviewCache preview, string title);

    /// <summary>Shows the connection manager modally; returns when it is closed.</summary>
    void ShowConnectionManager(Rotula.App.ViewModels.ConnectionManagerViewModel viewModel);
}
