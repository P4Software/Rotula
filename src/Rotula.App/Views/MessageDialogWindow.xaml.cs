using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Rotula.App.Localization;

namespace Rotula.App.Views;

/// <summary>What a message is about; decides the icon and its colour.</summary>
public enum MessageKind
{
    /// <summary>Plain information.</summary>
    Info,
    /// <summary>Something needs care but nothing failed.</summary>
    Warning,
    /// <summary>Something failed.</summary>
    Error,
    /// <summary>The user is asked to decide.</summary>
    Question,
}

/// <summary>One button of a <see cref="MessageDialogWindow"/>.</summary>
/// <param name="Text">Translated caption.</param>
/// <param name="IsPrimary">Drawn as the solid accent button (use for the one recommended action).</param>
/// <param name="IsDefault">Pressed by Enter and focused first.</param>
/// <param name="IsCancel">Pressed by Esc (and by the window's close button).</param>
public sealed record DialogChoice(string Text, bool IsPrimary = false, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// The program's own message box: it replaces the standard Windows one so every message looks like the rest of the app
/// (title bar, icon badge, primary / secondary buttons) and works with the keyboard (Enter = default, Esc = cancel).
/// </summary>
public partial class MessageDialogWindow : DialogWindow
{
    private int _cancelIndex = -1;

    /// <summary>Creates the dialog.</summary>
    /// <param name="kind">Icon and colour.</param>
    /// <param name="title">Window title and heading.</param>
    /// <param name="message">What happened and what to do next.</param>
    /// <param name="choices">The buttons, left to right.</param>
    public MessageDialogWindow(MessageKind kind, string title, string message, IReadOnlyList<DialogChoice> choices)
    {
        InitializeComponent();
        Title = title;
        AutomationProperties.SetName(this, title);
        Heading.Text = title;
        Body.Text = message;

        (string icon, string fg, string bg, string nameKey) = kind switch
        {
            MessageKind.Warning => ("IconWarning", "WarningBrush", "WarningTintBrush", "Dialog.Kind.Warning"),
            MessageKind.Error => ("IconError", "ErrorBrush", "ErrorTintBrush", "Dialog.Kind.Error"),
            MessageKind.Question => ("IconQuestion", "AccentBrush", "AccentTintBrush", "Dialog.Kind.Question"),
            _ => ("IconInfo", "AccentBrush", "AccentTintBrush", "Dialog.Kind.Info"),
        };
        BadgeGlyph.Data = (Geometry)FindResource(icon);
        BadgeGlyph.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, FindResource(fg));
        Badge.Background = (Brush)FindResource(bg);
        AutomationProperties.SetName(Badge, Loc.Get(nameKey));

        Button? focus = null;
        for (int i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            int index = i;
            var button = new Button { Content = c.Text, MinWidth = 96, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), IsDefault = c.IsDefault, IsCancel = c.IsCancel };
            if (c.IsPrimary) button.SetResourceReference(StyleProperty, "PrimaryButton");
            AutomationProperties.SetName(button, c.Text);
            button.Click += (_, _) => { ChosenIndex = index; DialogResult = true; };
            ButtonsPanel.Children.Add(button);
            if (c.IsCancel) _cancelIndex = i;
            if (c.IsDefault || (focus is null && c.IsPrimary)) focus = button;
        }
        focus ??= ButtonsPanel.Children.OfType<Button>().LastOrDefault();
        Loaded += (_, _) => focus?.Focus();
    }

    /// <summary>Index of the button that closed the dialog; the cancel button's index (or -1 when there is none) when it was closed otherwise.</summary>
    public int ChosenIndex { get; private set; } = -1;

    /// <inheritdoc />
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DialogResult != true) ChosenIndex = _cancelIndex;
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // A single-button message has no Cancel button, but Esc must still dismiss it.
        if (e.Key == Key.Escape && !e.Handled) { Close(); e.Handled = true; }
    }

    /// <summary>
    /// Shows a message box and returns the index of the pressed button (the cancel button's index when closed with Esc or the close button).
    /// The owner is the window the user is working in, so a message raised from the non-modal Data window opens over that window.
    /// </summary>
    public static int Show(MessageKind kind, string title, string message, params DialogChoice[] choices)
    {
        var window = new MessageDialogWindow(kind, title, message, choices);
        Window? owner = ResolveOwner();
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.ChosenIndex;
    }

    /// <summary>The active window, else the main window, but only when it is already shown (an unloaded owner would throw).</summary>
    public static Window? ResolveOwner()
    {
        var app = Application.Current;
        if (app is null) return null;
        Window? active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsLoaded);
        if (active is not null) return active;
        return app.MainWindow is { IsLoaded: true } main ? main : null;
    }
}
