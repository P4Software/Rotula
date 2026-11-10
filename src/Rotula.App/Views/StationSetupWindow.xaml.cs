using System.ComponentModel;
using System.Windows;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>"Set up print station". All behaviour is in <see cref="StationSetupViewModel"/>; this class only connects window events.</summary>
public partial class StationSetupWindow : DialogWindow
{
    private readonly StationSetupViewModel _vm;

    /// <summary>Creates the window.</summary>
    public StationSetupWindow(StationSetupViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // The labels are checked as soon as the window is up, so the person sees each label's result without pressing anything first.
        Loaded += async (_, _) => await _vm.CheckCommand.ExecuteAsync(null);
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Closing the window (Esc, X) stops a check that is still asking a driver.
        _vm.CancelOperation();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
