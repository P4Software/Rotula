using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Model;

namespace Rotula.App.Views;

/// <summary>
/// The New label dialog: a stock picker (search as you type, filters, favourites, details and warnings) next to the manual size.
/// The search box has the focus when the dialog opens; the arrow keys move through the results without leaving it, Enter creates the label.
/// </summary>
public partial class NewLabelWindow : DialogWindow
{
    /// <summary>How long typing must pause before the search runs. The search itself takes well under a millisecond; this only avoids redrawing the list for every key.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

    private readonly NewLabelViewModel _vm;
    private readonly DispatcherTimer _timer;

    /// <summary>Creates the dialog without an application context (stock library of the process, nothing remembered).</summary>
    public NewLabelWindow() : this(new NewLabelContext()) { }

    /// <summary>Creates the dialog.</summary>
    public NewLabelWindow(NewLabelContext context)
    {
        _vm = new NewLabelViewModel(context, new WpfStockDialogs());
        DataContext = _vm;
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = Debounce };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _vm.RunSearch();
            ScrollToSelection();
        };
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await _vm.LoadCatalogAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _vm.Dispose();
        };
        PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    /// <summary>The label the user asked for; set when the dialog closed with Create.</summary>
    public LabelDocument? Result { get; private set; }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        // A search that is still waiting for its pause must finish first, or Enter would create from the previous list.
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            _vm.RunSearch();
        }
        // Keep the dialog open and show the problem when the choice is not usable.
        Result = _vm.TryCreate();
        if (Result is not null) DialogResult = true;
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double click on a row (not on its star or the scroll bar) creates the label.
        if (e.OriginalSource is DependencyObject source && FindAncestor<ListBoxItem>(source) is not null && FindAncestor<Button>(source) is null)
            OnCreate(sender, e);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _timer.Stop();
        _timer.Start();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        int delta = e.Key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 8, Key.PageUp => -8, _ => 0 };
        if (delta == 0) return;
        // The list reflects what is typed before the selection moves.
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            _vm.RunSearch();
        }
        _vm.MoveSelection(delta);
        ScrollToSelection();
        e.Handled = true;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void ScrollToSelection()
    {
        if (_vm.SelectedItem is { } item) StockList.ScrollIntoView(item);
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? (d as FrameworkContentElement)?.Parent)
            if (d is T hit) return hit;
        return null;
    }
}
