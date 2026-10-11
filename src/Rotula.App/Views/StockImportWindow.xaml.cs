using System.Windows;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>The preview of an imported supplies list: the stocks found with how sure Rotula is about each, and the rows it skipped and why.</summary>
public partial class StockImportWindow : DialogWindow
{
    /// <summary>Creates the window.</summary>
    public StockImportWindow(StockImportPreviewViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
