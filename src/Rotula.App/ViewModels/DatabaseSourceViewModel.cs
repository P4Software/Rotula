using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Merge;
using Rotula.Data;
using Rotula.Files;

namespace Rotula.App.ViewModels;

/// <summary>A saved connection as offered in the drop-down.</summary>
public sealed class ConnectionChoice : ObservableObject
{
    /// <summary>Creates a choice.</summary>
    /// <param name="saved">The saved connection (settings without password).</param>
    public ConnectionChoice(SavedConnection saved) => Saved = saved;

    /// <summary>The saved connection.</summary>
    public SavedConnection Saved { get; }

    /// <summary>The connection's name (the only thing a .rotula file may refer to).</summary>
    public string Name => Saved.Settings.Name;

    /// <summary>Name plus engine, for example "Warehouse - SQL Server".</summary>
    public string Display => Name + "  -  " + DataMessages.ProviderName(Saved.Settings);

    /// <summary>Name of the engine, for example "SQL Server".</summary>
    public string ProviderName => DataMessages.ProviderName(Saved.Settings);

    /// <summary>Re-reads the translated text after a language change.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(ProviderName));
    }

    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>Raised when the user chose the rows to use as the label's data.</summary>
public sealed class DatabaseDataSelectedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public DatabaseDataSelectedEventArgs(MergeTable table, DatabaseMergeSettings settings)
    {
        Table = table;
        Settings = settings;
    }

    /// <summary>The rows, ready for the merge.</summary>
    public MergeTable Table { get; }

    /// <summary>What a .rotula file keeps: connection name, query, parameter defaults, row cap.</summary>
    public DatabaseMergeSettings Settings { get; }
}

/// <summary>
/// The "Database" half of the Data merge window: pick a saved connection, browse a table or write a SELECT query (checked by
/// the read-only guard as you type), fill in its <c>@parameters</c>, run it with a row cap, and use the rows as the label's data.
/// Everything that talks to a database is asynchronous and cancellable; nothing connects until the user asks (also not when a
/// label that refers to a connection is opened: the label only carries the connection's NAME and the query text, which are
/// untrusted input).
/// </summary>
public sealed partial class DatabaseSourceViewModel : ObservableObject
{
    /// <summary>Rows shown when browsing a table.</summary>
    public const int BrowseRows = 100;

    private readonly IDatabaseServices _db;
    private readonly IToolDialogService? _dialogs;
    private readonly IMessageService _messages;
    private readonly IFileDialogService _files;
    private readonly Dictionary<string, (string Value, string Type)> _paramMemory = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DatabaseObject> _allTables = new();
    private IDatabaseSource? _source;
    private CancellationTokenSource? _cts;
    private int _operationId;
    private bool _restoring;
    private (string Signature, MergeTable Table)? _lastRun;

    /// <summary>Creates the view-model.</summary>
    public DatabaseSourceViewModel(IDatabaseServices db, IToolDialogService? dialogs, IMessageService messages, IFileDialogService files)
    {
        _db = db;
        _dialogs = dialogs;
        _messages = messages;
        _files = files;
        Loc.Instance.SubscribeWeak(this, static self => self.RefreshLanguage());
    }

    /// <summary>Raised when "Use this data" produced rows.</summary>
    public event EventHandler<DatabaseDataSelectedEventArgs>? DataSelected;

    /// <summary>Raised when the grid contents (<see cref="GridColumns"/> / <see cref="GridRows"/>) changed, so the view rebuilds its columns.</summary>
    public event EventHandler? GridChanged;

    // ----------------------------------------------------------------------------------- connections

    /// <summary>The saved connections.</summary>
    public ObservableCollection<ConnectionChoice> Connections { get; } = new();

    /// <summary>True when there is no saved connection yet (shows the first-run hint).</summary>
    public bool HasNoConnections => Connections.Count == 0;

    /// <summary>The connection to read from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnection))]
    private ConnectionChoice? _selectedConnection;

    /// <summary>True when a connection is chosen.</summary>
    public bool HasConnection => SelectedConnection is not null;

    partial void OnSelectedConnectionChanged(ConnectionChoice? value)
    {
        // A different connection means different tables, a different dialect for the guard and a new source object.
        _source = null;
        _allTables.Clear();
        _loadedTables = false;
        Tables.Clear();
        SelectedTable = null;
        ClearGrid();
        _lastRun = null;
        UpdateGuard();
        NotifyCanUse();
        if (value is not null && !_restoring && IsTableMode) _ = LoadTablesAsync();
    }

    /// <summary>Reads the saved connections again (after the manager closed, or when the window opened). Never connects to a database.</summary>
    public async Task RefreshConnectionsAsync()
    {
        string? keep = SelectedConnection?.Name;
        ConnectionStoreContent content;
        try
        {
            content = await _db.Connections.LoadAsync();
        }
        catch (DatabaseException ex)
        {
            SetError(ex);
            return;
        }
        // Update the list in place instead of clearing it: clearing makes the combo box drop the selection, which would throw away the
        // tables already listed. A connection whose settings did not change keeps its entry (and so the selection and the open source).
        var fresh = content.Connections.OrderBy(c => c.Settings.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _restoring = true;
        try
        {
            for (int i = Connections.Count - 1; i >= 0; i--)
            {
                var match = fresh.FirstOrDefault(f => string.Equals(f.Settings.Name, Connections[i].Name, StringComparison.OrdinalIgnoreCase));
                if (match is null || match != Connections[i].Saved) Connections.RemoveAt(i);
            }
            foreach (var f in fresh)
            {
                if (Connections.Any(c => string.Equals(c.Name, f.Settings.Name, StringComparison.OrdinalIgnoreCase))) continue;
                int at = 0;
                while (at < Connections.Count && string.Compare(Connections[at].Name, f.Settings.Name, StringComparison.CurrentCultureIgnoreCase) < 0) at++;
                Connections.Insert(at, new ConnectionChoice(f));
            }
            var again = keep is null ? null : Connections.FirstOrDefault(c => string.Equals(c.Name, keep, StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(again, SelectedConnection)) SelectedConnection = again;
        }
        finally { _restoring = false; }
        OnPropertyChanged(nameof(HasNoConnections));
        if (content.ErrorKey is not null) SetResult(DataMessages.DescribeStore(content.ErrorKey), true);
        _referenceChecked = true;
        UpdateReferenceState();
    }

    /// <summary>Chooses a saved connection by name and reads its tables when the table view is showing. Used by the UI and tests.</summary>
    public async Task SelectConnectionAsync(string name)
    {
        var choice = Connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        _restoring = true;
        try { SelectedConnection = choice; }
        finally { _restoring = false; }
        if (choice is not null && IsTableMode) await LoadTablesAsync();
    }

    /// <summary>Opens the connection manager on a blank form.</summary>
    [RelayCommand]
    private async Task NewConnectionAsync() => await OpenManagerAsync(startNew: true);

    /// <summary>Opens the connection manager.</summary>
    [RelayCommand]
    private async Task ManageConnectionsAsync() => await OpenManagerAsync(startNew: false);

    private async Task OpenManagerAsync(bool startNew)
    {
        if (_dialogs is null) return;
        var manager = new ConnectionManagerViewModel(_db, _messages, _files);
        await manager.LoadAsync(SelectedConnection?.Name);
        if (startNew) manager.NewConnectionCommand.Execute(null);
        _dialogs.ShowConnectionManager(manager);
        await RefreshConnectionsAsync();
        // A connection created or edited in the manager becomes the current one: that is almost always why it was opened.
        if (manager.LastSavedName is { } saved) await SelectConnectionAsync(saved);
    }

    // ----------------------------------------------------------------------------------- mode

    /// <summary>0 = table or view, 1 = query (the tab index).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTableMode), nameof(IsQueryMode))]
    private int _modeIndex;

    /// <summary>True while browsing a table or view.</summary>
    public bool IsTableMode => ModeIndex == 0;

    /// <summary>True while writing a query.</summary>
    public bool IsQueryMode => ModeIndex == 1;

    partial void OnModeIndexChanged(int value)
    {
        ClearGrid();
        if (value == 0 && SelectedTable is not null) _ = PreviewTableAsync();
        else if (value == 1 && _lastRun is { } last) ShowGrid(last.Table);
        else if (value == 0 && SelectedConnection is not null && _allTables.Count == 0) _ = LoadTablesAsync();
        NotifyCanUse();
    }

    // ----------------------------------------------------------------------------------- tables

    /// <summary>The tables and views that match <see cref="TableFilter"/>.</summary>
    public ObservableCollection<DatabaseObject> Tables { get; } = new();

    /// <summary>Text typed in the search box.</summary>
    [ObservableProperty]
    private string _tableFilter = string.Empty;

    partial void OnTableFilterChanged(string value) => ApplyTableFilter();

    /// <summary>The table or view chosen in the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTable))]
    private DatabaseObject? _selectedTable;

    /// <summary>True when a table is chosen.</summary>
    public bool HasSelectedTable => SelectedTable is not null;

    partial void OnSelectedTableChanged(DatabaseObject? value)
    {
        ClearGrid();
        NotifyCanUse();
        if (value is not null && IsTableMode) _ = PreviewTableAsync();
    }

    /// <summary>True when the list is empty because the database has no tables (or none match the search).</summary>
    public bool ShowNoTables => HasConnection && !IsBusy && Tables.Count == 0 && _loadedTables;

    private bool _loadedTables;

    private void ApplyTableFilter()
    {
        string f = TableFilter.Trim();
        var keep = SelectedTable;
        Tables.Clear();
        foreach (var t in _allTables)
            if (f.Length == 0 || t.QualifiedName.Contains(f, StringComparison.CurrentCultureIgnoreCase)) Tables.Add(t);
        if (keep is not null && Tables.Contains(keep)) SelectedTable = keep;
        OnPropertyChanged(nameof(ShowNoTables));
    }

    /// <summary>Reads the list of tables and views of the chosen connection.</summary>
    [RelayCommand]
    public async Task LoadTablesAsync()
    {
        if (SelectedConnection is null) { SetResult(Loc.Get("Data.Db.PickConnection"), true); return; }
        await ExecuteAsync(Loc.Get("Data.Db.Busy.Tables"), async ct =>
        {
            var source = await GetSourceAsync(ct);
            var list = await source.ListTablesAsync(ct);
            _allTables.Clear();
            _allTables.AddRange(list);
            _loadedTables = true;
            ApplyTableFilter();
            SetResult(list.Count == 0 ? Loc.Get("Data.Db.NoTables") : string.Empty, false);
        }, replace: true);
    }

    /// <summary>Reads the first rows of the chosen table so the user can see what is in it.</summary>
    public async Task PreviewTableAsync()
    {
        var table = SelectedTable;
        if (table is null) return;
        await ExecuteAsync(Loc.Get("Data.Db.Busy.Preview", table.QualifiedName), async ct =>
        {
            var source = await GetSourceAsync(ct);
            var result = await source.PreviewAsync(table, BrowseRows, ct);
            if (!ReferenceEquals(table, SelectedTable)) return; // the user already picked another table
            ShowGrid(result.Table);
            SetResult(string.Empty, false);
        }, replace: true);
    }

    // ----------------------------------------------------------------------------------- query

    /// <summary>The SELECT text.</summary>
    [ObservableProperty]
    private string _queryText = string.Empty;

    partial void OnQueryTextChanged(string value)
    {
        UpdateGuard();
        NotifyCanUse();
    }

    /// <summary>Why the guard refused the query, or empty when it is fine (or still empty).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGuardMessage), nameof(GuardOk))]
    private string _guardMessage = string.Empty;

    /// <summary>True when there is a refusal to show.</summary>
    public bool HasGuardMessage => GuardMessage.Length > 0;

    private bool _queryAllowed;

    /// <summary>True when the text is an accepted read-only query (shows the green tick).</summary>
    public bool GuardOk => _queryAllowed && GuardMessage.Length == 0;

    /// <summary>The <c>@parameters</c> of the query.</summary>
    public ObservableCollection<QueryParameterEditor> Parameters { get; } = new();

    /// <summary>True when the query has parameters.</summary>
    public bool HasParameters => Parameters.Count > 0;

    /// <summary>
    /// Checks the query against the dialect the selected connection really speaks (preset flavour, ODBC engine hint, pack provider).
    /// When the add-on of a pack connection is missing the strictest dialect (unknown ODBC engine) is used; running then fails with
    /// a clear "add-on not available" message from the engine layer.
    /// </summary>
    private SqlGuardResult CheckGuard()
    {
        if (SelectedConnection is null) return SqlGuard.Check(QueryText, DatabaseProvider.Odbc);
        try { return SqlGuard.Check(QueryText, SelectedConnection.Saved.Settings); }
        catch (DatabaseException) { return SqlGuard.Check(QueryText, DatabaseProvider.Odbc); }
    }

    private void UpdateGuard()
    {
        var verdict = CheckGuard();
        _queryAllowed = verdict.IsAllowed;
        // An empty box is not an error yet; the Run button just stays off.
        GuardMessage = verdict.IsAllowed || string.IsNullOrWhiteSpace(QueryText) ? string.Empty : DataMessages.DescribeReason(verdict.ReasonKey, verdict.Arg);
        OnPropertyChanged(nameof(GuardOk));
        if (verdict.IsAllowed) RebuildParameters(verdict.ParameterNames);
        else if (string.IsNullOrWhiteSpace(QueryText)) RebuildParameters(Array.Empty<string>());
    }

    private void RebuildParameters(IReadOnlyList<string> names)
    {
        if (Parameters.Select(p => p.Name).SequenceEqual(names, StringComparer.OrdinalIgnoreCase)) return;
        Parameters.Clear();
        foreach (string name in names)
        {
            var (value, type) = _paramMemory.TryGetValue(name, out var m) ? m : (string.Empty, "text");
            Parameters.Add(new QueryParameterEditor(name, value, type, OnParameterChanged));
        }
        OnPropertyChanged(nameof(HasParameters));
    }

    private void OnParameterChanged()
    {
        // Typed values are remembered by name, so a query that is briefly invalid while typing does not lose them.
        foreach (var p in Parameters) _paramMemory[p.Name] = (p.Value, p.Type);
    }

    /// <summary>Largest number of rows to read, as typed.</summary>
    [ObservableProperty]
    private string _maxRowsText = DatabaseSource.DefaultMaxRows.ToString(CultureInfo.InvariantCulture);

    private bool TryGetMaxRows(out int maxRows)
    {
        if (int.TryParse(MaxRowsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out maxRows) && maxRows is >= 1 and <= DatabaseSource.HardMaxRows) return true;
        SetResult(Loc.Get("Data.Db.MaxRows.Invalid", DatabaseSource.HardMaxRows.ToString("N0", CultureInfo.CurrentCulture)), true);
        return false;
    }

    private bool TryBuildParameters(out Dictionary<string, object?> values)
    {
        values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Parameters)
        {
            if (!p.TryGetValue(out var v, out string? error))
            {
                SetResult(error!, true);
                return false;
            }
            values[p.Name] = v;
        }
        return true;
    }

    private string Signature(string sql, int cap) =>
        string.Join("\u0001", new[] { SelectedConnection?.Name ?? string.Empty, sql, cap.ToString(CultureInfo.InvariantCulture) }
            .Concat(Parameters.Select(p => p.Name + "=" + p.Type + ":" + p.Value)));

    /// <summary>Runs the query (read-only, capped) and shows the rows.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RunQueryAsync()
    {
        if (!PrepareRun(out var parameters, out int cap)) return;
        string sql = QueryText;
        string signature = Signature(sql, cap);
        await ExecuteAsync(Loc.Get("Data.Db.Busy.Query"), async ct =>
        {
            var source = await GetSourceAsync(ct);
            var result = await source.QueryAsync(sql, parameters, cap, ct);
            _lastRun = (signature, result.Table);
            ShowGrid(result.Table);
            SetResult(string.Empty, false);
        });
        NotifyCanUse();
    }

    private bool CanRun() => HasConnection && IsQueryMode && _queryAllowed && !IsBusy;

    private bool PrepareRun(out Dictionary<string, object?> parameters, out int cap)
    {
        parameters = new Dictionary<string, object?>();
        cap = 0;
        if (SelectedConnection is null) { SetResult(Loc.Get("Data.Db.PickConnection"), true); return false; }
        // The guard runs here for a friendlier message and again inside the engine, which is the real gate.
        var verdict = CheckGuard();
        if (!verdict.IsAllowed)
        {
            SetResult(Loc.Get(DataKeys.QueryRejected, DataMessages.DescribeReason(verdict.ReasonKey, verdict.Arg)), true);
            return false;
        }
        return TryBuildParameters(out parameters) && TryGetMaxRows(out cap);
    }

    // ----------------------------------------------------------------------------------- results

    /// <summary>Column names of the grid (the rows the user is looking at).</summary>
    public IReadOnlyList<string> GridColumns { get; private set; } = Array.Empty<string>();

    /// <summary>Rows of the grid.</summary>
    public IReadOnlyList<string[]> GridRows { get; private set; } = Array.Empty<string[]>();

    /// <summary>True when the grid has columns to show.</summary>
    public bool HasGrid => GridColumns.Count > 0;

    /// <summary>"250 rows, 5 columns".</summary>
    public string GridSummary => HasGrid ? Loc.Get("Data.Db.Rows", GridRows.Count, GridColumns.Count) : string.Empty;

    /// <summary>Shown when the row cap cut the result off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTruncatedWarning))]
    private string _truncatedWarning = string.Empty;

    /// <summary>True when the result was cut at the row cap.</summary>
    public bool HasTruncatedWarning => TruncatedWarning.Length > 0;

    private MergeTable? _gridTable;

    private void ShowGrid(MergeTable table)
    {
        _gridTable = table;
        GridColumns = table.Columns;
        GridRows = table.Rows;
        // The engine adds a Data.Query.Truncated warning to the table when the cap cut the rows off.
        var truncated = table.Issues.FirstOrDefault(i => i.MessageKey == DataKeys.QueryTruncated);
        TruncatedWarning = truncated is null ? string.Empty : Loc.FormatIssue(truncated);
        RaiseGridChanged();
    }

    private void ClearGrid()
    {
        if (_gridTable is null && GridColumns.Count == 0) return;
        _gridTable = null;
        GridColumns = Array.Empty<string>();
        GridRows = Array.Empty<string[]>();
        TruncatedWarning = string.Empty;
        RaiseGridChanged();
    }

    private void RaiseGridChanged()
    {
        OnPropertyChanged(nameof(GridColumns));
        OnPropertyChanged(nameof(GridRows));
        OnPropertyChanged(nameof(HasGrid));
        OnPropertyChanged(nameof(GridSummary));
        GridChanged?.Invoke(this, EventArgs.Empty);
    }

    // ----------------------------------------------------------------------------------- use the data

    /// <summary>True when "Use this data" can run.</summary>
    public bool CanUse => HasConnection && !IsBusy && (IsTableMode ? HasSelectedTable : _queryAllowed);

    private void NotifyCanUse()
    {
        OnPropertyChanged(nameof(CanUse));
        OnPropertyChanged(nameof(ShowNoTables));
        RunQueryCommand.NotifyCanExecuteChanged();
        UseDataCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Takes the rows as the label's data. A table is read in full up to the row cap (the browse view only shows 100 rows);
    /// a query reuses the last run when nothing changed, otherwise runs again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUse))]
    public async Task UseDataAsync()
    {
        if (SelectedConnection is null) return;
        if (!TryGetMaxRows(out int cap)) return;
        string name = SelectedConnection.Name;

        if (IsTableMode)
        {
            var table = SelectedTable;
            if (table is null) return;
            await ExecuteAsync(Loc.Get("Data.Db.Busy.Reading", table.QualifiedName), async ct =>
            {
                var source = await GetSourceAsync(ct);
                // BuildSelectAll quotes the names for this engine; the text is also what the .rotula file keeps.
                string sql = source.BuildSelectAll(table);
                var result = await source.QueryAsync(sql, null, cap, ct);
                Publish(result.Table, new DatabaseMergeSettings { Reference = new DatabaseQueryReference(name, sql), MaxRows = cap });
            });
            return;
        }

        if (!PrepareRun(out var parameters, out _)) return;
        string query = QueryText;
        string signature = Signature(query, cap);
        var settings = new DatabaseMergeSettings { Reference = new DatabaseQueryReference(name, query), MaxRows = cap };
        foreach (var p in Parameters) settings.Parameters.Add(p.ToSetting());
        if (_lastRun is { } last && last.Signature == signature)
        {
            Publish(last.Table, settings);
            return;
        }
        await ExecuteAsync(Loc.Get("Data.Db.Busy.Query"), async ct =>
        {
            var source = await GetSourceAsync(ct);
            var result = await source.QueryAsync(query, parameters, cap, ct);
            _lastRun = (signature, result.Table);
            ShowGrid(result.Table);
            Publish(result.Table, settings);
        });
    }

    private void Publish(MergeTable table, DatabaseMergeSettings settings)
    {
        IsReferencePending = false;
        ReferenceMissing = false;
        SetResult(string.Empty, false);
        DataSelected?.Invoke(this, new DatabaseDataSelectedEventArgs(table, settings));
    }

    // ----------------------------------------------------------------------------------- label that refers to a connection

    private DatabaseMergeSettings? _pending;

    /// <summary>True when the open label refers to a saved connection and its data has not been read yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReferenceBanner), nameof(ReferenceQuery))]
    private bool _isReferencePending;

    /// <summary>True when the label's connection name is not among the saved connections.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReferenceMissingText), nameof(CanConnectReference))]
    private bool _referenceMissing;

    /// <summary>Name of the connection the label refers to.</summary>
    public string ReferenceName => _pending?.Reference.ConnectionName ?? string.Empty;

    /// <summary>The query text from the file, shown read-only before anything runs.</summary>
    public string ReferenceQuery => _pending?.Reference.Query ?? string.Empty;

    /// <summary>"This label uses the saved connection "X"."</summary>
    public string ReferenceBanner => Loc.Get("Data.Db.Reference.Banner", ReferenceName);

    /// <summary>Plain explanation of how to create the missing connection.</summary>
    public string ReferenceMissingText => Loc.Get("Data.Db.Reference.Missing", ReferenceName);

    /// <summary>True when the reload button can be used.</summary>
    public bool CanConnectReference => IsReferencePending && !IsBusy;

    /// <summary>
    /// Remembers the data source of an opened label without touching any database: the connection name and the query are
    /// shown to the user, and only the "Connect and reload data" button reads anything.
    /// </summary>
    public void Restore(DatabaseMergeSettings? settings)
    {
        CancelOperation();
        _restoring = true;
        try
        {
            _pending = settings;
            _referenceChecked = false;
            SelectedConnection = null;
            _source = null;
            _lastRun = null;
            _paramMemory.Clear();
            Parameters.Clear();
            ClearGrid();
            SetResult(string.Empty, false);
            ReferenceMissing = false;
            if (settings is null)
            {
                IsReferencePending = false;
                QueryText = string.Empty;
                MaxRowsText = DatabaseSource.DefaultMaxRows.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                foreach (var p in settings.Parameters) _paramMemory[p.Name] = (p.Value, p.Type);
                MaxRowsText = settings.MaxRows.ToString(CultureInfo.InvariantCulture);
                ModeIndex = 1;
                QueryText = settings.Reference.Query;
                IsReferencePending = true;
            }
        }
        finally { _restoring = false; }
        // The text may be unchanged (so no change event fired); the guard and the parameter boxes must still reflect the saved values.
        UpdateGuard();
        NotifyCanUse();
        OnPropertyChanged(nameof(ReferenceName));
        OnPropertyChanged(nameof(ReferenceQuery));
        OnPropertyChanged(nameof(ReferenceBanner));
        UpdateReferenceState();
    }

    /// <summary>Forgets the remembered query reference (a file replaced it as the label's data).</summary>
    public void DiscardReference()
    {
        _pending = null;
        IsReferencePending = false;
        ReferenceMissing = false;
        OnPropertyChanged(nameof(ReferenceName));
        OnPropertyChanged(nameof(ReferenceQuery));
    }

    private void UpdateReferenceState()
    {
        if (_pending is null || !IsReferencePending) { ReferenceMissing = false; return; }
        // Only a name lookup in the local list, and only once the list was read: no connection is opened.
        ReferenceMissing = _referenceChecked && !Connections.Any(c => string.Equals(c.Name, ReferenceName, StringComparison.OrdinalIgnoreCase));
    }

    private bool _referenceChecked;

    /// <summary>
    /// The user clicked "Connect and reload data": find the saved connection by name, run the query from the file (after the
    /// read-only guard) and use the rows.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnectReference))]
    public async Task ConnectReferenceAsync()
    {
        var reference = _pending;
        if (reference is null) return;
        await RefreshConnectionsAsync();
        var choice = Connections.FirstOrDefault(c => string.Equals(c.Name, reference.Reference.ConnectionName, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            ReferenceMissing = true;
            return;
        }
        ReferenceMissing = false;
        _restoring = true;
        try { SelectedConnection = choice; }
        finally { _restoring = false; }
        // The query text comes from a file and is untrusted: the guard decides before anything is sent to the server.
        QueryText = reference.Reference.Query;
        ModeIndex = 1;
        await UseDataAsync();
    }

    // ----------------------------------------------------------------------------------- running work

    /// <summary>True while a database operation runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanConnectReference), nameof(CanUse))]
    private bool _isBusy;

    /// <summary>The opposite of <see cref="IsBusy"/>.</summary>
    public bool IsIdle => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCanUse();
        ConnectReferenceCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the running operation is doing.</summary>
    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>The last outcome in plain language (an error, or empty).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(HasError), nameof(HasInfo))]
    private string _resultText = string.Empty;

    /// <summary>True when <see cref="ResultText"/> is an error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(HasInfo))]
    private bool _resultIsError;

    /// <summary>The database's own message for the last error (already free of secrets), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    private string _errorDetail = string.Empty;

    /// <summary>True when there is a technical detail to show.</summary>
    public bool HasErrorDetail => ErrorDetail.Length > 0;

    /// <summary>True when there is a result line.</summary>
    public bool HasResult => ResultText.Length > 0;

    /// <summary>True when the result line is an error.</summary>
    public bool HasError => HasResult && ResultIsError;

    /// <summary>True when the result line is a calm note (stopped, no tables).</summary>
    public bool HasInfo => HasResult && !ResultIsError;

    private void SetResult(string text, bool isError)
    {
        ResultText = text;
        ResultIsError = isError;
        ErrorDetail = string.Empty;
    }

    private void SetError(DatabaseException ex)
    {
        SetResult(DataMessages.Describe(ex), true);
        ErrorDetail = ex.Detail ?? string.Empty;
    }

    /// <summary>Stops the running operation. Also used when the window closes.</summary>
    [RelayCommand]
    public void CancelOperation()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* the operation just finished */ }
    }

    private async Task<IDatabaseSource> GetSourceAsync(CancellationToken ct)
    {
        if (_source is not null) return _source;
        if (SelectedConnection is null) throw new DatabaseException(DataKeys.ConnectionNotFound, null, new[] { string.Empty });
        // OpenAsync unprotects the saved password inside the store; this class never sees it.
        var source = await _db.Connections.OpenAsync(SelectedConnection.Name, ct);
        _source = source;
        return source;
    }

    /// <summary>
    /// Runs one database operation: busy text, cancellation, and every failure shown as a plain sentence. With
    /// <paramref name="replace"/> a newer operation (the user clicked another table) stops the running one instead of being ignored.
    /// </summary>
    private async Task<bool> ExecuteAsync(string busyText, Func<CancellationToken, Task> work, bool replace = false)
    {
        if (IsBusy)
        {
            if (!replace) return false;
            CancelOperation();
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        int id = ++_operationId;
        IsBusy = true;
        BusyText = busyText;
        SetResult(string.Empty, false);
        try
        {
            await work(cts.Token);
            return !cts.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            if (id == _operationId) SetResult(Loc.Get("Data.Error.Cancelled"), false);
            return false;
        }
        catch (DatabaseException ex)
        {
            // A replaced operation is silent; a stopped one is a calm note, not a red error.
            if (id == _operationId) { if (ex.IsCancellation) SetResult(Loc.Get("Data.Error.Cancelled"), false); else SetError(ex); }
            return false;
        }
        finally
        {
            if (id == _operationId)
            {
                IsBusy = false;
                BusyText = string.Empty;
                _cts = null;
            }
            cts.Dispose();
        }
    }

    private void RefreshLanguage()
    {
        foreach (var c in QueryParameterEditor.TypeChoices) c.Refresh();
        foreach (var p in Parameters) p.RefreshLanguage();
        UpdateGuard();
        OnPropertyChanged(nameof(ReferenceBanner));
        OnPropertyChanged(nameof(ReferenceMissingText));
        OnPropertyChanged(nameof(GridSummary));
        foreach (var c in Connections) c.Refresh();
    }
}
