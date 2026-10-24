using System.Windows;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>Makes the exit PIN line for a print station's station.json. The PIN boxes are copied into the view-model only when Create is pressed.</summary>
public partial class StationPinWindow : DialogWindow
{
    private readonly StationPinViewModel _vm;

    /// <summary>Creates the window.</summary>
    public StationPinWindow(StationPinViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => PinBox.Focus();
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        _vm.Pin = PinBox.Password;
        _vm.Confirm = ConfirmBox.Password;
        _vm.CreateCommand.Execute(null);
        // The PIN is not kept anywhere once its hash is made.
        _vm.Pin = _vm.Confirm = string.Empty;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
