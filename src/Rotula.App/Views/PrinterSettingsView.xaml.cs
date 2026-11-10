using System.Windows;
using System.Windows.Controls;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>Printer choice and printer profile fields; used by the Print dialog and the Data merge window.</summary>
public partial class PrinterSettingsView : UserControl
{
    /// <summary>Identifies the <see cref="ShowModeChoice"/> dependency property.</summary>
    public static readonly DependencyProperty ShowModeChoiceProperty =
        DependencyProperty.Register(nameof(ShowModeChoice), typeof(bool), typeof(PrinterSettingsView), new PropertyMetadata(false));

    /// <summary>Creates the view.</summary>
    public PrinterSettingsView() => InitializeComponent();

    /// <summary>
    /// True to show the print mode under the printer list: the one-time question for a printer that has no saved mode, or the mode line with
    /// the way to switch. The Print dialog and the Data window both set it, because everything they print (a single label, the test label, and
    /// a batch of records) follows the printer's mode, and nothing prints until the question is answered. Off by default for any other host.
    /// </summary>
    public bool ShowModeChoice
    {
        get => (bool)GetValue(ShowModeChoiceProperty);
        set => SetValue(ShowModeChoiceProperty, value);
    }

    // The refresh is async in the view-model, so the window stays responsive while Windows lists its printers.
    private async void OnRefreshPrinters(object sender, RoutedEventArgs e)
    {
        if (DataContext is PrinterSettingsViewModel vm) await vm.RefreshPrintersAsync();
    }
}
