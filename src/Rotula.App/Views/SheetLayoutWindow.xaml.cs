using System.ComponentModel;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>The "Label sheets" dialog. All behaviour is in <see cref="SheetLayoutViewModel"/>; this class only connects window events.</summary>
public partial class SheetLayoutWindow : DialogWindow
{
    private readonly SheetLayoutViewModel _vm;

    /// <summary>Creates the dialog.</summary>
    public SheetLayoutWindow(SheetLayoutViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Closing the window (Esc, X) must stop an export that is still being written.
        _vm.CancelOperation();
        _vm.RememberLayout();
    }
}
