using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rotula.App.Views;

/// <summary>
/// The application icon as a WPF image source, shared by every window (title bar, taskbar, Alt-Tab). It is the same multi-size
/// ICO that is embedded in the executable, so WPF picks the hand-tuned 16 / 24 / 32 px frames itself.
/// </summary>
public static class BrandIcon
{
    // The assembly-qualified pack URI also resolves when the test host, not the app, is the entry assembly. It is built lazily, inside the
    // try of the getter, so a missing pack scheme can never fail the type initializer of every window.
    private const string IconPack = "pack://application:,,,/Rotula.App;component/Assets/rotula.ico";

    private static ImageSource? _icon;

    /// <summary>The frozen icon, decoded once. Returns null only if the resource cannot be read, in which case windows simply keep the default icon.</summary>
    public static ImageSource? Source
    {
        get
        {
            if (_icon is not null) return _icon;
            try
            {
                var frame = BitmapFrame.Create(new Uri(IconPack, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                _icon = frame;
            }
            catch (Exception)
            {
                // A missing icon must never stop a window from opening.
                _icon = null;
            }
            return _icon;
        }
    }
}
