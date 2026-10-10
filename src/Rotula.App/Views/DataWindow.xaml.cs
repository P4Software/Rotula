using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>
/// The Data merge window. It is not modal, so the canvas behind it can be watched while moving through the records.
/// Behaviour lives in <see cref="DataMergeViewModel"/>; this class builds the preview grid columns and connects window events.
/// </summary>
public partial class DataWindow : DialogWindow
{
    private readonly DataMergeViewModel _vm;

    /// <summary>Creates the window.</summary>
    public DataWindow(DataMergeViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.TableChanged += (_, _) => BuildColumns();
        // The database view fills the window when it is tall enough and scrolls when it is not; a view with a star row cannot
        // do that by itself inside a ScrollViewer, so its height is set from the viewport.
        DbScroll.SizeChanged += (_, _) => FitDatabaseView();
        DbScroll.IsVisibleChanged += (_, _) => FitDatabaseView();
        Loaded += (_, _) =>
        {
            BuildColumns();
            // Reading the list of saved connections is a local file read; no database is contacted by opening this window.
            if (_vm.IsDatabaseMode) _ = _vm.Database.RefreshConnectionsAsync();
        };
    }

    /// <summary>
    /// Creates one grid column per data column. Columns are built in code because their number and names come from the file;
    /// the cells bind by index (<c>[i]</c>) so a column name with dots or brackets can never break a binding path.
    /// </summary>
    private void BuildColumns()
    {
        PreviewGrid.Columns.Clear();
        var names = _vm.PreviewColumns;
        for (int i = 0; i < names.Count; i++)
        {
            PreviewGrid.Columns.Add(new DataGridTextColumn
            {
                Header = names[i],
                Binding = new Binding($"[{i}]") { Mode = BindingMode.OneWay },
                MinWidth = 70,
            });
        }
        // The last column takes the space that is left, so the table always fills its frame instead of ending in an empty strip.
        if (PreviewGrid.Columns.Count > 0) PreviewGrid.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }

    /// <summary>Smallest height of the database view: room for the connection card, the tabs, a few result rows and the "Use this data" bar.</summary>
    private const double DatabaseViewMinHeight = 520;

    private void FitDatabaseView()
    {
        if (DbScroll.ViewportHeight > 0)
            DbView.Height = Math.Max(DbScroll.ViewportHeight - DbView.Margin.Top - DbView.Margin.Bottom, DatabaseViewMinHeight);
    }

    private void OnGoToKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_vm.GoToRecordCommand.CanExecute(null)) _vm.GoToRecordCommand.Execute(null);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // A batch (or database read) that is still running stops when the window closes, instead of continuing invisibly.
        _vm.CancelOperation();
        _vm.Database.CancelOperation();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _vm.Printer.Persist();
    }
}
