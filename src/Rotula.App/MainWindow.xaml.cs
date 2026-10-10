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
        Loaded += (_, _) => viewModel.ZoomFitCommand.Execute(null);
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Never lose work silently: ask first, and let the user cancel the close.
        if (!_vm.ConfirmClose()) e.Cancel = true;
        else _vm.OnClosing();
    }

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
