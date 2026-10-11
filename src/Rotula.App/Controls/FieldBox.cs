using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Rotula.App.Localization;
using Rotula.App.ViewModels;

namespace Rotula.App.Controls;

/// <summary>
/// Adds field autocomplete to a text box: typing <c>{{</c> opens a small list of the available fields next to the caret, filtered as the
/// user types. Up/Down choose, Enter or Tab insert the field and the closing braces, Esc closes. The list never takes the keyboard focus
/// (the user keeps typing in the box), so it works with input methods and screen readers: the count of matches is a live region.
/// </summary>
public static class FieldBox
{
    /// <summary>The row the box edits. Setting it (to a row that has fields) switches the autocomplete on.</summary>
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.RegisterAttached("Row", typeof(TextRow), typeof(FieldBox), new PropertyMetadata(null, OnRowChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(AutoComplete), typeof(FieldBox));

    /// <summary>Gets the row.</summary>
    public static TextRow? GetRow(DependencyObject d) => (TextRow?)d.GetValue(RowProperty);

    /// <summary>Sets the row.</summary>
    public static void SetRow(DependencyObject d, TextRow? value) => d.SetValue(RowProperty, value);

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        var state = (AutoComplete?)box.GetValue(StateProperty);
        if (e.NewValue is TextRow { IsFieldTarget: true } row)
        {
            if (state is null)
            {
                state = new AutoComplete(box);
                box.SetValue(StateProperty, state);
            }
            state.Row = row;
        }
        else state?.Close();
    }

    /// <summary>
    /// Finds the open <c>{{</c> the caret is inside of. Returns the index of the braces and the characters typed after them, or null when the
    /// caret is not inside an unfinished placeholder.
    /// </summary>
    public static (int Open, string Prefix)? FindContext(string text, int caret)
    {
        if (caret < 2 || caret > text.Length) return null;
        for (int i = caret - 2; i >= 0; i--)
        {
            char c = text[i];
            if (c == '}' || c == '\n' || c == '\r') return null;
            if (c == '{' && text[i + 1] == '{')
            {
                string prefix = text[(i + 2)..caret];
                return prefix.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '#') ? (i, prefix) : null;
            }
        }
        return null;
    }

    /// <summary>The fields to offer for what has been typed after <c>{{</c>: names that start with it first, then names that contain it.</summary>
    public static List<FieldItem> Match(IEnumerable<FieldItem> fields, string prefix)
    {
        if (prefix.Length == 0) return fields.ToList();
        var starts = fields.Where(f => f.Token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        var contains = fields.Where(f => !starts.Contains(f) &&
            (f.Token.Contains(prefix, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
        starts.AddRange(contains);
        return starts;
    }

    private sealed class AutoComplete
    {
        private readonly TextBox _box;
        private readonly Popup _popup;
        private readonly ListBox _list;
        private readonly TextBlock _count;
        private int _open = -1;

        public AutoComplete(TextBox box)
        {
            _box = box;
            _count = new TextBlock
            {
                FontSize = 11.5, Margin = new Thickness(10, 6, 10, 4), FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            };
            AutomationProperties.SetLiveSetting(_count, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(_count, Loc.Get("Fields.List"));
            _list = new ListBox
            {
                MaxHeight = 224, Focusable = false, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                ItemTemplate = FieldTemplates.ItemTemplate(), ItemContainerStyle = FieldTemplates.ItemContainerStyle(),
            };
            AutomationProperties.SetName(_list, Loc.Get("Fields.List"));
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true; // keep the keyboard focus in the text box
            _list.PreviewMouseLeftButtonUp += OnListClick;
            var panel = new StackPanel();
            panel.Children.Add(_count);
            panel.Children.Add(_list);
            var card = new Border
            {
                Width = 300, Margin = new Thickness(0, 4, 12, 12), Padding = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.FindResource("SurfaceBrush"), BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.FindResource("BorderStrongBrush"), Child = panel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.18, Color = Colors.Black },
            };
            _popup = new Popup
            {
                Child = card, PlacementTarget = box, Placement = PlacementMode.Relative, AllowsTransparency = true, StaysOpen = true, Focusable = false,
                PopupAnimation = PopupAnimation.Fade,
            };
            box.TextChanged += (_, _) => Evaluate(typing: true);
            box.SelectionChanged += (_, _) => { if (_popup.IsOpen) Evaluate(typing: false); };
            box.PreviewKeyDown += OnKeyDown;
            box.LostKeyboardFocus += (_, _) => Close();
            box.Unloaded += (_, _) => Close();
        }

        public TextRow? Row { get; set; }

        public void Close()
        {
            _popup.IsOpen = false;
            _open = -1;
        }

        private void Evaluate(bool typing)
        {
            if (Row?.Fields is not { } fields) { Close(); return; }
            // Only what the user types opens the list; a text that changed by itself (undo, another selection) must not pop it up.
            if (!_popup.IsOpen && (!typing || !_box.IsKeyboardFocusWithin)) return;
            if (FindContext(_box.Text, _box.CaretIndex) is not { } ctx) { Close(); return; }
            var matches = Match(fields.Fields, ctx.Prefix);
            if (matches.Count == 0) { Close(); return; }
            _open = ctx.Open;
            string text = FieldsViewModel.CountTextFor(matches.Count);
            bool changed = _count.Text != text;
            _list.ItemsSource = matches;
            _list.SelectedIndex = 0;
            _count.Text = text;
            Place();
            _popup.IsOpen = true;
            if (changed && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                (UIElementAutomationPeer.FromElement(_count) ?? UIElementAutomationPeer.CreatePeerForElement(_count))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void Place()
        {
            var rect = _box.GetRectFromCharacterIndex(Math.Max(0, _open));
            if (rect.IsEmpty) { _popup.HorizontalOffset = 0; _popup.VerticalOffset = _box.ActualHeight; return; }
            _popup.HorizontalOffset = Math.Clamp(rect.Left, 0, Math.Max(0, _box.ActualWidth - 40));
            _popup.VerticalOffset = rect.Bottom + 2;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!_popup.IsOpen) return;
            switch (e.Key)
            {
                case Key.Down:
                    _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                    _list.ScrollIntoView(_list.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Up:
                    _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                    _list.ScrollIntoView(_list.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Enter:
                case Key.Tab:
                    if (_list.SelectedItem is FieldItem f) { Accept(f); e.Handled = true; }
                    break;
                case Key.Escape:
                    Close();
                    e.Handled = true; // the window must not treat Esc as "deselect everything"
                    break;
            }
        }

        private void OnListClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListBoxItem { DataContext: FieldItem f })
            {
                Accept(f);
                e.Handled = true;
            }
        }

        private void Accept(FieldItem field)
        {
            if (Row is null || _open < 0) { Close(); return; }
            string text = _box.Text;
            int caret = _box.CaretIndex;
            // Typing {{Sk}} by hand leaves closing braces behind the caret; they are replaced instead of doubled.
            int end = caret + (string.CompareOrdinal(text, caret, "}}", 0, 2) == 0 ? 2 : 0);
            int open = _open;
            Close();
            int newCaret = Row.InsertField(field, open, end - open);
            _box.CaretIndex = newCaret;
            _box.Dispatcher.BeginInvoke(new Action(() => { _box.Focus(); _box.CaretIndex = Math.Min(newCaret, _box.Text.Length); }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }
}
