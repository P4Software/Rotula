using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>
/// The GS1 builder dialog: presets and an identifier picker on the left, the chosen identifiers with their values in the middle, and the
/// result (preview, readable and FNC1 forms, character counter, problems) on the right. The search box has the focus when it opens; Down moves
/// into the results, Enter adds the highlighted identifier, and Enter anywhere else (or the Apply button) accepts the message once it is valid; Esc cancels.
/// </summary>
public partial class Gs1BuilderWindow : DialogWindow
{
    private readonly Gs1BuilderViewModel _vm;

    /// <summary>Creates the dialog for a view-model.</summary>
    public Gs1BuilderWindow(Gs1BuilderViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.Detach();
        };
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void OnCloseRequested(object? sender, bool applied)
    {
        try { DialogResult = applied; }
        catch (InvalidOperationException) { Close(); }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (Results.Items.Count > 0)
                {
                    if (Results.SelectedIndex < 0) Results.SelectedIndex = 0;
                    FocusSelected();
                    e.Handled = true;
                }
                break;
            case Key.Enter when Results.Items.Count > 0:
                // Enter in the search box adds the best match, so "10" Enter adds the batch number without touching the mouse.
                AddFromList(Results.SelectedItem as Gs1AiItem ?? Results.Items[0] as Gs1AiItem);
                e.Handled = true;
                break;
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            AddFromList(Results.SelectedItem as Gs1AiItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && Results.SelectedIndex == 0)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void OnResultsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(Results, d) is ListBoxItem { DataContext: Gs1AiItem item }) AddFromList(item);
    }

    private void FocusSelected()
    {
        if (Results.ItemContainerGenerator.ContainerFromItem(Results.SelectedItem) is ListBoxItem item) item.Focus();
        else Results.Focus();
    }

    private void AddFromList(Gs1AiItem? item)
    {
        if (item is null) return;
        var segment = _vm.AddAiCore(item.Ai);
        if (segment is null) return;
        // The new value box is ready to type in: that is the next thing the user wants after choosing an identifier.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var box in FindBoxes(this))
                if (box.DataContext is Gs1Segment s && ReferenceEquals(s, segment)) { box.Focus(); break; }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static IEnumerable<TextBox> FindBoxes(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { Name: "Box" } tb) yield return tb;
            foreach (var nested in FindBoxes(child)) yield return nested;
        }
    }
}
