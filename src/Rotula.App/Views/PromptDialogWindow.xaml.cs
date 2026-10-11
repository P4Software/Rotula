using System.Windows;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>
/// The "before printing" dialog that asks the questions of the label's prompt variables. Enter continues (when every answer is accepted)
/// and Esc cancels, because the buttons are the window's default and cancel buttons.
/// </summary>
public partial class PromptDialogWindow : DialogWindow
{
    /// <summary>Creates the dialog for a view-model.</summary>
    public PromptDialogWindow(PromptDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
        // The first question is ready to type in.
        Loaded += (_, _) => MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    private void OnCloseRequested(object? sender, bool accepted)
    {
        try { DialogResult = accepted; }
        catch (InvalidOperationException) { Close(); }
    }
}
