using System.ComponentModel;
using System.Windows;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>The Print dialog. All behaviour is in <see cref="PrintViewModel"/>; this class only connects window events.</summary>
public partial class PrintWindow : Window
{
    private readonly PrintViewModel _vm;

    /// <summary>Creates the dialog.</summary>
    public PrintWindow(PrintViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        // Printer discovery runs async, so the dialog appears at once and fills in when Windows answers.
        Loaded += async (_, _) => await _vm.Printer.RefreshPrintersAsync();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Closing the window (Esc, X) must stop a job that is still being sent.
        _vm.CancelOperation();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _vm.Printer.Persist();
    }
}
