using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Rotula.App.Views;

/// <summary>
/// Base class of the secondary windows (New label, Print, Data merge, Preview). It gives them the shared warm title bar
/// (the "DialogWindow" style in Theme.xaml) with working caption buttons, keeps them inside the work area of the screen the main
/// window is on (so nothing is cut off on a 1280 x 800 screen or at 150 % / 200 % scaling) and centres them on the window that opened them.
/// </summary>
public class DialogWindow : Window
{
    /// <summary>Share of the work area a dialog may occupy at most; leaves a margin so the title bar is never under the taskbar.</summary>
    private const double MaxShare = 0.96;

    /// <summary>Creates the window with the shared style.</summary>
    public DialogWindow()
    {
        SetResourceReference(StyleProperty, "DialogWindow");
        // WindowStyle=None removes the system caption buttons, so the template draws its own; a Window handles SystemCommands only when asked to.
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        Loaded += (_, _) => PlaceOnOwner();
        // A window can grow after it is shown (printer detection adds a note, a list gets longer): keep its bottom edge on the screen.
        SizeChanged += (_, _) => { if (IsLoaded && WindowState == WindowState.Normal) KeepInsideWorkArea(); };
    }

    private void KeepInsideWorkArea()
    {
        var area = WorkAreaInDips(Owner ?? this);
        double top = Math.Max(area.Top, Math.Min(Top, area.Bottom - ActualHeight));
        double left = Math.Max(area.Left, Math.Min(Left, area.Right - ActualWidth));
        if (Math.Abs(top - Top) > 0.5) Top = top;
        if (Math.Abs(left - Left) > 0.5) Left = left;
    }

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Owner is assigned after construction, so the screen to fit is only known here (the window handle exists now).
        var area = WorkAreaInDips(Owner ?? this);
        double maxW = area.Width * MaxShare, maxH = area.Height * MaxShare;
        // Dialogs scroll their content, so a smaller window is always usable, while one taller than the screen is not.
        if (!double.IsNaN(Width)) Width = Math.Min(Width, maxW);
        if (!double.IsNaN(Height)) Height = Math.Min(Height, maxH);
        MinWidth = Math.Min(MinWidth, maxW);
        MinHeight = Math.Min(MinHeight, maxH);
        // A size-to-content window (New label) is capped; a window with a fixed size must stay free to maximise.
        if (SizeToContent != SizeToContent.Manual) MaxHeight = Math.Min(MaxHeight, maxH);
    }

    private void PlaceOnOwner()
    {
        var area = WorkAreaInDips(Owner ?? this);
        double left = Left, top = Top;
        if (Owner is { WindowState: not WindowState.Minimized } owner && owner.ActualWidth > 0)
        {
            // Centre on the main window (WPF's CenterOwner can't know the final height of a SizeToContent window).
            bool max = owner.WindowState == WindowState.Maximized;
            double ownerLeft = max ? area.Left : owner.Left, ownerTop = max ? area.Top : owner.Top;
            double ownerWidth = max ? area.Width : owner.ActualWidth, ownerHeight = max ? area.Height : owner.ActualHeight;
            left = ownerLeft + (ownerWidth - ActualWidth) / 2;
            top = ownerTop + (ownerHeight - ActualHeight) / 2;
        }
        Left = Math.Max(area.Left, Math.Min(left, area.Right - ActualWidth));
        Top = Math.Max(area.Top, Math.Min(top, area.Bottom - ActualHeight));
    }

    // ------------------------------------------------------------------------------- work area of the right monitor

    /// <summary>
    /// The work area (screen minus taskbar) of the monitor <paramref name="window"/> is on, in WPF units. SystemParameters.WorkArea
    /// is always the primary monitor, which is wrong when the main window sits on a second screen.
    /// </summary>
    private static Rect WorkAreaInDips(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                {
                    DpiScale dpi = VisualTreeHelper.GetDpi(window);
                    return new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY,
                        (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // Fall through to the primary screen.
        }
        return SystemParameters.WorkArea;
    }

    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
