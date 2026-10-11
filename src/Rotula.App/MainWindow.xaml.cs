using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Rotula.App.Localization;
using Rotula.App.Views;
using Rotula.App.ViewModels;
using Rotula.Core.Model;

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
        Icon = Views.BrandIcon.Source;
        viewModel.Session.SelectionChanged += (_, _) => SyncLayersFromSession();
        viewModel.Layers.CollectionChanged += (_, _) => SyncLayersFromSession();
        viewModel.ExitRequested += (_, _) => Close();
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ObjectsPanelExpanded)) ApplyObjectsPanelState(); };
        // The window must fit the screen it opens on (a 1380 x 860 window is larger than a 1280 x 800 display or a 150 % scaled 1080p one).
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Loaded += async (_, _) =>
        {
            ApplyObjectsPanelState();
            viewModel.ZoomFitCommand.Execute(null);
            // The first thing a keyboard user should land on is the main call to action of the start screen.
            if (viewModel.ShowStartScreen) StartNewButton.Focus();
            // Printer discovery is async; the window is already usable while Windows lists its printers.
            await viewModel.Printer.RefreshPrintersAsync();
        };
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // A keyboard resize of the splitter raises no DragCompleted, so the position is saved here as well.
        SavePanelSplit();
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

    // ------------------------------------------------------------------------------- toolbar fit

    /// <summary>
    /// The toolbar has to fit at 1280 DIP wide in Spanish (about 1320 DIP of content with every caption). Below the thresholds the text
    /// captions in front of the drop-downs go first (their tooltips and automation names remain), then the "Preview" word goes and the
    /// printer box shrinks (and disappears below about 1060 DIP). The icons and controls themselves never move.
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width;
        var captions = w >= 1460 ? Visibility.Visible : Visibility.Collapsed;
        ZoomCaption.Visibility = LabelCaption.Visibility = PrinterCaption.Visibility = captions;
        PreviewText.Visibility = w >= 1200 ? Visibility.Visible : Visibility.Collapsed;
        PreviewIcon.Margin = w >= 1200 ? new Thickness(0, 0, 8, 0) : new Thickness(0);
        PrinterBox.Width = w >= 1300 ? 220 : w >= 1200 ? 190 : 150;
        // On a very small work area (1080p at 200 % is only 960 DIP wide) the printer box goes too; the printer is still chosen in the Print dialog.
        PrinterBox.Visibility = w >= 1060 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------------------- label size picker

    /// <summary>
    /// Opens the label size menu under the toolbar button: the common sizes (the current one is ticked), "Custom size..." and the print
    /// resolution. A ContextMenu gives arrow-key navigation, Escape and focus return for free, which keeps the picker keyboard accessible.
    /// </summary>
    private void OnLabelPickerClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = LabelPickerButton,
            Placement = PlacementMode.Bottom,
            // A ContextMenu is outside the visual tree, so it does not inherit the window's data context or the language.
            DataContext = _vm,
            MinWidth = LabelPickerButton.ActualWidth,
        };
        menu.Items.Add(Caption(Loc.Get("Picker.Sizes")));
        foreach (var preset in _vm.SizePresets)
        {
            var captured = preset;
            var item = new MenuItem { Header = preset.Name, IsCheckable = true, IsChecked = _vm.IsCurrentSize(preset) };
            item.Click += (_, _) => _vm.ApplyLabelSize(captured);
            menu.Items.Add(item);
        }
        var custom = new MenuItem { Header = Loc.Get("Picker.Custom") };
        custom.Click += (_, _) => ShowLabelSettings();
        menu.Items.Add(new Separator());
        menu.Items.Add(custom);
        menu.Items.Add(new Separator());
        menu.Items.Add(Caption(Loc.Get("Picker.Resolution")));
        foreach (var dpi in Enum.GetValues<PrinterDpi>())
        {
            var captured = dpi;
            var item = new MenuItem { Header = Loc.EnumName(dpi), IsCheckable = true, IsChecked = _vm.Session.Document.Dpi == dpi };
            item.Click += (_, _) => _vm.SetLabelDpi(captured);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private static MenuItem Caption(string text) => new()
    {
        Header = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("MenuCaption"), Margin = new Thickness(0, 6, 0, 4) },
        IsEnabled = false,
        Focusable = false,
    };

    /// <summary>Shows the label's own settings in the Properties panel and puts the cursor in the first field (the width).</summary>
    private void ShowLabelSettings()
    {
        SelectTool.IsChecked = true;
        _vm.SelectNoneCommand.Execute(null);
        PropertiesPanelView.FocusFirstField();
    }

    // ------------------------------------------------------------------------------- right column split

    private bool _restoringSplit;

    /// <summary>Applies the remembered Properties / Objects proportion (a share of the column, so it survives resizing and other DPI settings).</summary>
    private void RestorePanelSplit()
    {
        _restoringSplit = true;
        double share = _vm.PropertiesPanelShare;
        PropertiesRow.Height = new GridLength(share, GridUnitType.Star);
        ObjectsRow.Height = new GridLength(1 - share, GridUnitType.Star);
        _restoringSplit = false;
    }

    /// <summary>
    /// Opens or closes the Objects list. Closed, its row shrinks to the header and the Properties panel takes the rest of the column;
    /// open, the remembered Properties / Objects proportion comes back.
    /// </summary>
    private void ApplyObjectsPanelState()
    {
        bool open = _vm.ObjectsPanelExpanded;
        ObjectsBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ObjectsActions.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PanelSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ObjectsRow.MinHeight = open ? 170 : 0;
        if (open) RestorePanelSplit();
        else
        {
            _restoringSplit = true;
            PropertiesRow.Height = new GridLength(1, GridUnitType.Star);
            ObjectsRow.Height = GridLength.Auto;
            _restoringSplit = false;
        }
    }

    private void OnPanelSplitterDragCompleted(object sender, DragCompletedEventArgs e) => SavePanelSplit();

    private void SavePanelSplit()
    {
        if (_restoringSplit) return;
        double total = PropertiesRow.ActualHeight + ObjectsRow.ActualHeight;
        if (total > 0) _vm.PropertiesPanelShare = PropertiesRow.ActualHeight / total;
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

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
}
