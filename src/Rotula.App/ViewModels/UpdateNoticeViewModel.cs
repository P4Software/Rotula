using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.App.Services;

namespace Rotula.App.ViewModels;

/// <summary>
/// The non-modal "a newer version is available" strip under the toolbar. It only links to the release page; Rotula never
/// downloads or installs anything itself.
/// </summary>
public sealed partial class UpdateNoticeViewModel : ObservableObject
{
    private readonly Func<string, bool> _open;
    private readonly Action<string> _dismissed;
    private readonly Action<string> _status;

    /// <summary>Creates the notice.</summary>
    /// <param name="release">The newer release.</param>
    /// <param name="open">Opens a web address; returns false when that failed.</param>
    /// <param name="dismissed">Called with the version when the user closes the notice (so it is not shown again for that version).</param>
    /// <param name="status">Shows a sentence in the status bar.</param>
    public UpdateNoticeViewModel(ReleaseInfo release, Func<string, bool> open, Action<string> dismissed, Action<string> status)
    {
        Release = release ?? throw new ArgumentNullException(nameof(release));
        _open = open;
        _dismissed = dismissed;
        _status = status;
        Loc.Instance.SubscribeWeak(this, static self => self.OnPropertyChanged(nameof(NoticeText)));
    }

    /// <summary>The newer release.</summary>
    public ReleaseInfo Release { get; }

    /// <summary>The sentence of the notice, in the current language.</summary>
    public string NoticeText => Loc.Get("Update.Notice", Release.Version.ToString());

    /// <summary>False once the user closed the notice.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Opens the release page in the browser.</summary>
    [RelayCommand]
    private void OpenRelease()
    {
        if (!_open(Release.PageUrl)) _status(Loc.Get("Update.OpenFailed", Release.PageUrl));
    }

    /// <summary>Hides the notice and remembers that this version was seen.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        IsVisible = false;
        _dismissed(Release.Version.ToString());
    }
}
