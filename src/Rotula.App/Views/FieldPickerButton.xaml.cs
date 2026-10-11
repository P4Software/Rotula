using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Rotula.App.Controls;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>
/// The "Insert field" control: a button that opens a flyout with the available fields (searchable once the list is long). In insert
/// mode the chosen field is written at the caret of <see cref="Target"/>, replacing the selection; in bind mode it becomes the whole content.
/// </summary>
public partial class FieldPickerButton : UserControl
{
    /// <summary>Lists longer than this get a search box.</summary>
    public const int SearchThreshold = 12;

    /// <summary>The row whose content the field goes into.</summary>
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(TextRow), typeof(FieldPickerButton), new PropertyMetadata(null, OnRowChanged));

    /// <summary>The text box that shows the row's text (insert mode writes at its caret).</summary>
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(TextBox), typeof(FieldPickerButton));

    /// <summary>True for the "Field" half of the switch: choosing a field replaces the whole content.</summary>
    public static readonly DependencyProperty BindProperty = DependencyProperty.Register(nameof(Bind), typeof(bool), typeof(FieldPickerButton));

    /// <summary>
    /// True when choosing a field replaces the whole content although the button looks like the Insert button. Used for values that cannot hold
    /// mixed text (a drop-down, a check box, a date picker): the field then stands for the whole value.
    /// </summary>
    public static readonly DependencyProperty ReplaceAllProperty = DependencyProperty.Register(nameof(ReplaceAll), typeof(bool), typeof(FieldPickerButton));

    /// <summary>Text on the button.</summary>
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(FieldPickerButton));

    /// <summary>In bind mode, true while the content is a field (the half of the switch is lit).</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(FieldPickerButton));

    /// <summary>What a screen reader announces after the button's name (for the "Field" switch: the field the content is bound to).</summary>
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(string), typeof(FieldPickerButton));

    /// <summary>Status text announced with the button.</summary>
    public string? Status { get => (string?)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    private ListCollectionViewHolder? _holder;
    private int _selStart, _selLength;

    private sealed class ListCollectionViewHolder
    {
        public System.Windows.Data.ListCollectionView View { get; }
        public ListCollectionViewHolder(System.Collections.IList source) => View = new System.Windows.Data.ListCollectionView(source);
    }

    /// <summary>Creates the control.</summary>
    public FieldPickerButton()
    {
        InitializeComponent();
        List.ItemTemplate = FieldTemplates.ItemTemplate();
        List.ItemContainerStyle = FieldTemplates.ItemContainerStyle(focusable: true);
    }

    /// <summary>The row whose content the field goes into.</summary>
    public TextRow? Row { get => (TextRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }

    /// <summary>The text box that shows the row's text.</summary>
    public TextBox? Target { get => (TextBox?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    /// <summary>True for the "Field" half of the switch.</summary>
    public bool Bind { get => (bool)GetValue(BindProperty); set => SetValue(BindProperty, value); }

    /// <summary>True when choosing a field replaces the whole content.</summary>
    public bool ReplaceAll { get => (bool)GetValue(ReplaceAllProperty); set => SetValue(ReplaceAllProperty, value); }

    /// <summary>Text on the button.</summary>
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    /// <summary>True while the content is a field.</summary>
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (FieldPickerButton)d;
        var toggle = self.Toggle;
        if (e.NewValue is TextRow { Fields: { } fields })
        {
            toggle.SetBinding(IsEnabledProperty, new Binding(nameof(FieldsViewModel.HasFields)) { Source = fields });
            toggle.SetBinding(ToolTipProperty, new Binding(nameof(FieldsViewModel.InsertTip)) { Source = fields });
        }
        else
        {
            BindingOperations.ClearBinding(toggle, IsEnabledProperty);
            BindingOperations.ClearBinding(toggle, ToolTipProperty);
            toggle.IsEnabled = false;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (Toggle is null) return;
        if (e.Property == CaptionProperty) AutomationProperties.SetName(Toggle, (string?)e.NewValue ?? string.Empty);
        else if (e.Property == StatusProperty) AutomationProperties.SetItemStatus(Toggle, (string?)e.NewValue ?? string.Empty);
        else if (e.Property == BindProperty) Toggle.Style = (Style)FindResource((bool)e.NewValue ? "SegmentToggle" : "PickerToggle");
    }

    private System.Windows.Data.ListCollectionView? View => _holder?.View;

    private void OnOpened(object sender, EventArgs e)
    {
        if (Row?.Fields is not { } fields) { Flyout.IsOpen = false; return; }
        _selStart = Target?.SelectionStart ?? 0;
        _selLength = Target?.SelectionLength ?? 0;
        _holder = new ListCollectionViewHolder(fields.Fields);
        List.ItemsSource = View;
        Search.Visibility = fields.Fields.Count > SearchThreshold ? Visibility.Visible : Visibility.Collapsed;
        Search.Text = string.Empty;
        UpdateCount();
        List.SelectedIndex = 0;
        // With a search box the focus starts there; otherwise inside the list, so Down and Enter work at once.
        FocusFlyout(attempt: 0);
        AnnounceCount();
    }

    private long _closedAt;

    // The button and the flyout are tied together by hand: a binding between them leaves the button lit after the flyout was closed with Esc,
    // and a click on the button while the flyout is open would close it (click outside) and open it again at once, so that click is ignored.
    private void OnToggleChecked(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _closedAt < 250) { Toggle.IsChecked = false; return; }
        Flyout.IsOpen = true;
    }

    private void OnToggleUnchecked(object sender, RoutedEventArgs e) => Flyout.IsOpen = false;

    // The flyout window is created a moment after the popup opens, and keyboard focus cannot enter it before that; try again a few times.
    private void FocusFlyout(int attempt)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!Flyout.IsOpen) return;
            if (Search.Visibility == Visibility.Visible) Search.Focus();
            else FocusSelected();
            bool inside = Search.IsKeyboardFocusWithin || List.IsKeyboardFocusWithin;
            if (!inside && attempt < 8)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                timer.Tick += (_, _) => { timer.Stop(); FocusFlyout(attempt + 1); };
                timer.Start();
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _holder = null;
        _closedAt = Environment.TickCount64;
        Toggle.IsChecked = false;
    }

    private void UpdateCount() => CountText.Text = FieldsViewModel.CountTextFor(View?.Count ?? 0);

    // WPF does not announce a changed live region by itself; raise the event so screen readers read "N fields available".
    private void AnnounceCount()
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            (UIElementAutomationPeer.FromElement(CountText) ?? UIElementAutomationPeer.CreatePeerForElement(CountText))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void FocusSelected()
    {
        if (List.SelectedItem is null && List.Items.Count > 0) List.SelectedIndex = 0;
        if (List.SelectedItem is not null && List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) is ListBoxItem item) item.Focus();
        else List.Focus();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (View is not { } view) return;
        string q = Search.Text.Trim();
        view.Filter = q.Length == 0 ? null : o => o is FieldItem f &&
            (f.Token.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Sample.Contains(q, StringComparison.OrdinalIgnoreCase));
        UpdateCount();
        List.SelectedIndex = view.Count > 0 ? 0 : -1;
        AnnounceCount();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                FocusSelected();
                e.Handled = true;
                break;
            case Key.Enter:
                if (List.SelectedItem is FieldItem f) Choose(f);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseAndReturnFocus();
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
                if (List.SelectedItem is FieldItem f) { Choose(f); e.Handled = true; }
                break;
            case Key.Escape:
                CloseAndReturnFocus();
                e.Handled = true;
                break;
            case Key.Up when List.SelectedIndex == 0 && Search.Visibility == Visibility.Visible:
                Search.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem { DataContext: FieldItem f })
        {
            Choose(f);
            e.Handled = true;
        }
    }

    private void CloseAndReturnFocus()
    {
        Flyout.IsOpen = false;
        if (Bind || Target is null) Toggle.Focus();
        else Target.Focus();
    }

    private void Choose(FieldItem field)
    {
        Flyout.IsOpen = false;
        if (Row is null) return;
        if (Bind || ReplaceAll)
        {
            Row.BindTo(field);
            Dispatcher.BeginInvoke(new Action(() => Toggle.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            return;
        }
        int caret = Row.InsertField(field, _selStart, _selLength);
        var target = Target;
        if (target is null) return;
        // Setting the bound text moves the caret to the start; put it right after the inserted field and give the box its focus back.
        target.CaretIndex = Math.Min(caret, target.Text.Length);
        Dispatcher.BeginInvoke(new Action(() => { target.Focus(); target.CaretIndex = Math.Min(caret, target.Text.Length); }), System.Windows.Threading.DispatcherPriority.Input);
    }
}
