using System.Windows;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>
/// The QR content builder dialog: content type chips, a form for the chosen type, a live preview, the generated text and the capacity meter.
/// Enter applies (while the content is valid) and Esc cancels, because the buttons are the window's default and cancel buttons.
/// </summary>
public partial class ContentBuilderWindow : DialogWindow
{
    private readonly QrContentBuilderViewModel _vm;

    /// <summary>Creates the dialog for a view-model.</summary>
    public ContentBuilderWindow(QrContentBuilderViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // DialogResult can only be set on a window that is shown modally; the view-model asks to close whatever way it was opened.
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.Detach();
        };
        // The first field is ready to type in.
        Loaded += (_, _) => MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    private void OnCloseRequested(object? sender, bool applied)
    {
        try { DialogResult = applied; }
        catch (InvalidOperationException) { Close(); }
    }
}
