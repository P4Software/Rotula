using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>
/// The database half of the Data merge window (connection, table list or query editor, results). Behaviour lives in
/// <see cref="DatabaseSourceViewModel"/>; this class only builds the grid columns, whose number and names come from the database.
/// </summary>
public partial class DatabaseSourceView : UserControl
{
    private DatabaseSourceViewModel? _vm;

    /// <summary>Creates the view.</summary>
    public DatabaseSourceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.GridChanged -= OnGridChanged;
        _vm = e.NewValue as DatabaseSourceViewModel;
        if (_vm is not null) _vm.GridChanged += OnGridChanged;
        BuildColumns();
    }

    private void OnGridChanged(object? sender, EventArgs e) => BuildColumns();

    /// <summary>
    /// One grid column per result column. Cells bind by index (<c>[i]</c>) so a column name with dots or brackets can never
    /// break a binding path.
    /// </summary>
    private void BuildColumns()
    {
        var names = _vm?.GridColumns ?? Array.Empty<string>();
        foreach (var grid in new[] { TableGrid, ResultGrid })
        {
            grid.Columns.Clear();
            for (int i = 0; i < names.Count; i++)
                grid.Columns.Add(new DataGridTextColumn { Header = names[i], Binding = new Binding($"[{i}]") { Mode = BindingMode.OneWay }, MinWidth = 70 });
            if (grid.Columns.Count > 0) grid.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        }
    }
}
