using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Printing;
using Rotula.Rendering.View;

namespace Rotula.App.ViewModels;

/// <summary>An installed Windows printer as shown in the list.</summary>
/// <param name="Name">Queue name (what is stored and printed to).</param>
/// <param name="Display">Name plus notes such as "(default)" or "(offline)".</param>
public sealed record PrinterItem(string Name, string Display);

/// <summary>
/// Everything that says where and how to print: the Windows printer or the TCP host and port, plus the printer profile
/// (resolution, width, media tracking, darkness, speed). Shared by the Print dialog and the data merge window, and stored
/// in settings.json so the next session starts with the same printer.
/// </summary>
public sealed partial class PrinterSettingsViewModel : ObservableObject
{
    private readonly IPrintService _service;
    private readonly PrintSettings _settings;
    private readonly Action _save;
    private readonly Func<LabelDocument> _document;
    private IReadOnlyList<PrinterInfo> _printers = Array.Empty<PrinterInfo>();

    /// <summary>Creates the view-model.</summary>
    /// <param name="service">Printing service (used to list printers).</param>
    /// <param name="settings">The persisted settings object this view-model reads and updates.</param>
    /// <param name="save">Writes settings.json.</param>
    /// <param name="document">The open label; its size and resolution seed the profile the first time.</param>
    public PrinterSettingsViewModel(IPrintService service, PrintSettings settings, Action save, Func<LabelDocument> document)
    {
        _service = service;
        _settings = settings;
        _save = save;
        _document = document;

        DpiChoices = new[]
        {
            new Choice<PrinterDpi>(PrinterDpi.Dpi203, "Enum.PrinterDpi.Dpi203"),
            new Choice<PrinterDpi>(PrinterDpi.Dpi300, "Enum.PrinterDpi.Dpi300"),
            new Choice<PrinterDpi>(PrinterDpi.Dpi600, "Enum.PrinterDpi.Dpi600"),
        };
        MediaChoices = new[]
        {
            new Choice<MediaTracking>(MediaTracking.Web, "Enum.MediaTracking.Web"),
            new Choice<MediaTracking>(MediaTracking.Continuous, "Enum.MediaTracking.Continuous"),
            new Choice<MediaTracking>(MediaTracking.Mark, "Enum.MediaTracking.Mark"),
        };
        LoadFromSettings();
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            foreach (var c in DpiChoices) c.Refresh();
            foreach (var c in MediaChoices) c.Refresh();
            RebuildPrinterDisplay();
            NotifyToolbar();
        };
    }

    /// <summary>Resolution choices.</summary>
    public IReadOnlyList<Choice<PrinterDpi>> DpiChoices { get; }

    /// <summary>Media tracking choices.</summary>
    public IReadOnlyList<Choice<MediaTracking>> MediaChoices { get; }

    /// <summary>Installed Windows printers (filled by <see cref="RefreshPrintersAsync"/>).</summary>
    public ObservableCollection<PrinterItem> Printers { get; } = new();

    /// <summary>True to print to a network printer by address; false to use an installed Windows printer.</summary>
    [ObservableProperty]
    private bool _useNetwork;

    partial void OnUseNetworkChanged(bool value)
    {
        OnPropertyChanged(nameof(UseWindowsPrinter));
        NotifyToolbar();
    }

    partial void OnSelectedPrinterNameChanged(string? value) => NotifyToolbar();
    partial void OnHostChanged(string value) => NotifyToolbar();
    partial void OnPortTextChanged(string value) => NotifyToolbar();

    private void NotifyToolbar()
    {
        OnPropertyChanged(nameof(ToolbarText));
        OnPropertyChanged(nameof(ShowToolbarText));
    }

    /// <summary>
    /// Text laid over the toolbar printer drop-down when it cannot show the choice itself: a hint when nothing is chosen, or the
    /// address of a network printer.
    /// </summary>
    public string ToolbarText => UseNetwork && !string.IsNullOrWhiteSpace(Host) ? $"{Host.Trim()}:{PortText.Trim()}" : Loc.Get("Toolbar.SelectPrinter");

    /// <summary>True when <see cref="ToolbarText"/> should be shown over the drop-down.</summary>
    public bool ShowToolbarText => UseNetwork || string.IsNullOrEmpty(SelectedPrinterName);

    /// <summary>The opposite of <see cref="UseNetwork"/>; lets the radio buttons bind without a converter.</summary>
    public bool UseWindowsPrinter
    {
        get => !UseNetwork;
        set => UseNetwork = !value;
    }

    /// <summary>Queue name of the selected Windows printer.</summary>
    [ObservableProperty]
    private string? _selectedPrinterName;

    /// <summary>Network printer host name or IP address.</summary>
    [ObservableProperty]
    private string _host = string.Empty;

    /// <summary>Network printer port as typed.</summary>
    [ObservableProperty]
    private string _portText = "9100";

    /// <summary>Selected resolution.</summary>
    [ObservableProperty]
    private PrinterDpi _selectedDpi = PrinterDpi.Dpi203;

    /// <summary>Label width in millimetres as typed.</summary>
    [ObservableProperty]
    private string _widthText = "100";

    /// <summary>Label height in millimetres as typed.</summary>
    [ObservableProperty]
    private string _heightText = "50";

    /// <summary>Selected media tracking.</summary>
    [ObservableProperty]
    private MediaTracking _selectedMedia = MediaTracking.Web;

    /// <summary>Darkness (0-30) as typed; empty keeps the setting stored in the printer.</summary>
    [ObservableProperty]
    private string _darknessText = string.Empty;

    /// <summary>Speed (1-14 inches per second) as typed; empty keeps the setting stored in the printer.</summary>
    [ObservableProperty]
    private string _speedText = string.Empty;

    /// <summary>True while the printer list is being read.</summary>
    [ObservableProperty]
    private bool _isLoadingPrinters;

    /// <summary>Note about the printer list (none installed, last printer missing).</summary>
    [ObservableProperty]
    private string _printersNote = string.Empty;

    /// <summary>Short description of the chosen printer for summaries, for example "Zebra ZD420" or "192.168.1.50:9100".</summary>
    public string Summary => UseNetwork ? $"{Host}:{PortText}" : (SelectedPrinterName ?? string.Empty);

    private void LoadFromSettings()
    {
        var s = _settings;
        if (!s.Configured)
        {
            // First run: start from the open label so width and resolution are sensible without typing anything.
            var doc = _document();
            SelectedDpi = doc.Dpi;
            WidthText = FormatMm(ToMm(doc.Width, doc.Dpi));
            HeightText = FormatMm(ToMm(doc.Height, doc.Dpi));
            SelectedMedia = doc.MediaTracking;
            return;
        }
        UseNetwork = string.Equals(s.Transport, "Tcp", StringComparison.OrdinalIgnoreCase);
        SelectedPrinterName = string.IsNullOrEmpty(s.PrinterName) ? null : s.PrinterName;
        Host = s.Host;
        PortText = s.Port.ToString(CultureInfo.InvariantCulture);
        SelectedDpi = Enum.TryParse<PrinterDpi>(s.Dpi, out var dpi) ? dpi : PrinterDpi.Dpi203;
        WidthText = FormatMm(s.WidthMm);
        HeightText = FormatMm(s.HeightMm);
        SelectedMedia = Enum.TryParse<MediaTracking>(s.MediaTracking, out var m) ? m : MediaTracking.Web;
        DarknessText = s.Darkness?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SpeedText = s.SpeedIps?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static double ToMm(Measure m, PrinterDpi dpi) => UnitConverter.Convert(m.Value, m.Unit, LengthUnit.Millimeters, (int)dpi);

    private static string FormatMm(double mm) => Math.Round(mm, 2).ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>Reads the installed printers without blocking the window and selects the saved (or default) one.</summary>
    public async Task RefreshPrintersAsync(CancellationToken cancellationToken = default)
    {
        IsLoadingPrinters = true;
        PrintersNote = string.Empty;
        try
        {
            IReadOnlyList<PrinterInfo> found;
            try { found = await _service.GetPrintersAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                found = Array.Empty<PrinterInfo>();
            }
            _printers = found;
            RebuildPrinterDisplay();
            if (found.Count == 0)
            {
                PrintersNote = Loc.Get("Print.Printers.None");
                return;
            }
            if (SelectedPrinterName is { } saved && !found.Any(p => p.Name == saved))
            {
                PrintersNote = Loc.Get("Print.Printers.Missing", saved);
                SelectedPrinterName = null;
            }
            if (SelectedPrinterName is null && !UseNetwork)
                SelectedPrinterName = (found.FirstOrDefault(p => p.IsDefault) ?? found[0]).Name;
        }
        finally { IsLoadingPrinters = false; }
    }

    private void RebuildPrinterDisplay()
    {
        var keep = SelectedPrinterName;
        Printers.Clear();
        foreach (var p in _printers)
        {
            string display = p.Name;
            if (p.IsDefault) display += " " + Loc.Get("Print.Printers.Default");
            if (p.IsOffline) display += " " + Loc.Get("Print.Printers.Offline");
            Printers.Add(new PrinterItem(p.Name, display));
        }
        SelectedPrinterName = keep;
    }

    /// <summary>Parses a number the way people type it (current culture first, then invariant).</summary>
    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Builds the print target from the fields.</summary>
    /// <param name="target">The target when the fields are fine.</param>
    /// <returns>Null when the fields are fine; otherwise a plain-language message saying what to fix.</returns>
    public string? TryBuildTarget(out PrinterTarget? target)
    {
        target = null;
        if (UseNetwork)
        {
            if (string.IsNullOrWhiteSpace(Host)) return Loc.Get("Print.Field.NoHost");
            if (!int.TryParse(PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
                return Loc.Get("Print.Field.Port");
            target = PrinterTarget.TcpIp(Host.Trim(), port);
            return null;
        }
        if (string.IsNullOrWhiteSpace(SelectedPrinterName)) return Loc.Get("Print.Field.NoPrinter");
        target = PrinterTarget.Spooler(SelectedPrinterName);
        return null;
    }

    /// <summary>Builds and validates the printer profile from the fields.</summary>
    /// <param name="profile">The profile when valid.</param>
    /// <param name="encoding">Character set used by the test label.</param>
    /// <returns>Null when valid; otherwise a plain-language message.</returns>
    public string? TryBuildProfile(out PrinterProfile? profile, ZplEncoding? encoding = null)
    {
        profile = null;
        if (!TryParseDouble(WidthText, out double w)) return Loc.Get("Print.Field.Number", Loc.Get("Print.Profile.Width"));
        if (!TryParseDouble(HeightText, out double h)) return Loc.Get("Print.Field.Number", Loc.Get("Print.Profile.Height"));
        int? darkness = null, speed = null;
        if (!string.IsNullOrWhiteSpace(DarknessText))
        {
            if (!int.TryParse(DarknessText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int d))
                return Loc.Get("Print.Field.Number", Loc.Get("Print.Profile.Darkness"));
            darkness = d;
        }
        if (!string.IsNullOrWhiteSpace(SpeedText))
        {
            if (!int.TryParse(SpeedText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int sp))
                return Loc.Get("Print.Field.Number", Loc.Get("Print.Profile.Speed"));
            speed = sp;
        }
        var candidate = new PrinterProfile
        {
            Dpi = SelectedDpi,
            Width = Measure.Millimeters(w),
            Height = Measure.Millimeters(h),
            MediaTracking = SelectedMedia,
            Darkness = darkness,
            SpeedIps = speed,
            Encoding = encoding ?? ZplEncoding.Utf8,
        };
        var bad = candidate.Validate();
        if (bad.Count > 0) return string.Join(" ", bad.Select(k => Loc.Get(k)));
        profile = candidate;
        return null;
    }

    /// <summary>Stores the current choices in settings.json so the next session starts with them. Invalid fields keep their old stored value.</summary>
    public void Persist()
    {
        _settings.Configured = true;
        _settings.Transport = UseNetwork ? "Tcp" : "Spooler";
        _settings.PrinterName = SelectedPrinterName ?? _settings.PrinterName;
        _settings.Host = Host.Trim();
        if (int.TryParse(PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1 and <= 65535) _settings.Port = port;
        _settings.Dpi = SelectedDpi.ToString();
        if (TryParseDouble(WidthText, out double w) && w > 0) _settings.WidthMm = w;
        if (TryParseDouble(HeightText, out double h) && h > 0) _settings.HeightMm = h;
        _settings.MediaTracking = SelectedMedia.ToString();
        _settings.Darkness = int.TryParse(DarknessText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int d) ? d : null;
        _settings.SpeedIps = int.TryParse(SpeedText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int sp) ? sp : null;
        _save();
    }
}
