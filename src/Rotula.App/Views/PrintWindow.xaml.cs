using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>The Print dialog. All behaviour is in <see cref="PrintViewModel"/>; this class only connects window events.</summary>
public partial class PrintWindow : DialogWindow
{
    private readonly PrintViewModel _vm;

    /// <summary>Creates the dialog.</summary>
    public PrintWindow(PrintViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += (_, e) =>
        {
            // The findings sit at the top of the scrolled content. After pressing Print the person may have scrolled down to the copies, so the
            // list (and its box) is brought into view; after the layout pass, because the card has only just become visible.
            if (e.PropertyName == nameof(PrintViewModel.HasPreflightLines) && _vm.HasPreflightLines)
                Dispatcher.BeginInvoke(() => FindingsCard.BringIntoView(), DispatcherPriority.Loaded);
        };
        // Printer discovery runs async, so the dialog appears at once and fills in when Windows answers.
        Loaded += async (_, _) =>
        {
            // With records to choose from, the list is the first thing the operator works with, so the keyboard starts there (Tab moves on to the printer).
            if (_vm.HasRecords) RecordsList.Grid.Focus();
            await _vm.Printer.RefreshPrintersAsync();
        };
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
        _vm.Detach();
    }
}
