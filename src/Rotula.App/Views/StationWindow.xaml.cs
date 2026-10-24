using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rotula.App.ViewModels;
using Rotula.Files.Station;

namespace Rotula.App.Views;

/// <summary>
/// The print station window. Behaviour is in <see cref="StationViewModel"/>; this class turns keys into scanner steps (Enter and Tab end an
/// entry), moves the keyboard where the view-model asks, redraws the preview a moment after typing stops, and refuses to close without the PIN.
/// </summary>
public partial class StationWindow : Window
{
    /// <summary>Visible when every bound value is true.</summary>
    public static readonly IMultiValueConverter AllTrue = new AllTrueConverter();

    private readonly StationViewModel _vm;
    private readonly DispatcherTimer _previewTimer;

    /// <summary>Creates the window for a station.</summary>
    public StationWindow(StationViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Icon = BrandIcon.Source;
        if (viewModel.FullScreen)
        {
            // Full screen without a title bar: nothing but the station is on the screen.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        // A scanner types a whole code in a few milliseconds; drawing the label after every character would make it lag behind.
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _vm.RenderPreview(); };

        viewModel.PreviewRefreshRequested += OnPreviewRefreshRequested;
        viewModel.FocusFieldRequested += OnFocusField;
        viewModel.FocusPrintRequested += OnFocusPrint;
        viewModel.FocusSearchRequested += OnFocusSearch;
        viewModel.ExitRequested += OnExitRequested;
        viewModel.PropertyChanged += OnViewModelChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        // A field that gets the keyboard has its text selected, so the next scan replaces it instead of adding to it.
        AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => { if (e.NewFocus is TextBox t) t.SelectAll(); }), true);
        Loaded += async (_, _) =>
        {
            OnFocusSearch(this, EventArgs.Empty);
            await _vm.StartAsync();
        };
    }

    private void OnPreviewRefreshRequested(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StationViewModel.IsPinPromptOpen)) return;
        // The PIN box is modal: nothing behind it takes the keyboard while it is open.
        MainArea.IsEnabled = !_vm.IsPinPromptOpen;
        if (_vm.IsPinPromptOpen) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { PinBox.Clear(); PinBox.Focus(); });
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.IsPinPromptOpen)
        {
            if (e.Key is Key.Enter) { e.Handled = true; await SubmitPinAsync(); }
            else if (e.Key is Key.Escape) { e.Handled = true; _vm.CancelExit(); }
            return;
        }
        bool enter = e.Key == Key.Enter, tab = e.Key == Key.Tab;
        if (!enter && !tab) return;
        var source = e.OriginalSource as DependencyObject;
        if (ReferenceEquals(source, SearchBox))
        {
            if (!enter) return;
            e.Handled = true;
            await _vm.SubmitSearchAsync();
            return;
        }
        if (enter && (ReferenceEquals(source, CopiesBox) || ReferenceEquals(source, PrintButton)))
        {
            e.Handled = true;
            await _vm.PrintAsync();
            return;
        }
        if (_vm.IsLabelOpen && (source as FrameworkElement)?.DataContext is PromptFieldViewModel field && IsInFields(source))
        {
            // An open list of choices takes Enter itself (it picks the highlighted entry).
            if (enter && FindAncestor<ComboBox>(source) is { IsDropDownOpen: true }) return;
            e.Handled = true;
            var terminator = enter ? ScanTerminator.Enter : (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? ScanTerminator.ShiftTab : ScanTerminator.Tab;
            await _vm.FieldTerminatedAsync(field, terminator);
        }
    }

    private bool IsInFields(DependencyObject? source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, FieldsList)) return true;
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T found) return found;
        return null;
    }

    private void OnFocusField(object? sender, int index)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (FieldsList.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container) return;
            var input = FindInput(container);
            if (input is ComboBox combo)
            {
                combo.Focus();
                if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox inner) { inner.Focus(); inner.SelectAll(); }
            }
            else if (input is TextBox box)
            {
                box.Focus();
                box.SelectAll();
            }
            input?.BringIntoView();
        });
    }

    /// <summary>The visible input of a question (its text box, or the list of a choice question).</summary>
    private static FrameworkElement? FindInput(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: "StationField", IsVisible: true } fe) return fe;
            if (FindInput(child) is { } found) return found;
        }
        return null;
    }

    private void OnFocusPrint(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { PrintButton.Focus(); });

    private void OnFocusSearch(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (_vm.IsChoosing) { SearchBox.Focus(); SearchBox.SelectAll(); } });

    private async void OnPinOk(object sender, RoutedEventArgs e) => await SubmitPinAsync();

    private async Task SubmitPinAsync()
    {
        string pin = PinBox.Password;
        PinBox.Clear();
        if (!await _vm.SubmitPinAsync(pin)) PinBox.Focus();
    }

    private void OnExitRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Close);

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_vm.ExitAuthorized || !_vm.HasPin)
        {
            _vm.CancelOperation();
            return;
        }
        // Alt+F4 and the like: the station stays until the PIN is given.
        e.Cancel = true;
        _vm.RequestExit();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _previewTimer.Stop();
        _vm.PreviewRefreshRequested -= OnPreviewRefreshRequested;
        _vm.FocusFieldRequested -= OnFocusField;
        _vm.FocusPrintRequested -= OnFocusPrint;
        _vm.FocusSearchRequested -= OnFocusSearch;
        _vm.ExitRequested -= OnExitRequested;
        _vm.PropertyChanged -= OnViewModelChanged;
    }

    private sealed class AllTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
