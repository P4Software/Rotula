using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Rotula.App.ViewModels;

namespace Rotula.App.Controls;

/// <summary>
/// Gives an editable font box the behaviour of the one in a word processor: typing searches the list ("ari" shows Arial and Arial Narrow), the
/// font is only chosen when the user picks an entry or presses Enter on a name that exists, and a half-typed name never reaches the label.
/// </summary>
/// <remarks>
/// Why not the combo box's own type-ahead: it selects an entry for every key, which would be one undo step and one text measurement per letter.
/// The box's data context must be the <see cref="TextFormatViewModel"/>.
/// </remarks>
public static class FontSearch
{
    /// <summary>Attach with <c>ctl:FontSearch.Enabled="True"</c> on an editable <see cref="ComboBox"/>.</summary>
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(FontSearch), new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>Reads <see cref="EnabledProperty"/>.</summary>
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    /// <summary>Writes <see cref="EnabledProperty"/>.</summary>
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;
        combo.RemoveHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged));
        combo.PreviewKeyDown -= OnKeyDown;
        combo.DropDownClosed -= OnDropDownClosed;
        combo.LostKeyboardFocus -= OnLostFocus;
        if (!(bool)e.NewValue) return;
        // TextBoxBase.TextChanged bubbles up from the combo's inner text box.
        combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged));
        combo.PreviewKeyDown += OnKeyDown;
        combo.DropDownClosed += OnDropDownClosed;
        combo.LostKeyboardFocus += OnLostFocus;
    }

    private static TextFormatViewModel? Vm(object sender) => (sender as FrameworkElement)?.DataContext as TextFormatViewModel;

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not ComboBox combo || Vm(sender) is not { } vm) return;
        // Only typing is a search. The box also changes its text when an entry is chosen or the label is refreshed; that must not filter the list.
        if (!combo.IsKeyboardFocusWithin) return;
        string text = combo.Text ?? string.Empty;
        string shown = (combo.SelectedItem as FontItem)?.Display ?? string.Empty;
        if (string.Equals(text, shown, StringComparison.CurrentCulture)) { vm.FontSearch = string.Empty; return; }
        vm.FontSearch = text;
        combo.IsDropDownOpen = true;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox combo || Vm(sender) is not { } vm) return;
        if (e.Key == Key.Enter)
        {
            // An entry the user arrowed to wins over the typed text.
            if (!(combo.IsDropDownOpen && combo.SelectedItem is FontItem picked && combo.Text == picked.Display)) vm.CommitFontText(combo.Text);
            combo.IsDropDownOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (combo.IsDropDownOpen || vm.FontSearch.Length > 0))
        {
            vm.FontSearch = string.Empty;
            combo.IsDropDownOpen = false;
            vm.CommitFontText(vm.SelectedFont?.Display);
            e.Handled = true;
        }
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (Vm(sender!) is { } vm) vm.FontSearch = string.Empty;
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not ComboBox combo || Vm(sender) is not { } vm) return;
        // Focus moving into the drop-down list is not leaving the box.
        if (combo.IsKeyboardFocusWithin || combo.IsDropDownOpen) return;
        vm.CommitFontText(combo.Text);
    }
}
