using System.Windows;
using Rotula.App.Localization;

namespace Rotula.App.Views;

/// <summary>Shows the bundled THIRD-PARTY-NOTICES.md text.</summary>
public partial class NoticesWindow : DialogWindow
{
    /// <summary>Creates the window and loads the embedded notices.</summary>
    public NoticesWindow()
    {
        InitializeComponent();
        Notices.Text = AboutWindow.ReadNotices() ?? Loc.Get("About.Notices.Missing");
        Loaded += (_, _) => Notices.Focus();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
