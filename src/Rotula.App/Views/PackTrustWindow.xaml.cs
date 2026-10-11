using System.Windows;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Data.Packs;

namespace Rotula.App.Views;

/// <summary>
/// The approval prompt for an add-on in the user's own folder. It says plainly that an add-on is a program, shows who made it and
/// its fingerprint, and defaults to Skip.
/// </summary>
public partial class PackTrustWindow : DialogWindow
{
    /// <summary>Creates the prompt.</summary>
    public PackTrustWindow(PackTrustPrompt prompt)
    {
        InitializeComponent();
        var m = prompt.Candidate.Manifest;
        WarningText.Text = PackMessages.DescribeTrust(prompt);
        string unknown = Loc.Get("Pack.Trust.Unknown");
        PublisherText.Text = string.IsNullOrWhiteSpace(m?.Publisher) ? unknown : m!.Publisher;
        VersionText.Text = string.IsNullOrWhiteSpace(m?.Version) ? unknown : m!.Version;
        FingerprintText.Text = prompt.ShortFingerprint;
        FolderText.Text = prompt.Candidate.Folder;
        Loaded += (_, _) => SkipButton.Focus();
    }

    private void OnApprove(object sender, RoutedEventArgs e) => DialogResult = true;
}
