using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Rotula.App.Localization;

namespace Rotula.App.Views;

/// <summary>The About box: name, version, what the program is, the project link, the preview note and the third-party notices.</summary>
public partial class AboutWindow : DialogWindow
{
    /// <summary>Name of the embedded copy of THIRD-PARTY-NOTICES.md (see Rotula.App.csproj).</summary>
    public const string NoticesResourceName = "Rotula.App.THIRD-PARTY-NOTICES.md";

    /// <summary>Address of the project page.</summary>
    public const string ProjectUrl = "https://github.com/P4Software/Rotula";

    /// <summary>Creates the dialog.</summary>
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = Loc.Get("About.Version", VersionString());
    }

    /// <summary>The program version from the assembly (the informational version without the build metadata after "+", else the file version).</summary>
    public static string VersionString()
    {
        var asm = typeof(App).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    /// <summary>Reads the embedded third-party notices; null when this build does not contain them.</summary>
    public static string? ReadNotices()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(NoticesResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnNotices(object sender, RoutedEventArgs e) => new NoticesWindow { Owner = this }.ShowDialog();

    private void OnLink(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute hands the address to the default browser; there is no network code in the program itself.
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageDialogWindow.Show(MessageKind.Info, Loc.Get("About.Title"), Loc.Get("About.Link.Failed", ProjectUrl),
                new DialogChoice(Loc.Get("Common.Ok"), IsPrimary: true, IsDefault: true, IsCancel: true));
        }
    }
}
