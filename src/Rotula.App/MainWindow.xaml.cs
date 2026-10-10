using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rotula.App.Localization;
using Rotula.App.ViewModels;

namespace Rotula.App;

/// <summary>The main window. Almost all behaviour lives in <see cref="MainViewModel"/>; this class only wires view-specific details.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _syncing;

    /// <summary>Creates the window for a view-model.</summary>
    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Session.SelectionChanged += (_, _) => SyncLayersFromSession();
        viewModel.Layers.CollectionChanged += (_, _) => SyncLayersFromSession();
        viewModel.ExitRequested += (_, _) => Close();
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        Loaded += async (_, _) =>
        {
            viewModel.ZoomFitCommand.Execute(null);
            // Printer discovery is async; the window is already usable while Windows lists its printers.
            await viewModel.Printer.RefreshPrintersAsync();
        };
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Never lose work silently: ask first, and let the user cancel the close.
        if (!_vm.ConfirmClose()) e.Cancel = true;
        else _vm.OnClosing();
    }

    // ------------------------------------------------------------------------------- custom title bar

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = (System.Windows.Media.Geometry)FindResource(maximized ? "IconRestore" : "IconMaximize");
        string label = Loc.Get(maximized ? "Window.Restore" : "Window.Maximize");
        MaximizeButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, label);
        // A borderless window that is maximised extends past the screen by its resize border; pull the content back in.
        RootGrid.Margin = maximized ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + SystemParameters.FixedFrameVerticalBorderWidth,
            SystemParameters.WindowResizeBorderThickness.Top + SystemParameters.FixedFrameHorizontalBorderHeight,
            SystemParameters.WindowResizeBorderThickness.Right + SystemParameters.FixedFrameVerticalBorderWidth,
            SystemParameters.WindowResizeBorderThickness.Bottom + SystemParameters.FixedFrameHorizontalBorderHeight) : new Thickness(0);
    }

    // ------------------------------------------------------------------------------- toolbar

    private void OnZoomPresetChosen(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || e.AddedItems.Count != 1 || e.AddedItems[0] is not int percent) return;
        _vm.SetZoomPercent(percent);
        // Clear the pick so the same preset can be chosen again (the box itself displays the live zoom).
        combo.SelectedItem = null;
    }

    private void OnPrinterChosen(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || sender is not ComboBox { IsDropDownOpen: true }) return;
        // Choosing a Windows printer in the toolbar switches away from a network target and is remembered for next time.
        _vm.Printer.UseNetwork = false;
        _vm.Printer.Persist();
    }

    private void OnSelectTool(object sender, RoutedEventArgs e)
    {
        // The pointer is always the active tool (other tools place an element at once), so the button stays pressed.
        SelectTool.IsChecked = true;
        _vm.SelectNoneCommand.Execute(null);
        Design.FocusCanvas();
    }

    // ------------------------------------------------------------------------------- layers

    private void OnLayersSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        try { _vm.SelectLayers(LayersList.SelectedItems.OfType<LayerItem>().Select(l => l.Id)); }
        finally { _syncing = false; }
    }

    private void SyncLayersFromSession()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            LayersList.SelectedItems.Clear();
            foreach (var item in _vm.Layers.Where(l => _vm.Session.SelectedIds.Contains(l.Id)))
                LayersList.SelectedItems.Add(item);
        }
        finally { _syncing = false; }
    }

    private void OnLayersKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            _vm.DeleteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, Loc.Get("About.Body", typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0"), Loc.Get("Menu.About"),
            MessageBoxButton.OK, MessageBoxImage.Information);
}
