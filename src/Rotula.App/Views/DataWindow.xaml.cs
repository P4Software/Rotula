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
public partial class DataWindow : Window
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
        Loaded += (_, _) => BuildColumns();
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
        // A batch that is still running stops when the window closes, instead of continuing invisibly.
        _vm.CancelOperation();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _vm.Printer.Persist();
    }
}
