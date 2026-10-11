using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Rotula.App.Localization;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>
/// The records list of the Data window: a tick box, the row number and every data column of the loaded data, with a filter above it.
/// The grid is virtualised (only the rows on screen exist), so 100,000 records scroll as smoothly as ten. Its DataContext is the
/// <see cref="DataMergeViewModel"/>; all behaviour lives there, this class builds the columns and maps keys and clicks to it.
/// </summary>
public partial class DataRecordsView : UserControl
{
    private DataMergeViewModel? _vm;
    private int _anchor = -1; // position in the list of the last row that was ticked by hand: the start of a Shift+click range

    /// <summary>Creates the view.</summary>
    public DataRecordsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as DataMergeViewModel);
        Loaded += (_, _) =>
        {
            if (RecordsGrid.Columns.Count == 0) BuildColumns();
            Loc.Instance.LanguageChanged += OnLanguageChanged; // column headers are built in code, so they are rebuilt when the language changes
        };
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => BuildColumns();

    /// <summary>The grid, for tests that look at its rows.</summary>
    public DataGrid Grid => RecordsGrid;

    private void Attach(DataMergeViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.TableChanged -= OnTableChanged;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = vm;
        if (vm is null) return;
        vm.TableChanged += OnTableChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        BuildColumns();
    }

    private void OnTableChanged(object? sender, EventArgs e)
    {
        // The view-model may raise its events from another thread (a background load); WPF elements may only be touched on their own.
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnTableChanged(sender, e)); return; }
        _anchor = -1;
        BuildColumns();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnViewModelPropertyChanged(sender, e)); return; }
        switch (e.PropertyName)
        {
            case nameof(DataMergeViewModel.SortColumn):
            case nameof(DataMergeViewModel.SortDescending):
                ShowSortArrow();
                break;
            case nameof(DataMergeViewModel.CurrentRow):
                // Stepping through records with the navigator moves the highlight; keep it on screen.
                if (_vm?.CurrentRow is { } row && RecordsGrid.Items.Contains(row)) RecordsGrid.ScrollIntoView(row);
                break;
            case nameof(DataMergeViewModel.VisibleRows):
                _anchor = -1; // positions mean something else in the new list
                break;
        }
    }

    /// <summary>
    /// Creates the tick column, the row-number column and one column per data column. Built in code because the number and names come from
    /// the file; the cells bind by index (<c>[i]</c>) so a column name with dots or brackets can never break a binding path.
    /// </summary>
    private void BuildColumns()
    {
        RecordsGrid.Columns.Clear();
        if (_vm is null) return;

        // Column headers do not inherit the DataContext, so the header box is bound to the view-model explicitly.
        var header = new CheckBox { VerticalAlignment = VerticalAlignment.Center, IsThreeState = false };
        header.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(DataMergeViewModel.ShownSelectionState)) { Source = _vm, Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(header, Loc.Get("Data.Records.SelectColumn"));
        header.ToolTip = Loc.Get("Data.Records.SelectColumn");

        var tick = new FrameworkElementFactory(typeof(CheckBox));
        tick.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(RecordRow.IsSelected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        tick.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(RecordRow.SelectName)));
        tick.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        tick.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        // The box is not a tab stop: the keyboard works on the row (Space ticks it), the mouse and screen readers use the box itself.
        tick.SetValue(FocusableProperty, false);
        tick.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnTickClick));
        RecordsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = header,
            CellTemplate = new DataTemplate { VisualTree = tick },
            Width = new DataGridLength(52),
            CanUserSort = false,
            CanUserResize = false,
        });

        // The header is an element (not plain text) so a screen reader hears "Row number" rather than "#".
        var numberHeader = new TextBlock { Text = Loc.Get("Data.Records.Number") };
        AutomationProperties.SetName(numberHeader, Loc.Get("Data.Records.NumberName"));
        RecordsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = numberHeader,
            Binding = new Binding(nameof(RecordRow.Number)) { Mode = BindingMode.OneWay },
            Width = new DataGridLength(64),
            CanUserSort = false,
        });

        var names = _vm.PreviewColumns;
        for (int i = 0; i < names.Count; i++)
        {
            RecordsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = names[i],
                Binding = new Binding($"[{i}]") { Mode = BindingMode.OneWay },
                SortMemberPath = i.ToString(CultureInfo.InvariantCulture), // read back in OnGridSorting; the grid itself never sorts
                MinWidth = 90,
            });
        }
        // The last column takes the space that is left, so the table always fills its frame instead of ending in an empty strip.
        if (RecordsGrid.Columns.Count > 2) RecordsGrid.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        ShowSortArrow();
    }

    private void ShowSortArrow()
    {
        foreach (var column in RecordsGrid.Columns)
        {
            bool isData = int.TryParse(column.SortMemberPath, NumberStyles.None, CultureInfo.InvariantCulture, out int index);
            column.SortDirection = isData && _vm is { SortColumn: var s } && s == index
                ? (_vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    /// <summary>A click on a column header: the view-model sorts (off the UI thread, stably, keeping the ticks); the grid's own sort is not used.</summary>
    private void OnGridSorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_vm is null || !int.TryParse(e.Column.SortMemberPath, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return;
        _ = _vm.SortByAsync(index);
    }

    /// <summary>
    /// A click on a row's box. With Shift held, every row between the last ticked one and this one gets the same state as this click
    /// (the usual mail-list behaviour). The record also becomes the one shown on the canvas.
    /// </summary>
    private void OnTickClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { DataContext: RecordRow row }) return;
        int position = _vm.PositionInView(row);
        if (position < 0) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor >= 0 && _anchor != position)
            _vm.SetRange(_anchor, position, row.IsSelected);
        _anchor = position;
        _vm.CurrentRow = row;
    }

    /// <summary>Space ticks the highlighted row; Ctrl+A ticks every shown row (instead of the grid's own "select all rows").</summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.SelectAllShown();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) && RecordsGrid.SelectedItem is RecordRow row)
        {
            int position = _vm.PositionInView(row);
            if (Keyboard.Modifiers == ModifierKeys.Shift && _anchor >= 0 && position >= 0) _vm.SetRange(_anchor, position, !row.IsSelected);
            else row.IsSelected = !row.IsSelected;
            if (position >= 0) _anchor = position;
            e.Handled = true;
        }
    }
}
