using System.Windows;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>Lists the data source add-ons that could not be loaded, in plain language, with a way to review those waiting for approval.</summary>
public partial class PackDetailsWindow : DialogWindow
{
    /// <summary>Creates the dialog for a notice view-model.</summary>
    public PackDetailsWindow(PackNoticeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
