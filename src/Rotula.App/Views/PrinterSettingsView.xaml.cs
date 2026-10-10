using System.Windows;
using System.Windows.Controls;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>Printer choice and printer profile fields; used by the Print dialog and the Data merge window.</summary>
public partial class PrinterSettingsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PrinterSettingsView() => InitializeComponent();

    // The refresh is async in the view-model, so the window stays responsive while Windows lists its printers.
    private async void OnRefreshPrinters(object sender, RoutedEventArgs e)
    {
        if (DataContext is PrinterSettingsViewModel vm) await vm.RefreshPrintersAsync();
    }
}
