using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Editing;
using Rotula.Files;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Zpl;
using Rotula.Data;
using Rotula.Printing;

namespace Rotula.App.ViewModels;

/// <summary>Where the number of copies per record comes from.</summary>
public enum CopiesSource
{
    /// <summary>The label's own print quantity.</summary>
    LabelDefault,
    /// <summary>The same number for every record.</summary>
    Fixed,
    /// <summary>A column of the data.</summary>
    Column,
}

/// <summary>Where the records come from.</summary>
public enum DataSourceMode
{
    /// <summary>A CSV or Excel file.</summary>
    File,
    /// <summary>A saved database connection (a table, view or SELECT query).</summary>
    Database,
}

/// <summary>
/// The Data merge window: open a CSV or Excel file (or read a database table or query), map its columns to the label's placeholders, walk through the records
/// (the canvas shows the chosen record), set up serial counters and copies, check every record, and print or export the batch.
/// Only the settings are saved in the .rotula file; the data itself never is.
/// </summary>
public sealed partial class DataMergeViewModel : OperationViewModel
{
    private const int IssueRowLimit = 500;

    private readonly IPrintService _service;
    private readonly Func<LabelDocument> _document;
    private readonly Func<string> _documentName;
    private readonly IFileDialogService _files;
    private readonly IMessageService _messages;
    private readonly IVariableServices _variables;
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ColumnChoice> _columnChoices = new();
    private bool _applying;
    private bool _rebuildingRows;
    private char? _detectedDelimiter;
    private IReadOnlyList<string> _rowPlaceholders = Array.Empty<string>();
    private IReadOnlyList<string> _rowColumns = Array.Empty<string>();

    /// <summary>Creates the view-model.</summary>
    /// <param name="service">Printing service.</param>
    /// <param name="printer">Printer and profile fields (shared with the Print dialog).</param>
    /// <param name="document">The open label.</param>
    /// <param name="documentName">Name of the open label, used to suggest file names.</param>
    /// <param name="files">File dialogs.</param>
    /// <param name="messages">Message boxes (confirmation before a large print).</param>
    /// <param name="database">Saved connections and database access; null uses the real ones (nothing is read until the user asks).</param>
    /// <param name="dialogs">Opens the connection manager; null disables it (tests).</param>
    /// <param name="variables">Counter memory and the print-time questions; null means no counter memory and no questions (tests).</param>
    public DataMergeViewModel(IPrintService service, PrinterSettingsViewModel printer, Func<LabelDocument> document, Func<string> documentName,
        IFileDialogService files, IMessageService messages, IDatabaseServices? database = null, IToolDialogService? dialogs = null,
        IVariableServices? variables = null)
    {
        _variables = variables ?? new VariableServices(null, null);
        _service = service;
        Printer = printer;
        _document = document;
        _documentName = documentName;
        _files = files;
        _messages = messages;
        Database = new DatabaseSourceViewModel(database ?? new DatabaseServices(), dialogs, messages, files);
        Database.DataSelected += OnDatabaseDataSelected;
        Database.ReferenceReloaded += (_, _) => RememberedDataReloaded?.Invoke(this, EventArgs.Empty);

        DelimiterChoices = new[]
        {
            new Choice<string>("auto", "Data.Delimiter.Auto"), new Choice<string>("comma", "Data.Delimiter.Comma"),
            new Choice<string>("semicolon", "Data.Delimiter.Semicolon"), new Choice<string>("tab", "Data.Delimiter.Tab"),
        };
        CopiesSourceChoices = new[]
        {
            new Choice<CopiesSource>(CopiesSource.LabelDefault, "Data.Copies.Label"), new Choice<CopiesSource>(CopiesSource.Fixed, "Data.Copies.Fixed"),
            new Choice<CopiesSource>(CopiesSource.Column, "Data.Copies.Column"),
        };
        CopiesModeChoices = new[]
        {
            new Choice<CopiesMode>(CopiesMode.PrintQuantity, "Enum.CopiesMode.PrintQuantity"),
            new Choice<CopiesMode>(CopiesMode.RepeatBlock, "Enum.CopiesMode.RepeatBlock"),
        };
        InvalidPolicyChoices = new[]
        {
            new Choice<InvalidRecordPolicy>(InvalidRecordPolicy.Skip, "Enum.InvalidRecordPolicy.Skip"),
            new Choice<InvalidRecordPolicy>(InvalidRecordPolicy.Stop, "Enum.InvalidRecordPolicy.Stop"),
        };
        Loc.Instance.SubscribeWeak(this, static self => self.RefreshLanguage());
    }

    // ----------------------------------------------------------------------------------- choices

    /// <summary>Printer selection and profile, shared with the Print dialog.</summary>
    public PrinterSettingsViewModel Printer { get; }

    /// <summary>The database half of the source picker (connections, tables, query).</summary>
    public DatabaseSourceViewModel Database { get; }

    /// <summary>Delimiter choices for CSV files.</summary>
    public IReadOnlyList<Choice<string>> DelimiterChoices { get; }

    /// <summary>Copies source choices.</summary>
    public IReadOnlyList<Choice<CopiesSource>> CopiesSourceChoices { get; }

    /// <summary>How copies are written (^PQ or repeated labels).</summary>
    public IReadOnlyList<Choice<CopiesMode>> CopiesModeChoices { get; }

    /// <summary>What to do with invalid records.</summary>
    public IReadOnlyList<Choice<InvalidRecordPolicy>> InvalidPolicyChoices { get; }

    // ----------------------------------------------------------------------------------- events

    /// <summary>Raised when the settings that are saved in the .rotula file were edited by the user (not when they were loaded).</summary>
    public event EventHandler? SettingsEdited;

    /// <summary>Raised when the values shown on the canvas and in the ZPL view changed (record, mapping, counters, data).</summary>
    public event EventHandler? PreviewDataChanged;

    /// <summary>Raised when the loaded table changed, so the view can rebuild the preview grid columns.</summary>
    public event EventHandler? TableChanged;

    private void RaiseSettingsEdited()
    {
        if (!_applying) SettingsEdited?.Invoke(this, EventArgs.Empty);
    }

    private void RaisePreviewChanged() => PreviewDataChanged?.Invoke(this, EventArgs.Empty);

    // ----------------------------------------------------------------------------------- source

    // ----------------------------------------------------------------------------------- source kind (file or database)

    /// <summary>The database settings the current (or remembered) data came from; this is what the .rotula file keeps. Never holds credentials.</summary>
    private DatabaseMergeSettings? _databaseSettings;

    /// <summary>Where the records come from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileMode), nameof(IsDatabaseMode))]
    private DataSourceMode _sourceMode;

    partial void OnSourceModeChanged(DataSourceMode value) => NotifyLayout();

    /// <summary>True when the records come from a CSV or Excel file (binds the "File" option).</summary>
    public bool IsFileMode
    {
        get => SourceMode == DataSourceMode.File;
        set { if (value) SourceMode = DataSourceMode.File; }
    }

    /// <summary>True when the records come from a database (binds the "Database" option).</summary>
    public bool IsDatabaseMode
    {
        get => SourceMode == DataSourceMode.Database;
        set
        {
            if (!value || SourceMode == DataSourceMode.Database) return;
            SourceMode = DataSourceMode.Database;
            // Reading the list of saved connections is a local file read; no database is contacted.
            _ = Database.RefreshConnectionsAsync();
        }
    }

    /// <summary>True while the database picker is shown even though data is already loaded ("Change data source").</summary>
    [ObservableProperty]
    private bool _showDatabasePicker;

    partial void OnShowDatabasePickerChanged(bool value) => NotifyLayout();

    /// <summary>The database picker (connection, table or query) fills the area.</summary>
    public bool ShowDatabasePanel => IsDatabaseMode && (HasNoData || ShowDatabasePicker);

    /// <summary>The tabs (data, mapping, counters...) fill the area.</summary>
    public bool ShowTabs => HasData && !(IsDatabaseMode && ShowDatabasePicker);

    /// <summary>The file "open" card fills the area.</summary>
    public bool ShowFileEmpty => IsFileMode && HasNoData;

    /// <summary>The sheet picker applies (Excel file in file mode).</summary>
    public bool ShowXlsxOptions => IsFileMode && IsXlsx;

    /// <summary>The delimiter picker applies (text file in file mode).</summary>
    public bool ShowCsvOptions => IsFileMode && IsCsv;

    /// <summary>The "first row is a header" box applies (files only; a database always has column names).</summary>
    public bool ShowHeaderOption => IsFileMode;

    /// <summary>True when data from a database is shown in the tabs (the header offers "Change data source").</summary>
    public bool CanChangeDatabaseSource => IsDatabaseMode && HasData;

    /// <summary>Header text of a database source, for example "Warehouse (database)".</summary>
    public string DatabaseSummary => _databaseSettings is null
        ? Loc.Get("Data.Db.NoSource")
        : Loc.Get("Data.Db.Summary", _databaseSettings.Reference.ConnectionName);

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(ShowDatabasePanel));
        OnPropertyChanged(nameof(ShowTabs));
        OnPropertyChanged(nameof(ShowFileEmpty));
        OnPropertyChanged(nameof(ShowXlsxOptions));
        OnPropertyChanged(nameof(ShowCsvOptions));
        OnPropertyChanged(nameof(ShowHeaderOption));
        OnPropertyChanged(nameof(CanChangeDatabaseSource));
        OnPropertyChanged(nameof(DatabaseSummary));
    }

    /// <summary>Opens the database picker over the loaded data so another table or query can be chosen.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeDatabaseSource))]
    private void ChangeDatabaseSource()
    {
        ShowDatabasePicker = true;
        _ = Database.RefreshConnectionsAsync();
    }

    /// <summary>Goes back from the picker to the loaded data.</summary>
    [RelayCommand]
    private void HideDatabasePicker() => ShowDatabasePicker = false;

    /// <summary>
    /// The user chose rows in the database picker. They flow through exactly the same pipeline as a file: the table is bound to
    /// the label by <see cref="Rebind"/>, so mapping, counters, copies, the record navigator and batch print/export just work.
    /// </summary>
    private void OnDatabaseDataSelected(object? sender, DatabaseDataSelectedEventArgs e)
    {
        _databaseSettings = e.Settings;
        _applying = true;
        try
        {
            SourceHintIgnored = false;
            SourcePath = null; // a database label has no file path; the file hint is dropped, not kept beside the query
            SelectedSheet = null;
            SourceMode = DataSourceMode.Database;
            ShowDatabasePicker = false;
        }
        finally { _applying = false; }
        SetTable(e.Table, Array.Empty<string>(), null);
        CurrentIndex = 0;
        SetResult(Loc.Get("Data.Db.Loaded", e.Table.RowCount, e.Settings.Reference.ConnectionName), false);
        RaiseSettingsEdited();
        Rebind();
        RaisePreviewChanged();
    }

    /// <summary>The loaded table, or null when no data is loaded. The data is never saved.</summary>
    public MergeTable? Table { get; private set; }

    /// <summary>The table bound to the label with the current options.</summary>
    public MergeDataSet? DataSet { get; private set; }

    /// <summary>True when data is loaded.</summary>
    public bool HasData => Table is not null;

    /// <summary>True when no data is loaded (shows the empty state).</summary>
    public bool HasNoData => Table is null;

    /// <summary>The data file path. After opening a .rotula file this is only a hint to where the data was last time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceName), nameof(HasSource), nameof(IsXlsx), nameof(IsCsv), nameof(CanReload))]
    private string? _sourcePath;

    partial void OnSourcePathChanged(string? value) => NotifyLayout();

    /// <summary>File name of the data source.</summary>
    public string SourceName => SourcePath is null ? string.Empty : Path.GetFileName(SourcePath);

    /// <summary>True when a data file is known (loaded or remembered).</summary>
    public bool HasSource => !string.IsNullOrEmpty(SourcePath);

    /// <summary>True when the source is an Excel workbook.</summary>
    public bool IsXlsx => SourcePath?.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>True when the source is a text (CSV) file, where the delimiter matters.</summary>
    public bool IsCsv => !IsXlsx;

    /// <summary>
    /// True when a remembered file can be offered for reloading. Deliberately does not look at the file system: the path may
    /// come from a .rotula file, and probing it while the file is merely being opened could reach a remote machine.
    /// The file is only touched after the user clicks Reload.
    /// </summary>
    public bool CanReload => HasSource && !IsBusy;

    /// <summary>True when the .rotula file named a data path that was ignored for safety.</summary>
    [ObservableProperty]
    private bool _sourceHintIgnored;

    /// <summary>True when the remembered file is not loaded yet (shows the "reload" hint).</summary>
    public bool ShowReloadHint => HasSource && Table is null;

    /// <summary>Worksheets of the workbook.</summary>
    public ObservableCollection<string> Sheets { get; } = new();

    /// <summary>The worksheet to read.</summary>
    [ObservableProperty]
    private string? _selectedSheet;

    partial void OnSelectedSheetChanged(string? value) => SourceOptionEdited();

    /// <summary>Delimiter choice for CSV files: "auto", "comma", "semicolon" or "tab".</summary>
    [ObservableProperty]
    private string _selectedDelimiter = "auto";

    partial void OnSelectedDelimiterChanged(string value) => SourceOptionEdited();

    /// <summary>True when the first row holds column names.</summary>
    [ObservableProperty]
    private bool _hasHeader = true;

    partial void OnHasHeaderChanged(bool value) => SourceOptionEdited();

    /// <summary>Text such as "Delimiter detected: semicolon (;)".</summary>
    [ObservableProperty]
    private string _delimiterText = string.Empty;

    /// <summary>Notes from reading the file (for example an unclosed quote).</summary>
    public ObservableCollection<string> LoadWarnings { get; } = new();

    /// <summary>Column names of the records grid.</summary>
    public IReadOnlyList<string> PreviewColumns => Table?.Columns ?? Array.Empty<string>();

    /// <summary>Text such as "120 records, 5 columns".</summary>
    public string TableSummary => Table is null ? string.Empty : Loc.Get("Data.Summary", Table.RowCount, Table.Columns.Count);

    private void SourceOptionEdited()
    {
        if (_applying || IsBusy) return;
        RaiseSettingsEdited();
        if (Table is not null && HasSource) _ = LoadAsync(SourcePath!);
    }

    /// <summary>Asks for a data file and loads it.</summary>
    [RelayCommand]
    private async Task OpenDataAsync()
    {
        if (IsBusy) return;
        string? path = _files.PickOpenData();
        if (path is null) return;
        // A different file starts from its own first sheet and detected delimiter.
        _applying = true;
        SelectedSheet = null;
        SelectedDelimiter = "auto";
        _applying = false;
        await LoadAsync(path);
    }

    /// <summary>Raised after the label's remembered data (a file, or the saved query of a database) was read again by a Reload / Connect and reload click.</summary>
    public event EventHandler? RememberedDataReloaded;

    /// <summary>Loads the remembered data file again.</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (!HasSource || IsBusy) return;
        // The user asked for it, so now (and only now) the stored path is looked at. It was already checked to be a local
        // drive path when it was read; a mapped network drive letter is refused here as well.
        string? local = SafeLocalPath.TryGetLocalPath(SourcePath);
        if (local is null || !SafeLocalPath.IsFixedOrRemovableDrive(local))
        {
            SetResult(Loc.Get("Data.Reload.NotLocal", SourcePath ?? string.Empty), true);
            return;
        }
        if (!File.Exists(local))
        {
            SetResult(Loc.Get("Data.Reload.Missing", local), true);
            return;
        }
        if (await LoadAsync(local)) RememberedDataReloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Brings back the data a label was saved with, right after the user opened a label this PC trusts, so the fields and barcodes show real
    /// values instead of empty boxes. It does exactly what the Reload / "Connect and reload data" buttons do, so every safety
    /// check stays: a file must be on a local drive, a query goes through the read-only guard, and a database is reached only
    /// through a connection the user already saved under that name (the label stores no host or password). A failure is
    /// shown in the Data window as before and never raises a dialog; the buttons remain for a retry.
    /// </summary>
    public async Task ReloadRememberedDataAsync()
    {
        try
        {
            if (SourceMode == DataSourceMode.Database)
            {
                if (Database.IsReferencePending) await Database.ConnectReferenceAsync();
            }
            else if (HasSource && Table is null)
            {
                await ReloadAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The label is open and usable; the reason is in the Data window's own message.
            SetResult(ex.Message, true);
        }
    }

    /// <summary>Forgets the loaded data (the settings stay).</summary>
    [RelayCommand]
    private void ClearData()
    {
        SetTable(null, Array.Empty<string>(), null);
        RaiseSettingsEdited();
        RaisePreviewChanged();
    }

    /// <summary>Reads a CSV or Excel file off the UI thread and shows it. On failure the previous data stays and the reason is shown.</summary>
    /// <returns>True when the file was loaded.</returns>
    public async Task<bool> LoadAsync(string path)
    {
        char? delimiter = SelectedDelimiter switch { "comma" => ',', "semicolon" => ';', "tab" => '\t', _ => null };
        bool header = HasHeader;
        string? sheet = SelectedSheet;
        bool xlsx = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
        string fileName = Path.GetFileName(path);
        MergeTable? table = null;
        IReadOnlyList<string> sheets = Array.Empty<string>();
        char? detected = null;
        string? error = null;

        bool finished = await RunAsync(Loc.Get("Data.Busy.Loading", fileName), ct => Task.Run(() =>
        {
            try
            {
                // FileShare.ReadWrite: Excel keeps a workbook open for reading; the file can still be read while it is.
                if (xlsx)
                {
                    using (var names = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) sheets = XlsxReader.GetSheetNames(names);
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    string? useSheet = sheet is not null && sheets.Contains(sheet) ? sheet : null;
                    table = XlsxReader.Read(stream, new XlsxOptions { SheetName = useSheet, HasHeader = header });
                    sheet = useSheet ?? sheets.FirstOrDefault();
                }
                else
                {
                    string text = ReadText(path);
                    detected = CsvReader.DetectDelimiter(text);
                    table = CsvReader.Parse(text, new CsvOptions { Delimiter = delimiter, HasHeader = header });
                }
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = Loc.Get("Data.Error.Read", fileName);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = Loc.Get("Data.Error.Format", fileName);
            }
        }, ct));
        if (!finished) return false;
        if (error is null && table is { Columns.Count: 0 }) error = Loc.Get("Data.Error.Empty", fileName);
        if (error is not null)
        {
            SetResult(error, true);
            return false;
        }

        // Reloading the same file is not a change to the label; only a different file or sheet is.
        bool settingsChanged = !string.Equals(SourcePath, path, StringComparison.OrdinalIgnoreCase) || (xlsx && SelectedSheet != sheet)
            || _databaseSettings is not null;
        _applying = true;
        try
        {
            SourceHintIgnored = false;
            SourcePath = path;
            SelectedSheet = xlsx ? sheet : null;
            // A file replaces a database source: the label must not keep pointing at a query it no longer uses.
            _databaseSettings = null;
            Database.DiscardReference();
            SourceMode = DataSourceMode.File;
            ShowDatabasePicker = false;
        }
        finally { _applying = false; }
        SetTable(table, sheets, delimiter ?? detected);
        CurrentIndex = 0;
        SetResult(Loc.Get("Data.Loaded", fileName, table!.RowCount), false);
        if (settingsChanged) RaiseSettingsEdited();
        Rebind();
        RaisePreviewChanged();
        return true;
    }

    /// <summary>Reads a text file as UTF-8 (or UTF-16 with a byte order mark), falling back to Windows-1252, which is what Excel writes for "CSV".</summary>
    private static string ReadText(string path)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            // Touching ZplEncoding registers the legacy code pages.
            int page = ZplEncoding.Cp1252.DotNetCodePage ?? 1252;
            return Encoding.GetEncoding(page).GetString(bytes);
        }
    }

    private void SetTable(MergeTable? table, IReadOnlyList<string> sheets, char? delimiter)
    {
        Table = table;
        DataSet = null;
        // New data starts with every record ticked, no filter and the original order (the selection is never kept across data sets).
        ResetRecords(table);
        _detectedDelimiter = delimiter;
        Sheets.Clear();
        foreach (var s in sheets) Sheets.Add(s);
        LoadWarnings.Clear();
        if (table is not null)
            foreach (var issue in table.Issues) LoadWarnings.Add(Loc.FormatIssue(issue));
        UpdateDelimiterText();
        _rowColumns = Array.Empty<string>(); // force the mapping rows to be rebuilt for the new columns
        OnPropertyChanged(nameof(Table));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(HasNoData));
        OnPropertyChanged(nameof(ShowReloadHint));
        OnPropertyChanged(nameof(CanReload));
        OnPropertyChanged(nameof(PreviewColumns));
        OnPropertyChanged(nameof(TableSummary));
        OnPropertyChanged(nameof(HasLoadWarnings));
        NotifyLayout();
        ChangeDatabaseSourceCommand.NotifyCanExecuteChanged();
        TableChanged?.Invoke(this, EventArgs.Empty);
        Rebind();
    }

    /// <summary>True when reading the file produced notes.</summary>
    public bool HasLoadWarnings => LoadWarnings.Count > 0;

    private void UpdateDelimiterText()
    {
        if (Table is null || IsXlsx || _detectedDelimiter is not { } d) { DelimiterText = string.Empty; return; }
        string name = d switch { ',' => Loc.Get("Data.Delimiter.Comma"), ';' => Loc.Get("Data.Delimiter.Semicolon"), '\t' => Loc.Get("Data.Delimiter.Tab"), _ => d.ToString() };
        DelimiterText = Loc.Get(SelectedDelimiter == "auto" ? "Data.Delimiter.Detected" : "Data.Delimiter.Using", name);
    }

    // ----------------------------------------------------------------------------------- mapping

    /// <summary>The placeholders of the label and the column behind each.</summary>
    public ObservableCollection<MappingRow> MappingRows { get; } = new();

    /// <summary>Warnings about the mapping (unmapped placeholders, unused columns...), in plain language.</summary>
    public ObservableCollection<string> MappingWarnings { get; } = new();

    /// <summary>True when there are mapping warnings.</summary>
    public bool HasMappingWarnings => MappingWarnings.Count > 0;

    /// <summary>True when the label has no placeholders at all (so the mapping list is empty on purpose).</summary>
    public bool LabelHasNoPlaceholders => MappingRows.Count == 0 && HasData;

    /// <summary>Columns that can feed the "copies" setting.</summary>
    public IReadOnlyList<string> ColumnNames => Table?.Columns ?? Array.Empty<string>();

    private void OnMappingRowChanged(MappingRow row)
    {
        if (_rebuildingRows) return;
        if (row.Selected.Column is { } col) _overrides[row.Placeholder] = col;
        else _overrides.Remove(row.Placeholder);
        RaiseSettingsEdited();
        Rebind();
        RaisePreviewChanged();
    }

    /// <summary>The fields of the loaded data, counters and label variables, for the "Add to label" buttons of the Variables tab. Set by the main view-model.</summary>
    public FieldsViewModel? Fields { get; set; }

    /// <summary>
    /// Makes <paramref name="placeholder"/> read from <paramref name="column"/>. Used when a column's own name cannot be a placeholder
    /// (spaces, accents) and the field picker wrote a cleaned-up name for it. The match is saved with the label, like any other mapping.
    /// </summary>
    public void EnsureColumnMapping(string placeholder, string column)
    {
        if (_overrides.TryGetValue(placeholder, out var existing) && string.Equals(existing, column, StringComparison.OrdinalIgnoreCase)) return;
        _overrides[placeholder] = column;
        RaiseSettingsEdited();
        Rebind();
        RaisePreviewChanged();
    }

    /// <summary>Called when the open label changed; rebuilds the mapping only when the placeholders changed.</summary>
    public void OnDocumentChanged() => Rebind();

    /// <summary>
    /// Binds the data to the label again with the current options and refreshes everything derived from it (mapping list,
    /// warnings, record count). Cheap enough to run on every edit of the label.
    /// </summary>
    public void Rebind()
    {
        var doc = _document();
        var placeholders = MergeDataSet.CollectPlaceholders(doc);
        var options = BuildOptions();
        if (Table is null)
        {
            DataSet = null;
            RebuildRows(placeholders, Array.Empty<string>());
            MappingWarnings.Clear();
            OnPropertyChanged(nameof(HasMappingWarnings));
            UpdateNavigator();
            return;
        }
        DataSet = new MergeDataSet(doc, Table, options);
        if (!_rowPlaceholders.SequenceEqual(placeholders) || !_rowColumns.SequenceEqual(Table.Columns))
            RebuildRows(placeholders, Table.Columns);
        UpdateRowStatus();
        MappingWarnings.Clear();
        foreach (var issue in DataSet.Issues) MappingWarnings.Add(Loc.FormatIssue(issue));
        OnPropertyChanged(nameof(HasMappingWarnings));
        OnPropertyChanged(nameof(LabelHasNoPlaceholders));
        UpdateNavigator();
    }

    private void RebuildRows(IReadOnlyList<string> placeholders, IReadOnlyList<string> columns)
    {
        _rebuildingRows = true;
        try
        {
            _rowPlaceholders = placeholders.ToList();
            _rowColumns = columns.ToList();
            _columnChoices.Clear();
            _columnChoices.Add(new ColumnChoice(null));
            foreach (var c in columns) _columnChoices.Add(new ColumnChoice(c));
            MappingRows.Clear();
            foreach (var p in placeholders)
            {
                bool counter = p.StartsWith('#');
                var choices = _columnChoices.ToList();
                var selected = choices[0];
                if (!counter && _overrides.TryGetValue(p, out var col))
                {
                    selected = choices.FirstOrDefault(c => string.Equals(c.Column, col, StringComparison.OrdinalIgnoreCase)) ?? new ColumnChoice(col);
                    if (!choices.Contains(selected)) choices.Add(selected); // a saved column that the file no longer has stays visible (and is warned about)
                }
                MappingRows.Add(new MappingRow(p, choices, selected, counter, OnMappingRowChanged));
            }
        }
        finally { _rebuildingRows = false; }
        OnPropertyChanged(nameof(ColumnNames));
    }

    private void UpdateRowStatus()
    {
        if (DataSet is null || Table is null) return;
        foreach (var row in MappingRows)
        {
            if (row.IsCounter)
            {
                bool known = DataSet.Options.Counters.Any(c => string.Equals(c.VirtualColumn, row.Placeholder, StringComparison.OrdinalIgnoreCase));
                row.SetStatus(known ? Loc.Get("Data.Mapping.FromCounter") : Loc.Get("Data.Mapping.NoCounter"), !known);
            }
            else if (DataSet.Mapping.TryGetValue(row.Placeholder, out int idx))
                row.SetStatus(Loc.Get("Data.Mapping.Uses", Table.Columns[idx]), false);
            else
                row.SetStatus(Loc.Get("Data.Mapping.None"), true);
        }
    }

    // ----------------------------------------------------------------------------------- navigator

    /// <summary>Zero-based index of the record shown on the canvas.</summary>
    [ObservableProperty]
    private int _currentIndex;

    partial void OnCurrentIndexChanged(int value)
    {
        UpdateNavigator();
        OnPropertyChanged(nameof(CurrentRow));
        RaisePreviewChanged();
    }

    /// <summary>Number of records.</summary>
    public int RecordCount => DataSet?.RecordCount ?? 0;

    /// <summary>"Record 3 of 120", or a hint when there is no data. With only some records ticked it counts the ticked ones (see the records list).</summary>
    public string RecordText => BuildRecordText();

    /// <summary>Record number typed in the "go to" box.</summary>
    [ObservableProperty]
    private string _goToText = string.Empty;

    /// <summary>True to show the chosen record on the canvas and in the ZPL view.</summary>
    [ObservableProperty]
    private bool _previewEnabled = true;

    partial void OnPreviewEnabledChanged(bool value) => RaisePreviewChanged();

    /// <summary>Values of the shown record, or null when no record is shown (no data, or preview switched off).</summary>
    public IReadOnlyDictionary<string, string>? CurrentData
    {
        get
        {
            if (!PreviewEnabled || DataSet is null || RecordCount == 0) return null;
            return DataSet.GetRecordData(Math.Clamp(CurrentIndex, 0, RecordCount - 1));
        }
    }

    private void UpdateNavigator()
    {
        if (RecordCount > 0 && (CurrentIndex < 0 || CurrentIndex >= RecordCount))
        {
            // A smaller data set (or a record number out of range) never leaves the pointer past the end.
            int clamped = Math.Clamp(CurrentIndex, 0, RecordCount - 1);
            if (clamped != CurrentIndex) { CurrentIndex = clamped; return; }
        }
        OnPropertyChanged(nameof(RecordCount));
        OnPropertyChanged(nameof(RecordText));
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(RecordNumberText));
        FirstRecordCommand.NotifyCanExecuteChanged();
        PreviousRecordCommand.NotifyCanExecuteChanged();
        NextRecordCommand.NotifyCanExecuteChanged();
        LastRecordCommand.NotifyCanExecuteChanged();
        GoToRecordCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Ready));
        PrintAllCommand.NotifyCanExecuteChanged();
        ExportBatchCommand.NotifyCanExecuteChanged();
        CheckAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True when there is at least one record.</summary>
    public bool HasRecords => RecordCount > 0;

    /// <summary>1-based number of the shown record.</summary>
    public string RecordNumberText => RecordCount == 0 ? string.Empty : (CurrentIndex + 1).ToString(CultureInfo.CurrentCulture);

    /// <summary>True when data is loaded and at least one record is ticked (enables the batch buttons).</summary>
    public bool Ready => HasRecords && SelectedCount > 0;

    // The four navigator commands step through the TICKED records in data order; see MoveFirst, MovePrevious, MoveNext and MoveLast.

    /// <summary>Goes to the first ticked record.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void FirstRecord() => MoveFirst();

    /// <summary>Goes to the previous ticked record.</summary>
    [RelayCommand(CanExecute = nameof(CanStepBack))]
    private void PreviousRecord() => MovePrevious();

    /// <summary>Goes to the next ticked record.</summary>
    [RelayCommand(CanExecute = nameof(CanStepForward))]
    private void NextRecord() => MoveNext();

    /// <summary>Goes to the last ticked record.</summary>
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void LastRecord() => MoveLast();

    /// <summary>Goes to the record number typed in the box.</summary>
    [RelayCommand(CanExecute = nameof(HasRecords))]
    private void GoToRecord()
    {
        if (int.TryParse(GoToText.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out int n) && n >= 1 && n <= RecordCount)
        {
            CurrentIndex = n - 1;
            SetResult(string.Empty, false);
        }
        else SetResult(Loc.Get("Data.Record.GoToInvalid", RecordCount), true);
    }

    // ----------------------------------------------------------------------------------- counters

    /// <summary>The serial counters.</summary>
    public ObservableCollection<CounterEditor> Counters { get; } = new();

    /// <summary>The counter being edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCounter))]
    private CounterEditor? _selectedCounter;

    /// <summary>True when a counter is selected.</summary>
    public bool HasSelectedCounter => SelectedCounter is not null;

    /// <summary>True when no counter exists yet (empty state).</summary>
    public bool HasNoCounters => Counters.Count == 0;

    private CounterEditor NewCounterEditor(CounterSettings s) => new(s, OnCounterEdited);

    private void OnCounterEdited()
    {
        if (_applying) return;
        RaiseSettingsEdited();
        Rebind();
        RaisePreviewChanged();
    }

    /// <summary>Adds a counter with a free name.</summary>
    [RelayCommand]
    private void AddCounter()
    {
        string name = "Serial";
        for (int n = 2; Counters.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = "Serial" + n;
        var editor = NewCounterEditor(new CounterSettings { Name = name, Padding = 4 });
        Counters.Add(editor);
        SelectedCounter = editor;
        OnPropertyChanged(nameof(HasNoCounters));
        OnCounterEdited();
    }

    /// <summary>Removes the selected counter.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedCounter))]
    private void RemoveCounter()
    {
        if (SelectedCounter is null) return;
        int index = Counters.IndexOf(SelectedCounter);
        Counters.Remove(SelectedCounter);
        SelectedCounter = Counters.Count == 0 ? null : Counters[Math.Min(index, Counters.Count - 1)];
        OnPropertyChanged(nameof(HasNoCounters));
        OnCounterEdited();
    }

    partial void OnSelectedCounterChanged(CounterEditor? value) => RemoveCounterCommand.NotifyCanExecuteChanged();

    // ----------------------------------------------------------------------------------- copies and batch options

    /// <summary>Where the copies per record come from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixedCopies), nameof(IsColumnCopies))]
    private CopiesSource _copiesSource;

    partial void OnCopiesSourceChanged(CopiesSource value) => OptionsEdited();

    /// <summary>True when the copies are a fixed number.</summary>
    public bool IsFixedCopies => CopiesSource == CopiesSource.Fixed;

    /// <summary>True when the copies come from a column.</summary>
    public bool IsColumnCopies => CopiesSource == CopiesSource.Column;

    /// <summary>Fixed copies as typed.</summary>
    [ObservableProperty]
    private string _fixedCopiesText = "1";

    partial void OnFixedCopiesTextChanged(string value) => OptionsEdited();

    /// <summary>Column that holds the copies per record.</summary>
    [ObservableProperty]
    private string? _copiesColumn;

    partial void OnCopiesColumnChanged(string? value) => OptionsEdited();

    /// <summary>How copies are written.</summary>
    [ObservableProperty]
    private CopiesMode _copiesMode = CopiesMode.PrintQuantity;

    partial void OnCopiesModeChanged(CopiesMode value) => OptionsEdited();

    /// <summary>What to do with an invalid record.</summary>
    [ObservableProperty]
    private InvalidRecordPolicy _invalidPolicy = InvalidRecordPolicy.Skip;

    partial void OnInvalidPolicyChanged(InvalidRecordPolicy value) => OptionsEdited();

    /// <summary>Records per print job as typed (0 = one job).</summary>
    [ObservableProperty]
    private string _maxRecordsText = "0";

    partial void OnMaxRecordsTextChanged(string value) => OptionsEdited();

    /// <summary>Write one file per job when exporting.</summary>
    [ObservableProperty]
    private bool _oneFilePerJob;

    partial void OnOneFilePerJobChanged(bool value) => RaiseSettingsEdited();

    /// <summary>What is wrong with the copies or job size, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOptionsError))]
    private string _optionsError = string.Empty;

    /// <summary>True when an option has an invalid value.</summary>
    public bool HasOptionsError => OptionsError.Length > 0;

    private void OptionsEdited()
    {
        if (_applying) return;
        RaiseSettingsEdited();
        Rebind();
        RaisePreviewChanged();
    }

    /// <summary>
    /// Builds a fresh <see cref="MergeOptions"/> from the editor fields (never the shared instance, so a background batch
    /// keeps its own copy while the user keeps editing). Invalid fields set <see cref="OptionsError"/> and fall back to defaults.
    /// </summary>
    private MergeOptions BuildOptions()
    {
        var o = new MergeOptions { CopiesMode = CopiesMode, OnInvalidRecord = InvalidPolicy };
        foreach (var ed in Counters)
            if (ed.TryBuild() is { } counter) o.Counters.Add(counter);
        foreach (var kv in _overrides) o.ColumnOverrides[kv.Key] = kv.Value;
        // Only the ticked records are merged, in data order. "Everything ticked" is passed as null, which is the plain path with the old behaviour.
        if (Table is not null && SelectedCount < Table.RowCount) o.SelectedRecords = SelectedArray();

        string error = string.Empty;
        switch (CopiesSource)
        {
            case CopiesSource.Fixed:
                if (int.TryParse(FixedCopiesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int copies) && copies is >= 1 and <= 99999999) o.CopiesPerRecord = copies;
                else error = Loc.Get("Data.Copies.FixedInvalid");
                break;
            case CopiesSource.Column:
                o.CopiesColumn = CopiesColumn;
                break;
        }
        if (int.TryParse(MaxRecordsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int max) && max is >= 0 and <= 1_000_000) o.MaxRecordsPerJob = max;
        else if (error.Length == 0) error = Loc.Get("Data.MaxRecords.Invalid");
        OptionsError = error;
        return o;
    }

    // ----------------------------------------------------------------------------------- checking and batch

    /// <summary>Problems found by the last check or batch, one line per problem.</summary>
    public ObservableCollection<RecordIssueRow> Issues { get; } = new();

    /// <summary>Summary of the last check.</summary>
    [ObservableProperty]
    private string _issuesSummary = string.Empty;

    /// <summary>True when the issue list has content to show.</summary>
    [ObservableProperty]
    private bool _hasIssues;

    /// <summary>
    /// Takes a private copy of the label and options for a background run, and checks that a run makes sense.
    /// Why a copy: the user may keep editing the label while the batch runs.
    /// </summary>
    /// <returns>Null when ready; otherwise a message saying what to fix.</returns>
    /// <param name="doc">The private copy of the label.</param>
    /// <param name="dataSet">The merge data set built on that copy.</param>
    /// <param name="convertToPrinter">True to convert the copy to the chosen printer resolution (printing, or exporting for a chosen printer).</param>
    /// <param name="quantity">When set, replaces the copy's print quantity (the Print dialog's copies); the open label is never touched.</param>
    private string? Snapshot(out LabelDocument doc, out MergeDataSet dataSet, bool convertToPrinter = false, int? quantity = null)
    {
        doc = ElementCloner.CloneDocument(_document());
        if (quantity is { } q) doc.Print.Quantity = q;
        dataSet = null!;
        if (convertToPrinter)
        {
            var converted = Printer.ConvertForPrinter(doc, out _, out string? convertError);
            if (converted is null) return convertError;
            doc = converted;
        }
        dataSet = null!;
        if (Table is null) return Loc.Get("Data.Error.NoData");
        var options = BuildOptions();
        if (OptionsError.Length > 0) return OptionsError;
        // Serial numbers that remember their value need the memory; without it they refuse to print instead of repeating numbers.
        options.CounterStore = _variables.CounterStore;
        options.TextRasterizer = _variables.TextRasterizer;
        var bad = Counters.FirstOrDefault(c => c.HasError);
        if (bad is not null) return bad.Error;
        dataSet = new MergeDataSet(doc, Table, options);
        var blocking = dataSet.Issues.FirstOrDefault(i => i.Severity == ValidationSeverity.Error);
        if (blocking is not null) return Loc.FormatIssue(blocking);
        if (dataSet.RecordCount == 0) return Loc.Get("Data.Error.NoRecords");
        if (dataSet.BatchCount == 0) return Loc.Get("Data.Error.NoneSelected");
        return null;
    }

    private void ShowReport(MergeReport report)
    {
        Issues.Clear();
        foreach (var ri in report.Issues.Take(IssueRowLimit))
            Issues.Add(new RecordIssueRow(ri.RecordNumber, ri.Issue.Severity == ValidationSeverity.Error, Loc.FormatIssue(ri.Issue)));
        string summary = Loc.Get("Data.Check.Summary", report.Processed, report.Emitted, report.SkippedRecords.Count);
        if (report.Issues.Count > IssueRowLimit) summary += " " + Loc.Get("Data.Check.Truncated", IssueRowLimit, report.Issues.Count);
        IssuesSummary = summary;
        HasIssues = true;
    }

    /// <summary>
    /// Asks the label's print-time questions and stores the answers in the batch options (the same answers for every record). A question that
    /// a data column answers is not asked. Returns false when the operator cancelled.
    /// </summary>
    private bool AskBatchPrompts(MergeDataSet ds)
    {
        var answers = _variables.Ask(ds.BuildPromptSet(_variables.PromptMemory));
        if (answers is null) return false;
        foreach (var kv in answers) ds.Options.PromptAnswers[kv.Key] = kv.Value;
        return true;
    }

    /// <summary>
    /// Fills the answers with each question's suggested value (remembered answer, default or sample) without asking, for a check that must not
    /// interrupt the operator. The check then shows how the label behaves with the suggestions.
    /// </summary>
    private void UseSuggestedAnswers(MergeDataSet ds)
    {
        foreach (var item in ds.BuildPromptSet(_variables.PromptMemory).Items) ds.Options.PromptAnswers[item.Name] = item.InitialValue;
    }

    /// <summary>Generates every ticked record in the background and lists the problems, without printing or writing anything.</summary>
    [RelayCommand(CanExecute = nameof(Ready))]
    private async Task CheckAllAsync()
    {
        if (IsBusy) return;
        string? error = Snapshot(out var doc, out var ds);
        if (error is not null) { SetResult(error, true); return; }
        var report = new MergeReport();
        var progress = new Progress<double>(p => Progress = p);
        UseSuggestedAnswers(ds);
        // A check prints nothing, so it must not use up serial numbers: the run owns a job and gives every number back afterwards.
        VariableJob? checkJob = null;
        if (VariableEngine.HasDefinitions(doc))
        {
            checkJob = VariableJob.Begin(doc, ds.CreateBatchContext(), ds.BatchCount);
            ds.Options.Job = checkJob;
        }
        bool ok = await RunAsync(Loc.Get("Data.Busy.Checking"), ct => Task.Run(() =>
        {
            try
            {
                int n = 0;
                foreach (var _ in MergeBatch.Labels(doc, ds, report))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++n % 256 == 0) ((IProgress<double>)progress).Report(100.0 * report.Processed / ds.BatchCount);
                }
            }
            finally { checkJob?.Cancel(); }
        }, ct));
        if (!ok) return;
        ShowReport(report);
        SetResult(report.HasErrors ? Loc.Get("Data.Check.Problems", report.SkippedRecords.Count) : Loc.Get("Data.Check.Ok", report.Emitted), report.HasErrors);
    }

    /// <summary>Sends the ticked records to the printer (in data order), in jobs of at most the chosen number of records.</summary>
    [RelayCommand(CanExecute = nameof(Ready))]
    private async Task PrintAllAsync()
    {
        if (IsBusy) return;
        var outcome = await PrintBatchAsync(RunAsync, p => Progress = p, null);
        if (outcome is { } o) SetResult(o.Text, o.IsError);
    }

    /// <summary>
    /// The batch print shared by this window and the Print dialog, so both go through one tested path (serial numbers reserved first and confirmed
    /// only for labels that reached the printer, print-time questions, jobs, partial and cancel handling).
    /// </summary>
    /// <param name="run">The host window's single-operation runner (busy state and cancel); it shows its own message when the work is cancelled or crashes.</param>
    /// <param name="progress">Receives 0-100 while jobs go out.</param>
    /// <param name="labelCopies">The Print dialog's copies per record, used when the copies setting is the label's own quantity; null keeps the label's own.</param>
    /// <returns>The result line to show, or null when nothing more is to be said (declined, cancelled, or the host already showed the outcome).</returns>
    internal async Task<BatchOutcome?> PrintBatchAsync(Func<string, Func<CancellationToken, Task>, Task<bool>> run, Action<double> progress, int? labelCopies)
    {
        string? error = Snapshot(out var doc, out var ds, convertToPrinter: true, quantity: labelCopies);
        PrinterTarget? target = null;
        PrinterProfile? profile = null;
        error ??= Printer.TryBuildTarget(out target);
        error ??= Printer.TryBuildProfile(out profile, doc.Encoding);
        if (error is not null) return new BatchOutcome(error, true);
        // The question says exactly what will leave: the ticked records, and how many labels that is once copies are counted.
        long labels = CountLabels(ds, doc.Print.Quantity);
        string question = ds.BatchCount < ds.RecordCount
            ? Loc.Get("Data.PrintAll.ConfirmSelected", ds.BatchCount, Printer.Summary, labels, ds.RecordCount)
            : labels != ds.BatchCount
                ? Loc.Get("Data.PrintAll.ConfirmCopies", ds.BatchCount, Printer.Summary, labels)
                : Loc.Get("Data.PrintAll.Confirm", ds.BatchCount, Printer.Summary);
        if (!_messages.Confirm(Loc.Get("Data.PrintAll.Title"), question)) return null;
        if (!AskBatchPrompts(ds)) return null;

        var report = new MergeReport();
        int jobs = 0;
        PrintError? failure = null;
        bool cancelled = false;
        // The run owns the serial-number reservation, so only labels that really reached the printer are confirmed. MergeBatch alone would
        // count every label as used the moment it is generated, and a failed send would then burn the numbers of the labels after it.
        VariableJob? serials = null;
        if (VariableEngine.HasDefinitions(doc))
        {
            serials = VariableJob.Begin(doc, ds.CreateBatchContext(), ds.BatchCount);
            ds.Options.Job = serials;
        }
        int confirmedThrough = 0; // record number of the last label that was sent, or may have been (a send in doubt counts as sent)
        var closing = new List<ValidationIssue>();
        // The printer set-up (darkness, speed...) goes out once, in front of the first job.
        string setup = PrinterSetup.BuildSetupZpl(profile!);
        // Raw ZPL imported from bytes must reach the printer byte for byte; collected once, not per record.
        var rawBlocks = ByteExactBlocks.From(doc);
        var progressReporter = new Progress<double>(progress);
        bool ok = await run(Loc.Get("Data.Busy.Printing"), async ct =>
        {
            int inFlight = 0; // last record of the job being sent right now
            try
            {
                await Task.Run(async () =>
                {
                    foreach (var job in MergeBatch.JobBlocks(doc, ds, report))
                    {
                        ct.ThrowIfCancellationRequested();
                        inFlight = job.ItemsThrough;
                        var result = await _service.PrintZplAsync(target!, jobs == 0 ? setup + job.Zpl : job.Zpl, doc.Encoding, rawBlocks, ct);
                        if (!result.Succeeded)
                        {
                            failure = result.Error;
                            // A send that moved bytes may have printed labels: their numbers stay used. One that moved nothing gives them back.
                            if (LabelPrintRun.MayHavePrinted(result)) confirmedThrough = inFlight;
                            break;
                        }
                        confirmedThrough = inFlight;
                        inFlight = 0;
                        jobs++;
                        ((IProgress<double>)progressReporter).Report(100.0 * report.Processed / ds.BatchCount);
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                // Cancelled while a job was on its way: it may have printed, so its numbers are not given back.
                if (inFlight > 0) confirmedThrough = inFlight;
            }
            finally
            {
                // Always confirms (also when the run failed or was cancelled), so a reservation is never left hanging.
                if (serials is not null) closing.AddRange(serials.Complete(confirmedThrough));
            }
        });
        if (!ok) return null;
        ShowReport(report);
        string notes = string.Join(" ", closing.Select(Loc.FormatIssue));
        if (failure is not null)
        {
            return new BatchOutcome(PrintMessages.Describe(failure) + " " + Loc.Get("Data.Batch.Partial", jobs) + (notes.Length > 0 ? " " + notes : string.Empty),
                failure.Kind != PrintErrorKind.Cancelled);
        }
        if (cancelled)
            return new BatchOutcome(Loc.Get("Data.Batch.CancelledPartial", jobs) + (notes.Length > 0 ? " " + notes : string.Empty), false);
        Printer.Persist();
        return new BatchOutcome(BatchSummary("Data.PrintAll.Done", report, jobs) + (notes.Length > 0 ? " " + notes : string.Empty), report.HasErrors || report.Emitted == 0);
    }

    /// <summary>The result line of a batch print or export, and whether it reports a problem.</summary>
    internal readonly record struct BatchOutcome(string Text, bool IsError);

    private static string BatchSummary(string doneKey, MergeReport report, int units)
    {
        if (report.Emitted == 0) return Loc.Get("Data.Batch.NothingProduced");
        string text = Loc.Get(doneKey, report.Emitted, units);
        if (report.SkippedRecords.Count > 0) text += " " + Loc.Get("Data.Batch.Skipped", report.SkippedRecords.Count);
        if (report.Stopped) text += " " + Loc.Get("Data.Batch.Stopped", report.SkippedRecords[^1]);
        return text;
    }

    /// <summary>Writes the whole batch as ZPL, in one file or one file per job.</summary>
    [RelayCommand(CanExecute = nameof(Ready))]
    private async Task ExportBatchAsync()
    {
        if (IsBusy) return;
        var outcome = await ExportBatchCoreAsync(RunAsync, p => Progress = p, null);
        if (outcome is { } o) SetResult(o.Text, o.IsError);
    }

    /// <summary>The batch export shared by this window and the Print dialog. Parameters and result as in <see cref="PrintBatchAsync"/>.</summary>
    internal async Task<BatchOutcome?> ExportBatchCoreAsync(Func<string, Func<CancellationToken, Task>, Task<bool>> run, Action<double> progress, int? labelCopies)
    {
        // With no printer chosen the file keeps the design's own resolution; with one, it matches what that printer would receive.
        string? error = Snapshot(out var doc, out var ds, convertToPrinter: Printer.PrinterChosen, quantity: labelCopies);
        if (error is not null) return new BatchOutcome(error, true);
        string? path = _files.PickExportFile(ExportKind.Zpl, _documentName() + ".zpl");
        if (path is null) return null;

        int max = ds.Options.MaxRecordsPerJob;
        bool perJob = OneFilePerJob && max > 0;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        string NameFor(int job) => Path.Combine(dir, $"{stem}_{job:D3}{ext}");
        if (perJob)
        {
            int possible = (ds.BatchCount + max - 1) / max;
            int existing = Enumerable.Range(1, possible).Count(i => File.Exists(NameFor(i)));
            if (existing > 0 && !_messages.Confirm(Loc.Get("Data.Export.ReplaceTitle"), Loc.Get("Data.Export.ReplaceBody", existing)))
                return null;
        }

        // The file will be printed later, so its serial numbers count as used (the safe side). Only numbers that really reached a file are
        // confirmed: if the export fails before the file is complete, the numbers go back.
        if (!AskBatchPrompts(ds)) return null;
        VariableJob? serials = null;
        if (VariableEngine.HasDefinitions(doc))
        {
            serials = VariableJob.Begin(doc, ds.CreateBatchContext(), ds.BatchCount);
            ds.Options.Job = serials;
        }
        int writtenThrough = 0; // record number of the last label that is in a finished file
        var closing = new List<ValidationIssue>();

        var report = new MergeReport();
        int files = 0;
        string? writeError = null;
        bool encodingFailed = false;
        var rawBlocks = ByteExactBlocks.From(doc);
        var progressReporter = new Progress<double>(progress);
        bool ok = await run(Loc.Get("Data.Busy.Exporting"), ct => Task.Run(() =>
        {
            string temp = path + ".tmp";
            try
            {
                if (perJob)
                {
                    int job = 0;
                    foreach (var merged in MergeBatch.JobBlocks(doc, ds, report))
                    {
                        ct.ThrowIfCancellationRequested();
                        var bytes = ZplFiles.TryEncode(merged.Zpl, doc.Encoding, rawBlocks);
                        if (bytes is null) { encodingFailed = true; return; }
                        File.WriteAllBytes(NameFor(++job), bytes);
                        files++;
                        writtenThrough = merged.ItemsThrough;
                        ((IProgress<double>)progressReporter).Report(100.0 * report.Processed / ds.BatchCount);
                    }
                }
                else
                {
                    // Written to a temp file first, so a cancelled or failed export never destroys an existing file.
                    int lastInTemp = 0;
                    using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    {
                        foreach (var merged in MergeBatch.JobBlocks(doc, ds, report))
                        {
                            ct.ThrowIfCancellationRequested();
                            var bytes = ZplFiles.TryEncode(merged.Zpl, doc.Encoding, rawBlocks);
                            if (bytes is null) { encodingFailed = true; return; }
                            fs.Write(bytes);
                            lastInTemp = merged.ItemsThrough;
                            ((IProgress<double>)progressReporter).Report(100.0 * report.Processed / ds.BatchCount);
                        }
                    }
                    if (report.Emitted > 0) { File.Move(temp, path, overwrite: true); files = 1; writtenThrough = lastInTemp; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writeError = Loc.Get("File.Error.Write", path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (serials is not null) closing.AddRange(serials.Complete(writtenThrough));
            }
        }, ct));
        if (!ok) return null;
        ShowReport(report);
        string notes = string.Join(" ", closing.Select(Loc.FormatIssue));
        if (writeError is not null) return new BatchOutcome(writeError, true);
        if (encodingFailed) return new BatchOutcome(Loc.Get("Print.Error.EncodingFailed", doc.Encoding.ToString(), string.Empty), true);
        return new BatchOutcome(BatchSummary(perJob ? "Data.Export.DonePerJob" : "Data.Export.Done", report, files) + (notes.Length > 0 ? " " + notes : string.Empty),
            report.HasErrors || report.Emitted == 0);
    }

    /// <summary>Closes the window (stopping any running batch or database operation first).</summary>
    [RelayCommand]
    private void Close()
    {
        Database.CancelOperation();
        RequestClose();
    }

    // ----------------------------------------------------------------------------------- settings in the .rotula file

    /// <summary>The settings to save in the .rotula file. The data itself is never included.</summary>
    public DataMergeSettings ToSettings()
    {
        var s = new DataMergeSettings
        {
            SourcePath = SourcePath, SheetName = SelectedSheet, Delimiter = SelectedDelimiter, HasHeader = HasHeader,
            CopiesPerRecord = CopiesSource == CopiesSource.Fixed && int.TryParse(FixedCopiesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int c) ? c : null,
            CopiesColumn = CopiesSource == CopiesSource.Column ? CopiesColumn : null,
            CopiesMode = CopiesMode, OnInvalidRecord = InvalidPolicy,
            MaxRecordsPerJob = int.TryParse(MaxRecordsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int m) ? m : 0,
            OneFilePerJob = OneFilePerJob,
            // Only the connection NAME, the query text and parameter defaults: never host, user, password or connection string.
            Database = _databaseSettings,
        };
        foreach (var kv in _overrides) s.ColumnOverrides[kv.Key] = kv.Value;
        foreach (var c2 in Counters) s.Counters.Add(c2.ToSettings());
        return s;
    }

    /// <summary>Loads settings from a .rotula file (or resets to defaults for null). The data is not loaded; the source path stays a hint.</summary>
    public void Apply(DataMergeSettings? settings)
    {
        settings ??= new DataMergeSettings();
        _applying = true;
        try
        {
            SourcePath = string.IsNullOrEmpty(settings.SourcePath) ? null : settings.SourcePath;
            SourceHintIgnored = settings.SourcePathIgnored;
            // A query reference is only remembered and shown. Nothing connects until the user clicks "Connect and reload data".
            _databaseSettings = settings.Database;
            SourceMode = settings.Database is null ? DataSourceMode.File : DataSourceMode.Database;
            ShowDatabasePicker = false;
            Database.Restore(settings.Database);
            SelectedSheet = settings.SheetName;
            SelectedDelimiter = settings.Delimiter;
            HasHeader = settings.HasHeader;
            _overrides.Clear();
            foreach (var kv in settings.ColumnOverrides) _overrides[kv.Key] = kv.Value;
            Counters.Clear();
            foreach (var c in settings.Counters) Counters.Add(NewCounterEditor(c));
            SelectedCounter = Counters.FirstOrDefault();
            OnPropertyChanged(nameof(HasNoCounters));
            CopiesSource = settings.CopiesPerRecord is not null ? CopiesSource.Fixed : !string.IsNullOrEmpty(settings.CopiesColumn) ? CopiesSource.Column : CopiesSource.LabelDefault;
            FixedCopiesText = (settings.CopiesPerRecord ?? 1).ToString(CultureInfo.InvariantCulture);
            CopiesColumn = settings.CopiesColumn;
            CopiesMode = settings.CopiesMode;
            InvalidPolicy = settings.OnInvalidRecord;
            MaxRecordsText = settings.MaxRecordsPerJob.ToString(CultureInfo.InvariantCulture);
            OneFilePerJob = settings.OneFilePerJob;
            Issues.Clear();
            IssuesSummary = string.Empty;
            HasIssues = false;
            SetResult(string.Empty, false);
            SetTable(null, Array.Empty<string>(), null);
            CurrentIndex = 0;
            OnPropertyChanged(nameof(ShowReloadHint));
        }
        finally { _applying = false; }
        Rebind();
        RaisePreviewChanged();
    }

    private void RefreshLanguage()
    {
        foreach (var c in DelimiterChoices) c.Refresh();
        foreach (var c in CopiesSourceChoices) c.Refresh();
        foreach (var c in CopiesModeChoices) c.Refresh();
        foreach (var c in InvalidPolicyChoices) c.Refresh();
        foreach (var c in _columnChoices) c.Refresh();
        foreach (var c in Counters) c.RefreshLanguage();
        UpdateDelimiterText();
        Rebind();
        NotifyLayout();
        OnPropertyChanged(nameof(TableSummary));
        OnPropertyChanged(nameof(RecordText));
        RefreshRecordsLanguage();
    }
}
