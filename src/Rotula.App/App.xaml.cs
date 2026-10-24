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
        // Measuring and drawing failures are survivable (the editor keeps the last size / shows "preview unavailable"); log the first one only.
        Editing.ElementBoundsCalculator.ProbeFailed += LogCrash;
        Editing.PreviewCache.RenderFailed += LogCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        var launch = StationLaunch.Parse(e.Args);
        if (launch.Mode != LaunchMode.Designer)
        {
            StartStation(launch);
            return;
        }

        try
        {
            // What the organization fixed through Group Policy or Intune (HKLM wins over HKCU; docs/deployment.md). Read once.
            var policy = Rotula.Files.Policy.RotulaPolicy.Read(Rotula.Files.Policy.RegistryPolicySource.ForThisComputer());
            // Add-ons load on a background thread while the window opens; a failure of the loader itself only ends up in the log.
            // A policy can keep add-ons in the user's own folder from loading (the installed "packs" folder is still used).
            var packOptions = new Rotula.Data.Packs.PackLoadOptions { IncludeUserPacks = policy.AllowUserAddOns };
            var packLoading = PackStartup.Begin(packOptions, LogCrash);
            // The first New label dialog should find the stock library already read.
            StockLibrary.Preload();
            var dialogs = new WpfToolDialogService();
            var services = new MainServices(
                new WpfFileDialogService(), new WpfMessageService(), new WpfClipboardService(), new WpfImageLoader(),
                new WpfNewLabelDialogService(), CreateZplImporter(), new Rotula.Printing.PrintService(), new JsonSettingsStore(), dialogs, new DatabaseServices(),
                // Serial numbers that remember their value live in %AppData%\Rotula\counters.json (see docs/variables.md).
                // Windows-font text is drawn to a picture by the SkiaSharp rasterizer, so Print, Save ZPL, Check All and Export can all print it.
                new VariableServices(Rotula.Files.FileCounterStore.OpenDefault(), dialogs, textRasterizer: new Rotula.Rendering.SkiaTextRasterizer()),
                Policy: policy,
                // The opt-in update check: nothing is sent unless the user (Help menu) or a policy switches it on.
                Updates: new UpdateChecker(new GitHubReleaseFeed(), Views.AboutWindow.VersionString()),
                Links: new ShellUrlLauncher());
            var vm = new MainViewModel(services);
            var window = new MainWindow(vm);
            MainWindow = window;
            window.Show();
            // When loading is over, tell the user (non-modally) about add-ons that could not be loaded or wait for approval.
            _ = ShowPackNoticeAsync(packLoading, packOptions, vm);
            _ = CheckForUpdatesAsync(vm);

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

    /// <summary>
    /// Starts the print station (or its PIN tool) instead of the designer. The station uses the same printing service, counter memory and saved
    /// language as the designer; a configuration that cannot be used is explained and the program ends.
    /// </summary>
    private void StartStation(StationLaunch launch)
    {
        try
        {
            var settings = new JsonSettingsStore().Load();
            Loc.Instance.SetLanguage(settings.Language);
            if (launch.Mode == LaunchMode.StationPin)
            {
                var pinWindow = new Views.StationPinWindow(new StationPinViewModel(new WpfClipboardService())) { ShowInTaskbar = true };
                MainWindow = pinWindow;
                pinWindow.Show();
                return;
            }
            var load = Rotula.Files.Station.StationConfigLoader.Load(launch.Path);
            if (!load.CanOpen)
            {
                ShowCrashMessage(Loc.Get("Station.Open.Failed") + Environment.NewLine + Environment.NewLine + StationViewModel.DescribeIssues(load.Issues));
                Shutdown(2);
                return;
            }
            // Labels whose data comes from an add-on database need the add-ons; they load in the background as in the designer.
            PackStartup.Begin(new Rotula.Data.Packs.PackLoadOptions(), LogCrash);
            var variables = new VariableServices(Rotula.Files.FileCounterStore.OpenDefault(), null, textRasterizer: new Rotula.Rendering.SkiaTextRasterizer());
            var station = new StationViewModel(load.Config!, load.Warnings,
                new StationServices(new Rotula.Printing.PrintService(), variables, new WpfMessageService(), settings.Print, new DatabaseServices()));
            var window = new Views.StationWindow(station);
            MainWindow = window;
            window.Show();
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

    private static async Task CheckForUpdatesAsync(MainViewModel vm)
    {
        try
        {
            await vm.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            // The update check is a convenience; it must never disturb the user.
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
