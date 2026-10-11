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
            // Add-ons load on a background thread while the window opens; a failure of the loader itself only ends up in the log.
            var packOptions = new Rotula.Data.Packs.PackLoadOptions();
            var packLoading = PackStartup.Begin(packOptions, LogCrash);
            // The first New label dialog should find the stock library already read.
            StockLibrary.Preload();
            var dialogs = new WpfToolDialogService();
            var services = new MainServices(
                new WpfFileDialogService(), new WpfMessageService(), new WpfClipboardService(), new WpfImageLoader(),
                new WpfNewLabelDialogService(), CreateZplImporter(), new Rotula.Printing.PrintService(), new JsonSettingsStore(), dialogs, new DatabaseServices(),
                // Serial numbers that remember their value live in %AppData%\Rotula\counters.json (see docs/variables.md).
                new VariableServices(Rotula.Files.FileCounterStore.OpenDefault(), dialogs));
            var vm = new MainViewModel(services);
            var window = new MainWindow(vm);
            MainWindow = window;
            window.Show();
            // When loading is over, tell the user (non-modally) about add-ons that could not be loaded or wait for approval.
            _ = ShowPackNoticeAsync(packLoading, packOptions, vm);

            // Double-clicking a .rotula file starts the program with the path as its only argument.
            if (e.Args.Length > 0 && File.Exists(e.Args[0])) vm.LoadFile(e.Args[0]);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            ShowCrashMessage(Loc.Get("Crash.Startup", LogPath));
            Shutdown(1);
        }
    }

    private async Task ShowPackNoticeAsync(Task<Rotula.Data.Packs.PackLoadReport> loading, Rotula.Data.Packs.PackLoadOptions options, MainViewModel vm)
    {
        try
        {
            var report = await loading;
            if (report.Warnings.Count > 0) vm.PackNotice = new PackNoticeViewModel(report, options, new WpfPackDialogService());
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    /// <summary>The ZPL importer: the parser that ships in Rotula.Core.</summary>
    private static IZplImporter CreateZplImporter() => new CoreZplImporter();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        // Keep the program running: one failed command should not throw away the user's open label.
        ShowCrashMessage(Loc.Get("Crash.Unexpected", LogPath));
        e.Handled = true;
    }

    /// <summary>
    /// Shows a failure with the styled dialog. If that cannot be shown (for example the theme itself failed to load, which is what a
    /// start-up crash may be), the plain Windows message box is the last resort so the user always sees the message.
    /// </summary>
    private static void ShowCrashMessage(string message)
    {
        try
        {
            Views.MessageDialogWindow.Show(Views.MessageKind.Error, "Rotula", message,
                new Views.DialogChoice(Loc.Get("Common.Ok"), IsPrimary: true, IsDefault: true, IsCancel: true));
        }
        catch (Exception)
        {
            MessageBox.Show(message, "Rotula", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
