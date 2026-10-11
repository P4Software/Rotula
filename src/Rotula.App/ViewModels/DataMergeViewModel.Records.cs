using System.Collections;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.Core.Merge;
using Rotula.Core.Variables;

namespace Rotula.App.ViewModels;

/// <summary>
/// One data row in the records grid of the Data window. It is a thin handle: the cells are the table's own strings and the ticked state
/// lives in the owning view-model, so 100,000 of these cost little memory and a tick never copies data.
/// </summary>
public sealed class RecordRow : ObservableObject
{
    private readonly DataMergeViewModel _owner;
    private readonly string[] _cells;

    internal RecordRow(DataMergeViewModel owner, int index, string[] cells)
    {
        _owner = owner;
        Index = index;
        _cells = cells;
    }

    /// <summary>Zero-based position of the row in the loaded data (its original order, which never changes).</summary>
    public int Index { get; }

    /// <summary>1-based row number shown in the grid.</summary>
    public int Number => Index + 1;

    /// <summary>The text of column <paramref name="column"/> (zero-based). Bound by index so column names with dots or brackets cannot break a binding.</summary>
    public string this[int column] => column >= 0 && column < _cells.Length ? _cells[column] : string.Empty;

    /// <summary>True when this record is ticked and so will be printed, checked or exported.</summary>
    public bool IsSelected
    {
        get => _owner.IsRecordSelected(Index);
        set => _owner.SetRecordSelected(Index, value);
    }

    /// <summary>Accessible name of the row's checkbox, for example "Select record 12".</summary>
    public string SelectName => Loc.Get("Data.Records.SelectRow", Number);

    internal void RaiseSelectionChanged() => OnPropertyChanged(nameof(IsSelected));

    internal void RefreshLanguage() => OnPropertyChanged(nameof(SelectName));
}

/// <summary>An entry of the filter's column drop-down: "All columns" (index -1) or one data column.</summary>
public sealed class FilterColumnChoice : ObservableObject
{
    private readonly string? _column;

    internal FilterColumnChoice(int index, string? column)
    {
        Index = index;
        _column = column;
    }

    /// <summary>Zero-based column to search, or -1 for every column.</summary>
    public int Index { get; }

    /// <summary>Display name in the current language.</summary>
    public string Name => Index < 0 ? Loc.Get("Data.Filter.AllColumns") : _column ?? string.Empty;

    internal void Refresh() => OnPropertyChanged(nameof(Name));

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// The records list of the Data window: every loaded row with a tick box, a filter, sorting and "select all / none / invert".
/// Only ticked records are printed, checked or exported; they are processed in the original data order (not the order the grid is sorted in),
/// so serial numbers and job splits never depend on how the list looks. The selection lives only in memory and is never saved in the .rotula file.
/// </summary>
public sealed partial class DataMergeViewModel
{
    /// <summary>How long typing in the filter box waits before the list is filtered (so every key press does not start a search of 100,000 rows).</summary>
    public static TimeSpan FilterDebounce { get; set; } = TimeSpan.FromMilliseconds(250);

    private RecordRow[] _rows = Array.Empty<RecordRow>();
    private bool[] _checked = Array.Empty<bool>();
    private int[]? _selectedCache;
    private int _shownSelected;
    private bool _resettingRecords;
    private int _viewVersion;
    private CancellationTokenSource? _viewCts;
    private CancellationTokenSource? _debounceCts;
    private Task _viewTask = Task.CompletedTask;
    private int _selectedCount;
    private IReadOnlyList<RecordRow> _visibleRows = Array.Empty<RecordRow>();
    private string _filterText = string.Empty;
    private bool _showOnlySelected;
    private FilterColumnChoice? _selectedFilterColumn;
    private int _sortColumn = -1;
    private bool _sortDescending;

    // ----------------------------------------------------------------------------------- the list

    /// <summary>The rows the grid shows: after the filter, "only selected" and the sort. Replaced as a whole (never edited) so a 100,000 row list is cheap to swap.</summary>
    public IReadOnlyList<RecordRow> VisibleRows
    {
        get => _visibleRows;
        private set
        {
            _visibleRows = value;
            OnPropertyChanged();
            RecountShown();
            NotifyShown();
            // The grid clears its highlighted row when its rows are replaced; this puts the highlight back if that record is still shown.
            OnPropertyChanged(nameof(CurrentRow));
        }
    }

    /// <summary>Number of rows in the list, shown or not.</summary>
    public int TotalRows => _rows.Length;

    /// <summary>Number of ticked records.</summary>
    public int SelectedCount => _selectedCount;

    /// <summary>The zero-based rows that are ticked, in data order. This is exactly what a batch processes.</summary>
    public IReadOnlyList<int> SelectedIndexes => SelectedArray();

    private int[] SelectedArray()
    {
        if (_selectedCache is { } cached) return cached;
        var result = new int[_selectedCount];
        int k = 0;
        for (int i = 0; i < _checked.Length; i++)
            if (_checked[i]) result[k++] = i;
        return _selectedCache = result;
    }

    /// <summary>True when the rows listed are narrowed by a filter or by "only selected" (the buttons then say "shown").</summary>
    public bool IsFiltered => _filterText.Trim().Length > 0 || _showOnlySelected;

    /// <summary>True when data is loaded but the filter hides every row.</summary>
    public bool ShowNoMatches => _rows.Length > 0 && _visibleRows.Count == 0;

    /// <summary>The record on the canvas, as a grid row (null when it is not loaded). Setting it from the grid shows that record on the canvas.</summary>
    public RecordRow? CurrentRow
    {
        get => CurrentIndex >= 0 && CurrentIndex < _rows.Length ? _rows[CurrentIndex] : null;
        set { if (value is not null && value.Index != CurrentIndex) CurrentIndex = value.Index; }
    }

    internal bool IsRecordSelected(int index) => index >= 0 && index < _checked.Length && _checked[index];

    /// <summary>Called when the data is replaced or cleared: every record starts ticked, and the filter and sort start over.</summary>
    private void ResetRecords(MergeTable? table)
    {
        _resettingRecords = true;
        try
        {
            CancelViewWork();
            int n = table?.RowCount ?? 0;
            _rows = new RecordRow[n];
            for (int i = 0; i < n; i++) _rows[i] = new RecordRow(this, i, table!.Rows[i]);
            _checked = new bool[n];
            Array.Fill(_checked, true);
            _selectedCache = null;
            SetProperty(ref _selectedCount, n, nameof(SelectedCount));
            _filterText = string.Empty;
            _showOnlySelected = false;
            _sortColumn = -1;
            _sortDescending = false;
            FilterColumns.Clear();
            FilterColumns.Add(new FilterColumnChoice(-1, null));
            if (table is not null)
                for (int c = 0; c < table.Columns.Count; c++) FilterColumns.Add(new FilterColumnChoice(c, table.Columns[c]));
            _selectedFilterColumn = FilterColumns[0];
            OnPropertyChanged(nameof(FilterText));
            OnPropertyChanged(nameof(ShowOnlySelected));
            OnPropertyChanged(nameof(SelectedFilterColumn));
            OnPropertyChanged(nameof(SortColumn));
            OnPropertyChanged(nameof(SortDescending));
            OnPropertyChanged(nameof(TotalRows));
            VisibleRows = _rows; // nothing is filtered, so the grid shows the rows as they are
        }
        finally { _resettingRecords = false; }
        OnSelectionChanged(rebind: false);
    }

    // ----------------------------------------------------------------------------------- filter

    /// <summary>The columns the filter can search: "All columns" first, then each data column.</summary>
    public System.Collections.ObjectModel.ObservableCollection<FilterColumnChoice> FilterColumns { get; } = new();

    /// <summary>The column to search, or "All columns".</summary>
    public FilterColumnChoice? SelectedFilterColumn
    {
        get => _selectedFilterColumn;
        set
        {
            if (!SetProperty(ref _selectedFilterColumn, value) || _resettingRecords) return;
            _ = RefreshViewAsync();
        }
    }

    /// <summary>
    /// Text to look for (case-insensitive, anywhere in the cell, plain text: no wildcards). Leading and trailing spaces are ignored.
    /// The list updates a moment after typing stops.
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value ?? string.Empty) || _resettingRecords) return;
            OnPropertyChanged(nameof(IsFiltered));
            _ = ScheduleViewRefreshAsync();
        }
    }

    /// <summary>Shows only the ticked records, so the operator can review what will print.</summary>
    public bool ShowOnlySelected
    {
        get => _showOnlySelected;
        set
        {
            if (!SetProperty(ref _showOnlySelected, value) || _resettingRecords) return;
            OnPropertyChanged(nameof(IsFiltered));
            _ = RefreshViewAsync();
        }
    }

    /// <summary>Empties the filter box and turns "only selected" off.</summary>
    [RelayCommand]
    private void ClearFilter()
    {
        _filterText = string.Empty;
        _showOnlySelected = false;
        _selectedFilterColumn = FilterColumns.FirstOrDefault();
        OnPropertyChanged(nameof(FilterText));
        OnPropertyChanged(nameof(ShowOnlySelected));
        OnPropertyChanged(nameof(SelectedFilterColumn));
        OnPropertyChanged(nameof(IsFiltered));
        _ = RefreshViewAsync();
    }

    private async Task ScheduleViewRefreshAsync()
    {
        _debounceCts?.Cancel();
        var cts = _debounceCts = new CancellationTokenSource();
        try { await Task.Delay(FilterDebounce, cts.Token); }
        catch (OperationCanceledException) { return; } // typing went on: the newer key press owns the refresh
        await RefreshViewAsync();
    }

    // ----------------------------------------------------------------------------------- sort

    /// <summary>Zero-based data column the list is sorted by, or -1 for the original order.</summary>
    public int SortColumn
    {
        get => _sortColumn;
        private set => SetProperty(ref _sortColumn, value);
    }

    /// <summary>True when the sort is descending.</summary>
    public bool SortDescending
    {
        get => _sortDescending;
        private set => SetProperty(ref _sortDescending, value);
    }

    /// <summary>
    /// Sorts by a column (a click on its header): first click ascending, second descending, third back to the original order. The sort is stable,
    /// numbers sort as numbers when the whole column is numeric, and it never changes which records are ticked.
    /// </summary>
    public Task SortByAsync(int column)
    {
        if (column < 0 || column >= (Table?.Columns.Count ?? 0)) return Task.CompletedTask;
        if (_sortColumn != column) { SortColumn = column; SortDescending = false; }
        else if (!_sortDescending) SortDescending = true;
        else { SortColumn = -1; SortDescending = false; }
        return RefreshViewAsync();
    }

    // ----------------------------------------------------------------------------------- computing the shown rows

    /// <summary>
    /// Works out the shown rows now (cancelling a typing delay that is still waiting). The search and sort run on a background thread, so the
    /// window stays responsive with 100,000 rows. Awaiting the returned task is enough to see the result.
    /// </summary>
    public Task RefreshViewAsync()
    {
        _debounceCts?.Cancel();
        CancelViewWork();
        if (Table is null) { _viewTask = Task.CompletedTask; return _viewTask; }

        string needle = _filterText.Trim();
        int column = _selectedFilterColumn?.Index ?? -1;
        bool only = _showOnlySelected;
        int sortColumn = _sortColumn;
        bool descending = _sortDescending;
        if (needle.Length == 0 && !only && sortColumn < 0)
        {
            VisibleRows = _rows;
            _viewTask = Task.CompletedTask;
            return _viewTask;
        }

        // Snapshots: the background search must not see ticks made while it runs.
        bool[]? ticked = only ? (bool[])_checked.Clone() : null;
        var rows = _rows;
        var cells = Table.Rows;
        var cts = _viewCts = new CancellationTokenSource();
        int version = ++_viewVersion;
        _viewTask = ApplyViewAsync(version, rows, cells, ticked, needle, column, sortColumn, descending, cts.Token);
        return _viewTask;
    }

    private void CancelViewWork()
    {
        _viewCts?.Cancel();
        _viewVersion++; // an answer that is still on its way is stale from now on
    }

    private async Task ApplyViewAsync(int version, RecordRow[] rows, IReadOnlyList<string[]> cells, bool[]? ticked, string needle, int column,
        int sortColumn, bool descending, CancellationToken token)
    {
        int[] shown;
        try { shown = await Task.Run(() => ComputeShown(cells, ticked, needle, column, sortColumn, descending, token), token); }
        catch (OperationCanceledException) { return; }
        if (version != _viewVersion) return;
        var list = new RecordRow[shown.Length];
        for (int i = 0; i < shown.Length; i++) list[i] = rows[shown[i]];
        VisibleRows = list;
    }

    private static int[] ComputeShown(IReadOnlyList<string[]> cells, bool[]? ticked, string needle, int column, int sortColumn, bool descending, CancellationToken token)
    {
        int n = cells.Count;
        var buffer = new int[n];
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            if ((i & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            if (ticked is not null && !ticked[i]) continue;
            if (needle.Length > 0 && !RowMatches(cells[i], column, needle)) continue;
            buffer[count++] = i;
        }
        var shown = count == n ? buffer : buffer.AsSpan(0, count).ToArray();
        if (sortColumn >= 0) SortRows(shown, cells, sortColumn, descending, token);
        return shown;
    }

    private static bool RowMatches(string[] row, int column, string needle)
    {
        if (column >= 0) return column < row.Length && row[column].Contains(needle, StringComparison.OrdinalIgnoreCase);
        foreach (var cell in row)
            if (cell.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Sorts row numbers by one column. Ties keep the original order (the row number is the last key), which makes the sort stable
    /// and the descending order the exact reverse of the ascending one for different values.
    /// </summary>
    private static void SortRows(int[] shown, IReadOnlyList<string[]> cells, int column, bool descending, CancellationToken token)
    {
        // Parse every cell once: comparing 100,000 rows would otherwise parse each cell many times.
        var numbers = new double[cells.Count];
        var empty = new bool[cells.Count];
        bool numeric = true;
        foreach (int i in shown)
        {
            string text = column < cells[i].Length ? cells[i][column].Trim() : string.Empty;
            if (text.Length == 0) { empty[i] = true; continue; }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])) { numeric = false; break; }
        }
        token.ThrowIfCancellationRequested();
        var culture = CultureInfo.CurrentCulture.CompareInfo;
        int Compare(int a, int b)
        {
            int c;
            if (numeric)
                c = empty[a] ? (empty[b] ? 0 : -1) : empty[b] ? 1 : numbers[a].CompareTo(numbers[b]);
            else
                c = culture.Compare(cells[a][column], cells[b][column], CompareOptions.IgnoreCase);
            if (c != 0) return descending ? -c : c;
            return a.CompareTo(b);
        }
        Array.Sort(shown, Compare);
    }

    // ----------------------------------------------------------------------------------- ticking

    /// <summary>Ticks or unticks one record (zero-based row).</summary>
    public void SetRecordSelected(int index, bool value)
    {
        if (index < 0 || index >= _checked.Length || _checked[index] == value) return;
        _checked[index] = value;
        _selectedCount += value ? 1 : -1;
        _rows[index].RaiseSelectionChanged();
        OnSelectionChanged();
    }

    /// <summary>Ticks every shown record. With a filter active only the shown records change; hidden ones keep their state.</summary>
    [RelayCommand]
    public void SelectAllShown() => ChangeShown(_ => true);

    /// <summary>Unticks every shown record (with a filter active, only the shown ones).</summary>
    [RelayCommand]
    public void SelectNoneShown() => ChangeShown(_ => false);

    /// <summary>Flips the tick of every shown record (with a filter active, only the shown ones).</summary>
    [RelayCommand]
    public void InvertShown() => ChangeShown(current => !current);

    /// <summary>
    /// Sets the tick of every shown row between two positions of the list (both included, in the order shown). This is the Shift+click range.
    /// </summary>
    public void SetRange(int fromPosition, int toPosition, bool value)
    {
        int lo = Math.Max(0, Math.Min(fromPosition, toPosition));
        int hi = Math.Min(_visibleRows.Count - 1, Math.Max(fromPosition, toPosition));
        if (lo > hi) return;
        bool any = false;
        for (int p = lo; p <= hi; p++) any |= Apply(_visibleRows[p].Index, value);
        if (any) OnSelectionChanged();
    }

    private void ChangeShown(Func<bool, bool> map)
    {
        if (_rows.Length == 0) return;
        bool any = false;
        foreach (var row in _visibleRows) any |= Apply(row.Index, map(_checked[row.Index]));
        if (any) OnSelectionChanged();
    }

    private bool Apply(int index, bool value)
    {
        if (_checked[index] == value) return false;
        _checked[index] = value;
        _selectedCount += value ? 1 : -1;
        _rows[index].RaiseSelectionChanged();
        return true;
    }

    /// <summary>The tick state of the shown rows for the header box: true all, false none, null some (the box shows a dash).</summary>
    public bool? ShownSelectionState
    {
        get
        {
            if (_visibleRows.Count == 0 || _shownSelected == 0) return false;
            return _shownSelected == _visibleRows.Count ? true : null;
        }
        set
        {
            if (value == true) SelectAllShown();
            else if (value == false) SelectNoneShown();
        }
    }

    private void RecountShown()
    {
        int k = 0;
        foreach (var row in _visibleRows) if (_checked[row.Index]) k++;
        _shownSelected = k;
    }

    private void OnSelectionChanged(bool rebind = true)
    {
        _selectedCache = null;
        RecountShown();
        OnPropertyChanged(nameof(SelectedCount));
        NotifyShown();
        OnPropertyChanged(nameof(PrintAllText));
        OnPropertyChanged(nameof(BatchBlockedText));
        OnPropertyChanged(nameof(HasBatchBlockedText));
        UpdateNavigator();
        if (_resettingRecords) return;
        if (_showOnlySelected) _ = RefreshViewAsync();
        // A serial number follows the position among the ticked records, so a counter in the label changes on the canvas when ticks change.
        if (rebind && Table is not null && (Counters.Count > 0 || VariableEngine.HasDefinitions(_document())))
        {
            Rebind();
            RaisePreviewChanged();
        }
    }

    private void NotifyShown()
    {
        OnPropertyChanged(nameof(ShownSelectionState));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(ShownSummary));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(SelectAllText));
        OnPropertyChanged(nameof(SelectNoneText));
        OnPropertyChanged(nameof(InvertText));
    }

    // ----------------------------------------------------------------------------------- texts

    /// <summary>"37 of 120 selected": always visible.</summary>
    public string SelectionSummary => Loc.Get("Data.Records.Summary", _selectedCount, _rows.Length);

    /// <summary>"Showing 12 of 120 records" while a filter is active, otherwise empty.</summary>
    public string ShownSummary => IsFiltered ? Loc.Get("Data.Records.Shown", _visibleRows.Count, _rows.Length) : string.Empty;

    /// <summary>Text of the "select all" button; says how many rows it reaches while a filter is active.</summary>
    public string SelectAllText => IsFiltered ? Loc.Get("Data.Records.SelectAllShown", _visibleRows.Count) : Loc.Get("Data.Records.SelectAll");

    /// <summary>Text of the "select none" button.</summary>
    public string SelectNoneText => IsFiltered ? Loc.Get("Data.Records.SelectNoneShown", _visibleRows.Count) : Loc.Get("Data.Records.SelectNone");

    /// <summary>Text of the "invert" button.</summary>
    public string InvertText => IsFiltered ? Loc.Get("Data.Records.InvertShown", _visibleRows.Count) : Loc.Get("Data.Records.Invert");

    /// <summary>Text of the print button, for example "Print 37 selected labels". It is also the button's accessible name.</summary>
    public string PrintAllText
    {
        get
        {
            int n = _selectedCount;
            if (n == 1) return Loc.Get("Data.Batch.PrintSelected.One");
            if (n > 1 && n == _rows.Length) return Loc.Get("Data.Batch.PrintEvery", n);
            return Loc.Get("Data.Batch.PrintSelected", n);
        }
    }

    /// <summary>Why the print, check and export buttons are disabled, or empty when they are not.</summary>
    public string BatchBlockedText => HasRecords && _selectedCount == 0 ? Loc.Get("Data.Batch.NoneSelected") : string.Empty;

    /// <summary>True when <see cref="BatchBlockedText"/> has something to say.</summary>
    public bool HasBatchBlockedText => BatchBlockedText.Length > 0;

    // ----------------------------------------------------------------------------------- stepping through the ticked records

    /// <summary>First ticked record after <paramref name="index"/>, or -1.</summary>
    private int NextSelected(int index)
    {
        var sel = SelectedArray();
        int p = Array.BinarySearch(sel, index);
        int at = p >= 0 ? p + 1 : ~p;
        return at < sel.Length ? sel[at] : -1;
    }

    /// <summary>Last ticked record before <paramref name="index"/>, or -1.</summary>
    private int PreviousSelected(int index)
    {
        var sel = SelectedArray();
        int p = Array.BinarySearch(sel, index);
        int at = (p >= 0 ? p : ~p) - 1;
        return at >= 0 ? sel[at] : -1;
    }

    private bool CanGoBack() => _selectedCount > 0 && SelectedArray()[0] != CurrentIndex;
    private bool CanGoForward() => _selectedCount > 0 && SelectedArray()[^1] != CurrentIndex;
    private bool CanStepBack() => _selectedCount > 0 && PreviousSelected(CurrentIndex) >= 0;
    private bool CanStepForward() => _selectedCount > 0 && NextSelected(CurrentIndex) >= 0;

    /// <summary>Shows the first ticked record. Does nothing when none is ticked.</summary>
    public void MoveFirst() { if (_selectedCount > 0) CurrentIndex = SelectedArray()[0]; }

    /// <summary>Shows the last ticked record. Does nothing when none is ticked.</summary>
    public void MoveLast() { if (_selectedCount > 0) CurrentIndex = SelectedArray()[^1]; }

    /// <summary>Shows the previous ticked record (data order). Does nothing at the first one.</summary>
    public void MovePrevious() { if (PreviousSelected(CurrentIndex) is var p and >= 0) CurrentIndex = p; }

    /// <summary>Shows the next ticked record (data order). Does nothing at the last one.</summary>
    public void MoveNext() { if (NextSelected(CurrentIndex) is var n and >= 0) CurrentIndex = n; }

    /// <summary>
    /// "Record 3 of 120" while every record is ticked (the same text as before selection existed). With a partial selection it counts the ticked records:
    /// "Selected 3 of 37 (row 12)", or "Row 12 (not selected)" when the shown record is not ticked.
    /// </summary>
    private string BuildRecordText()
    {
        if (RecordCount == 0) return Loc.Get("Data.Record.None");
        if (_selectedCount == RecordCount) return Loc.Get("Data.Record.Of", CurrentIndex + 1, RecordCount);
        if (_selectedCount == 0) return Loc.Get("Data.Record.NoneSelected", CurrentIndex + 1, RecordCount);
        if (IsRecordSelected(CurrentIndex))
        {
            int rank = Array.BinarySearch(SelectedArray(), CurrentIndex) + 1;
            return Loc.Get("Data.Record.OfSelected", rank, _selectedCount, CurrentIndex + 1);
        }
        return Loc.Get("Data.Record.NotSelected", CurrentIndex + 1, _selectedCount);
    }

    // ----------------------------------------------------------------------------------- counting labels

    /// <summary>
    /// How many labels the batch produces counting copies, for the confirmation: the copies column summed over the ticked records, the fixed
    /// number times the records, or the label's own print quantity times the records.
    /// </summary>
    private long CountLabels(MergeDataSet ds, int labelQuantity)
    {
        long records = ds.BatchCount;
        switch (CopiesSource)
        {
            case CopiesSource.Fixed:
                return records * (ds.Options.CopiesPerRecord ?? 1);
            case CopiesSource.Column:
                int column = ds.Table.IndexOfColumn(ds.Options.CopiesColumn ?? string.Empty);
                if (column < 0) return records;
                long total = 0;
                for (int p = 0; p < ds.BatchCount; p++)
                    if (int.TryParse(ds.Table.Rows[ds.RecordAt(p)][column].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int copies)) total += copies;
                return total;
            default:
                return records * Math.Max(1, labelQuantity);
        }
    }

    private void RefreshRecordsLanguage()
    {
        foreach (var c in FilterColumns) c.Refresh();
        foreach (var row in _visibleRows.Take(2000)) row.RefreshLanguage(); // rows that are off screen read the name again when they are drawn
        NotifyShown();
        OnPropertyChanged(nameof(PrintAllText));
        OnPropertyChanged(nameof(BatchBlockedText));
        OnPropertyChanged(nameof(HasBatchBlockedText));
    }

    /// <summary>The position of <paramref name="row"/> in the shown list, or -1. Used by the Shift+click range.</summary>
    public int PositionInView(RecordRow row) => _visibleRows is IList list ? list.IndexOf(row) : -1;
}
