using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;

namespace Rotula.App;

/// <summary>Application entry: wires the services, applies the saved language and opens the main window.</summary>
public partial class App : Application
{
    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rotula", "rotula.log");

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Tooltips appear after a short, comfortable pause (the attached property must be overridden once for all elements).
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(450));
        ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(12000));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        try
        {
            var services = new MainServices(
                new WpfFileDialogService(), new WpfMessageService(), new WpfClipboardService(), new WpfImageLoader(),
                new WpfNewLabelDialogService(), CreateZplImporter(), new Rotula.Printing.PrintService(), new JsonSettingsStore(), new WpfToolDialogService());
            var vm = new MainViewModel(services);
            var window = new MainWindow(vm);
            MainWindow = window;
            window.Show();

            // Double-clicking a .rotula file starts the program with the path as its only argument.
            if (e.Args.Length > 0 && File.Exists(e.Args[0])) vm.LoadFile(e.Args[0]);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show(Loc.Get("Crash.Startup", LogPath), "Rotula", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>The ZPL importer: the parser that ships in Rotula.Core.</summary>
    private static IZplImporter CreateZplImporter() => new CoreZplImporter();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        // Keep the program running: one failed command should not throw away the user's open label.
        MessageBox.Show(Loc.Get("Crash.Unexpected", LogPath), "Rotula", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception)
        {
            // Logging must never throw while we are already handling a failure.
        }
    }
}
