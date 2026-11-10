using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Model;
using Rotula.Core.Validation;
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
    private readonly Func<string, CancellationToken, Task<PrinterDetection>> _detect;
    private readonly Action<PrinterDpi>? _applyLabelDpi;
    private CancellationTokenSource? _detectCts;
    private PrinterDetection _detected = PrinterDetection.None;

    /// <summary>The resolution the driver of the selected printer reported, or null when nothing was read (not asked yet, failed, or a network printer).</summary>
    public PrinterDpi? DetectedDpi => _detected.Dpi;
    private IReadOnlyList<PrinterInfo> _printers = Array.Empty<PrinterInfo>();
    private readonly string? _defaultPrinter;

    /// <summary>Creates the view-model.</summary>
    /// <param name="service">Printing service (used to list printers).</param>
    /// <param name="settings">The persisted settings object this view-model reads and updates.</param>
    /// <param name="save">Writes settings.json.</param>
    /// <param name="document">The open label; its size and resolution seed the profile the first time.</param>
    /// <param name="detect">Reads the driver resolution and paper size of a printer; null uses <see cref="PrinterDetector"/> (tests pass a fake).</param>
    /// <param name="applyLabelDpi">Switches the open label to a resolution (one undo step); null disables the "Switch label" action.</param>
    /// <param name="defaultPrinter">
    /// A Windows printer chosen by the organization's policy, preselected until the user has picked a printer once; null for none.
    /// </param>
    public PrinterSettingsViewModel(IPrintService service, PrintSettings settings, Action save, Func<LabelDocument> document,
        Func<string, CancellationToken, Task<PrinterDetection>>? detect = null, Action<PrinterDpi>? applyLabelDpi = null,
        string? defaultPrinter = null)
    {
        _defaultPrinter = string.IsNullOrWhiteSpace(defaultPrinter) ? null : defaultPrinter.Trim();
        _detect = detect ?? ((name, ct) => PrinterDetector.DetectAsync(service, name, ct));
        _applyLabelDpi = applyLabelDpi;
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
        Loc.Instance.SubscribeWeak(this, static self =>
        {
            foreach (var c in self.DpiChoices) c.Refresh();
            foreach (var c in self.MediaChoices) c.Refresh();
            self.RebuildPrinterDisplay();
            self.NotifyToolbar();
        });
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
        OnPropertyChanged(nameof(CheckStatus));
        ClearStatus();
        NotifyToolbar();
        // A network printer is always ZPL, a Windows printer has its own saved mode: switching transport can switch the mode.
        NotifyMode();
        RefreshDpiMismatch();
    }

    partial void OnSelectedDpiChanged(PrinterDpi value) => RefreshDpiMismatch();
    partial void OnIsDetectingChanged(bool value) => RefreshDpiMismatch();

    partial void OnSelectedPrinterNameChanged(string? value)
    {
        NotifyToolbar();
        ClearStatus();
        // Each printer has its own saved mode (or none yet, which brings the question); DetectAsync refreshes the resolution note afterwards.
        NotifyMode();
        _ = DetectAsync();
    }
    partial void OnHostChanged(string value)
    {
        NotifyToolbar();
        ClearStatus();
        RefreshDpiMismatch();
    }
    partial void OnPortTextChanged(string value) => NotifyToolbar();

    private void NotifyToolbar()
    {
        OnPropertyChanged(nameof(PrinterChosen));
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
        // Only the radio that becomes checked may change the choice. The group also writes "false" back into the radio that just got
        // unchecked; honouring that mirrored write made the two bound properties flip each other endlessly (a stack overflow seen when
        // the radio buttons were driven by UI Automation).
        set { if (value) UseNetwork = false; }
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

    // ------------------------------------------------------------------------------- printer status (~HS)

    private bool _statusCheckNetwork = true;
    private bool _statusCheckWindows;
    private CancellationTokenSource? _statusCts;

    /// <summary>
    /// True to ask the printer for its status before and after printing. Kept separately for network and Windows printers: on by default
    /// for a network printer, off for a Windows printer (many USB drivers cannot answer).
    /// </summary>
    public bool CheckStatus
    {
        get => UseNetwork ? _statusCheckNetwork : _statusCheckWindows;
        set
        {
            if (CheckStatus == value) return;
            if (UseNetwork) _statusCheckNetwork = value; else _statusCheckWindows = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True while "Check printer status" waits for the answer.</summary>
    [ObservableProperty]
    private bool _isCheckingStatus;

    /// <summary>The decoded status in plain language, or empty before the first check.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText), nameof(HasStatusOk), nameof(HasStatusProblem))]
    private string _statusText = string.Empty;

    /// <summary>True when <see cref="StatusText"/> reports a problem (the printer cannot print, or its status is unknown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusOk), nameof(HasStatusProblem))]
    private bool _statusIsProblem;

    /// <summary>True when there is a status result to show.</summary>
    public bool HasStatusText => StatusText.Length > 0;

    /// <summary>True when the status result is good news (drives the green note).</summary>
    public bool HasStatusOk => HasStatusText && !StatusIsProblem;

    /// <summary>True when the status result is a problem (drives the amber note).</summary>
    public bool HasStatusProblem => HasStatusText && StatusIsProblem;

    /// <summary>
    /// True when "Check printer status" may run: not in Windows-driver (picture) mode, where <c>~HS</c>, a ZPL command, would be printed as text.
    /// The view hides the button in that mode as well; the command is off on its own so a key binding or UI automation cannot send it either.
    /// </summary>
    public bool CanCheckPrinterStatus => !IsDriverMode;

    /// <summary>Asks the chosen printer for its status (<c>~HS</c>) and shows the answer. A second click while waiting starts over.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckPrinterStatus))]
    private async Task CheckPrinterStatusAsync()
    {
        // ExecuteAsync does not consult CanExecute, so the rule is enforced here too: in picture mode nothing is asked and nothing is shown.
        if (!CanCheckPrinterStatus) return;
        string? error = TryBuildTarget(out var target);
        if (error is not null)
        {
            StatusIsProblem = true;
            StatusText = error;
            return;
        }
        var previous = _statusCts;
        previous?.Cancel();
        var cts = _statusCts = new CancellationTokenSource();
        IsCheckingStatus = true;
        StatusText = string.Empty;
        try
        {
            var status = await _service.GetStatusAsync(target!, cts.Token);
            if (cts.IsCancellationRequested) return;
            (string text, bool problem) = PrintMessages.DescribeStatus(status);
            StatusIsProblem = problem;
            StatusText = text;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_statusCts, cts))
            {
                _statusCts = null;
                IsCheckingStatus = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>Forgets a shown status when another printer is chosen, so an old answer is never read as the new printer's.</summary>
    private void ClearStatus()
    {
        _statusCts?.Cancel();
        StatusText = string.Empty;
        StatusIsProblem = false;
    }

    // ------------------------------------------------------------------------------- print mode (Printer language or Windows driver)

    /// <summary>
    /// How the selected printer gets the label. A network printer is always <see cref="PrinterOutputMode.PrinterLanguage"/>: it has no Windows driver to
    /// draw through. A Windows printer uses the mode saved for it. A Windows printer with no saved mode also prints as
    /// <see cref="PrinterOutputMode.PrinterLanguage"/>, as every printer did before driver printing existed. A suggestion never switches a printer on its
    /// own; the person answers <see cref="ModeQuestion"/> first (see <see cref="NeedsModeQuestion"/>).
    /// Setting it stores the choice in the map for the selected Windows printer only. Other printers keep their own choice. Persist, or any save
    /// of the settings, writes it to settings.json. It is ignored for a network printer or when no printer is selected.
    /// </summary>
    public PrinterOutputMode Mode
    {
        get => KnownMode() ?? PrinterOutputMode.PrinterLanguage;
        set
        {
            if (StoreMode(value)) RefreshDpiMismatch();
            // Raised even when nothing was stored, so a bound control that offered a refused value snaps back to the real one.
            NotifyMode();
        }
    }

    /// <summary>True when the mode of the selected printer is settled: a network printer (always ZPL) or a Windows printer with a saved mode.</summary>
    public bool ModeKnown => KnownMode() is not null;

    /// <summary>
    /// The mode to pre-select in the question, read from the names Windows reports for the selected printer (queue, driver and port, through
    /// <see cref="PrinterOutputModeSuggestion"/>). Before the printer list is read only the queue name is known. Only a suggestion: it never becomes
    /// <see cref="Mode"/> until the person chooses it. Always <see cref="PrinterOutputMode.PrinterLanguage"/> for a network printer.
    /// </summary>
    public PrinterOutputMode SuggestedMode
    {
        get
        {
            string? name = SelectedPrinterName;
            if (UseNetwork || string.IsNullOrWhiteSpace(name)) return PrinterOutputMode.PrinterLanguage;
            var info = _printers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            return info is null
                ? PrinterOutputModeSuggestion.For(name, string.Empty, string.Empty)
                : PrinterOutputModeSuggestion.For(info.Name, info.DriverName, info.PortName);
        }
    }

    /// <summary>
    /// True when <see cref="ModeQuestion"/> must be answered before anything is printed (the Print dialog, and batch printing from the Data
    /// window or the Print dialog): a Windows printer is selected and has no saved mode. That is a printer never used with Rotula, a new
    /// installation, or a printer added since. It is also a printer configured in this version whose question was never answered, for example
    /// one only chosen in a window and never printed to. A printer configured by an earlier version is recorded as ZPL when the settings are
    /// loaded, so its user is never asked. A network printer is never asked about.
    /// </summary>
    public bool NeedsModeQuestion => !UseNetwork && !string.IsNullOrWhiteSpace(SelectedPrinterName) && !ModeKnown;

    /// <summary>
    /// Why nothing may be printed yet while <see cref="NeedsModeQuestion"/> is true, in plain words: answer the question shown under the printer
    /// name, or, for a queue that is not installed (no question is shown for it), refresh the list or choose another printer. Empty when the
    /// mode is settled. The Print dialog and batch printing say the same sentence, so the person reads one instruction wherever they print.
    /// </summary>
    public string PendingModeText()
    {
        if (!NeedsModeQuestion) return string.Empty;
        return ShowModeQuestion
            ? Loc.Get("Print.Mode.AnswerFirst")
            : Loc.Get("Print.Mode.PrinterNotListed", SelectedPrinterName ?? string.Empty);
    }

    /// <summary>True when the label is drawn as a picture and printed through the Windows driver (never for a network printer).</summary>
    public bool IsDriverMode => Mode == PrinterOutputMode.WindowsDriver;

    /// <summary>The one-time question for a printer with no saved mode ("How does this printer understand labels?").</summary>
    public string ModeQuestion => Loc.Get("Print.Mode.Question");

    /// <summary>One sentence that names <see cref="SuggestedMode"/> and why it is suggested, shown with the question.</summary>
    public string ModeSuggestionText => Loc.Get("Print.Mode.Suggested", ModeName(SuggestedMode));

    /// <summary>One plain sentence for the Print dialog: which mode is in use and why.</summary>
    public string ModeExplanation => Loc.Get(IsDriverMode ? "Print.Mode.Explain.WindowsDriver" : "Print.Mode.Explain.PrinterLanguage");

    /// <summary>
    /// True when the selected Windows printer is in the list Windows reported (<see cref="Printers"/>). False before the list was read, when
    /// discovery failed, or for a queue remembered from another session that is no longer installed.
    /// </summary>
    public bool IsSelectedPrinterListed =>
        !string.IsNullOrWhiteSpace(SelectedPrinterName)
        && _printers.Any(p => string.Equals(p.Name, SelectedPrinterName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the Print dialog shows <see cref="ModeQuestion"/> with its two answers: the printer has no saved mode and is really installed.
    /// For a queue that is not in the list the question would be about a printer Rotula cannot reach, so it stays hidden and printing says
    /// to refresh the list or choose another printer instead.
    /// </summary>
    public bool ShowModeQuestion => NeedsModeQuestion && IsSelectedPrinterListed;

    /// <summary>
    /// True when the Print dialog shows the mode line (<see cref="ModeExplanation"/> and the control that switches to the other mode): a Windows
    /// printer whose mode is settled. Never for a network printer: it is always printer language, so there is nothing to change.
    /// </summary>
    public bool ShowModeLine => !UseNetwork && !string.IsNullOrWhiteSpace(SelectedPrinterName) && ModeKnown;

    /// <summary>True when <see cref="SuggestedMode"/> is the Windows driver; the question then shows that answer as the main button.</summary>
    public bool SuggestsDriver => SuggestedMode == PrinterOutputMode.WindowsDriver;

    /// <summary>
    /// The answer to <see cref="ModeQuestion"/>: stores <paramref name="mode"/> for the selected Windows printer and saves settings.json at
    /// once. Saving at once means that closing the dialog without printing does not bring the question back next time. Ignored for a network
    /// printer. The command is typed (<c>IRelayCommand&lt;PrinterOutputMode&gt;</c>), so the view passes <c>{x:Static}</c> values: a plain string
    /// parameter is refused.
    /// </summary>
    [RelayCommand]
    private void ChooseMode(PrinterOutputMode mode)
    {
        bool stored = StoreMode(mode);
        NotifyMode();
        if (!stored) return;
        RefreshDpiMismatch();
        _save();
    }

    /// <summary>The settled mode of the selection: ZPL for a network printer, the saved mode of a Windows printer, or null when none is saved.</summary>
    private PrinterOutputMode? KnownMode() => UseNetwork ? PrinterOutputMode.PrinterLanguage : SavedMode(SelectedPrinterName);

    /// <summary>The valid mode saved for <paramref name="printerName"/>, or null when there is none.</summary>
    private PrinterOutputMode? SavedMode(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName) || !_settings.PrintModes.TryGetValue(printerName, out string? text)) return null;
        return ParseMode(text);
    }

    /// <summary>
    /// Reads a stored mode. Only the two names count (any letter case, for a hand-edited file). Enum.TryParse is not used: it also accepts
    /// digits and comma lists, so "1" would pick the Windows driver nobody chose and "7" would give no mode at all. Anything else reads as "not
    /// answered yet" instead of crashing.
    /// </summary>
    private static PrinterOutputMode? ParseMode(string? text)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        foreach (var mode in Enum.GetValues<PrinterOutputMode>())
            if (string.Equals(trimmed, mode.ToString(), StringComparison.OrdinalIgnoreCase)) return mode;
        return null;
    }

    /// <summary>Writes <paramref name="mode"/> into the map for the selected Windows printer. Returns false when nothing changed or nothing may be stored.</summary>
    private bool StoreMode(PrinterOutputMode mode)
    {
        string? name = SelectedPrinterName;
        // A network printer has no driver to draw through, so there is no choice to remember for it.
        if (UseNetwork || string.IsNullOrWhiteSpace(name)) return false;
        if (SavedMode(name) == mode) return false;
        _settings.PrintModes[name] = mode.ToString();
        return true;
    }

    /// <summary>The name shown for a mode: "Printer language (ZPL)" or "Windows driver (picture)".</summary>
    private static string ModeName(PrinterOutputMode mode) =>
        Loc.Get(mode == PrinterOutputMode.WindowsDriver ? "Print.Mode.WindowsDriver" : "Print.Mode.PrinterLanguage");

    /// <summary>
    /// Makes the saved map safe to use, through the same rule the settings store applies on load (see
    /// <see cref="PrintSettings.NormalizePrintModes"/>). The store has normally done it already, so this changes nothing then. It matters for
    /// settings built in memory, such as the print station's copy. A settings object whose map was never present stands for a file from before
    /// driver printing, and its configured printer is recorded as ZPL. A present but empty map is never seeded.
    /// </summary>
    private void NormalizeModeMap() => _settings.NormalizePrintModes();

    /// <summary>Raises the change of every mode-related property; they are all computed from the selection and the map.</summary>
    private void NotifyMode()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ModeKnown));
        OnPropertyChanged(nameof(SuggestedMode));
        OnPropertyChanged(nameof(NeedsModeQuestion));
        OnPropertyChanged(nameof(IsDriverMode));
        OnPropertyChanged(nameof(ModeQuestion));
        OnPropertyChanged(nameof(ModeSuggestionText));
        OnPropertyChanged(nameof(ModeExplanation));
        OnPropertyChanged(nameof(IsSelectedPrinterListed));
        OnPropertyChanged(nameof(ShowModeQuestion));
        OnPropertyChanged(nameof(ShowModeLine));
        OnPropertyChanged(nameof(SuggestsDriver));
        OnPropertyChanged(nameof(CanCheckPrinterStatus));
        // The status check is off in picture mode; a button bound to the command greys out (or back) with the mode.
        CheckPrinterStatusCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------------------- driver detection

    /// <summary>True while the driver is being read (the view shows a small progress bar).</summary>
    [ObservableProperty]
    private bool _isDetecting;

    /// <summary>Plain-language result under the resolution box ("Detected 300 dpi from the printer driver", or why nothing was detected).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDpiNote))]
    private string _dpiNote = string.Empty;

    /// <summary>True when <see cref="DpiNote"/> has text.</summary>
    public bool HasDpiNote => DpiNote.Length > 0;

    /// <summary>Text of the "Use printer size (4 x 6 in)" action, or empty when the driver reported no paper size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectedSize))]
    private string _detectedSizeText = string.Empty;

    /// <summary>True when the paper size of the printer is known and can be offered.</summary>
    public bool HasDetectedSize => DetectedSizeText.Length > 0;

    /// <summary>
    /// Informative note shown when the open label resolution differs from the printer one. Printing converts a copy of the label
    /// to the printer resolution, so this is not a warning about wrong output. Empty when the resolutions match or no printer is chosen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDpiMismatch))]
    private string _dpiMismatchText = string.Empty;

    /// <summary>True when the label and printer resolutions differ (the note is shown and a converted copy is printed).</summary>
    public bool HasDpiMismatch => DpiMismatchText.Length > 0;

    /// <summary>Text of the secondary action of the note ("Switch the label to 300 dpi").</summary>
    [ObservableProperty]
    private string _switchLabelDpiText = string.Empty;

    /// <summary>Plain-language sentences about what the automatic conversion cannot do exactly (barcode module rounding, raw ZPL not scaled).</summary>
    public ObservableCollection<string> ConversionIssues { get; } = new();

    /// <summary>True when the conversion raised notes worth reading.</summary>
    public bool HasConversionIssues => ConversionIssues.Count > 0;

    /// <summary>Header of the expandable list of conversion notes ("2 things to check after conversion").</summary>
    [ObservableProperty]
    private string _conversionIssuesHeader = string.Empty;

    /// <summary>
    /// True when a printer is chosen (a Windows queue, or a network host). Only then is the label converted to the printer
    /// resolution; a ZPL export with no printer keeps the design resolution.
    /// </summary>
    public bool PrinterChosen => UseNetwork ? !string.IsNullOrWhiteSpace(Host) : !string.IsNullOrWhiteSpace(SelectedPrinterName);

    /// <summary>
    /// Returns the label to send to the printer: the same object when its resolution already equals the chosen one, otherwise a
    /// converted copy that keeps the physical size and layout. The source is never modified. In picture mode (<see cref="IsDriverMode"/>) it is
    /// always the same object: no ZPL resolution conversion happens.
    /// </summary>
    /// <param name="source">The label (or a private copy of it).</param>
    /// <param name="issues">Notes raised by the conversion; empty when nothing was converted.</param>
    /// <param name="error">Plain-language message when the label cannot be converted (the caller must stop); null otherwise.</param>
    /// <returns>The label to send, or null when <paramref name="error"/> is set.</returns>
    public LabelDocument? ConvertForPrinter(LabelDocument source, out IReadOnlyList<ValidationIssue> issues, out string? error)
    {
        issues = Array.Empty<ValidationIssue>();
        error = null;
        // Picture mode draws the label at the driver's own resolution when it prints, so the ZPL dots conversion does not apply: the label goes
        // as designed. The ZPL path below is untouched.
        if (IsDriverMode) return source;
        if (!LabelDpiConverter.NeedsConversion(source, SelectedDpi)) return source;
        try
        {
            var result = LabelDpiConverter.Convert(source, SelectedDpi);
            issues = result.Issues;
            return result.Document;
        }
        catch (NotSupportedException)
        {
            // Core refuses element kinds it does not know how to rescale; say so instead of crashing.
            error = Loc.Get("Print.DpiConvert.Failed", (int)source.Dpi, (int)SelectedDpi);
            return null;
        }
    }

    /// <summary>
    /// Reads the selected Windows printer driver without blocking the window: sets the resolution box to what the driver reports and keeps
    /// the paper size as an offer. A newer selection cancels an older read, so a slow driver can never overwrite a later choice.
    /// </summary>
    public async Task DetectAsync()
    {
        // Cancel and drop the previous read; the field must be cleared too, or the next call would touch a disposed source.
        var previous = _detectCts;
        _detectCts = null;
        previous?.Cancel();
        previous?.Dispose();
        _detected = PrinterDetection.None;
        DetectedSizeText = string.Empty;
        string? name = SelectedPrinterName;
        if (UseNetwork || string.IsNullOrEmpty(name))
        {
            IsDetecting = false;
            DpiNote = string.Empty;
            RefreshDpiMismatch();
            return;
        }
        var cts = _detectCts = new CancellationTokenSource();
        IsDetecting = true;
        DpiNote = Loc.Get("Print.Dpi.Detecting");
        PrinterDetection result;
        try { result = await _detect(name, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or NotSupportedException)
        {
            result = PrinterDetection.None;
        }
        // Another printer was chosen while this one was read: that read owns the state now.
        if (cts.IsCancellationRequested) return;
        IsDetecting = false;
        _detected = result;
        if (result.Dpi is { } dpi)
        {
            SelectedDpi = dpi;
            DpiNote = Loc.Get("Print.Dpi.Detected", (int)dpi);
        }
        else
        {
            DpiNote = Loc.Get("Print.Dpi.NotDetected");
        }
        if (result.PaperWidth is { } w && result.PaperHeight is { } h)
            DetectedSizeText = Loc.Get("Print.UsePrinterSize", FormatMeasure(w, h));
        RefreshDpiMismatch();
    }

    /// <summary>Fills the label width and height boxes with the paper size the driver reported (an explicit user action; nothing is overridden silently).</summary>
    [RelayCommand]
    private void UseDetectedSize()
    {
        if (_detected.PaperWidth is not { } w || _detected.PaperHeight is not { } h) return;
        int dpi = (int)(_detected.Dpi ?? SelectedDpi);
        WidthText = FormatMm(UnitConverter.Convert(w.Value, w.Unit, LengthUnit.Millimeters, dpi));
        HeightText = FormatMm(UnitConverter.Convert(h.Value, h.Unit, LengthUnit.Millimeters, dpi));
    }

    /// <summary>Converts the open label to the chosen printer resolution (one undo step).</summary>
    [RelayCommand]
    private void SwitchLabelDpi()
    {
        if (_applyLabelDpi is null || !HasDpiMismatch) return;
        _applyLabelDpi(SelectedDpi);
        RefreshDpiMismatch();
    }

    /// <summary>Compares the open label resolution with the chosen printer resolution and updates the note. Call it when the label may have changed.</summary>
    public void RefreshDpiMismatch()
    {
        var doc = _document();
        // While the driver is still being read the resolution box may hold a stale value, so say nothing yet. In picture mode nothing is
        // converted (see ConvertForPrinter), so a ZPL resolution note would describe something that does not happen.
        if (PrinterChosen && !IsDetecting && !IsDriverMode && LabelDpiConverter.NeedsConversion(doc, SelectedDpi))
        {
            DpiMismatchText = Loc.Get("Print.DpiConvert", (int)doc.Dpi, (int)SelectedDpi);
            SwitchLabelDpiText = _applyLabelDpi is null ? string.Empty : Loc.Get("Print.SwitchLabelDpi", (int)SelectedDpi);
            // Convert a throw-away copy only to learn what would be approximated; the real print converts again from the live label.
            ConvertForPrinter(doc, out var issues, out string? failure);
            ConversionIssues.Clear();
            if (failure is not null) ConversionIssues.Add(failure);
            foreach (var i in issues) ConversionIssues.Add(PrintMessages.DescribeDpiIssue(i));
            ConversionIssuesHeader = Loc.Get("Print.DpiIssues.Header", ConversionIssues.Count);
        }
        else
        {
            DpiMismatchText = string.Empty;
            SwitchLabelDpiText = string.Empty;
            ConversionIssues.Clear();
            ConversionIssuesHeader = string.Empty;
        }
        OnPropertyChanged(nameof(HasConversionIssues));
    }

    /// <summary>Sets the width and height boxes to the open label size, so a test label or a ^PW set-up matches the design and not an old stored size.</summary>
    public void UseDesignSize()
    {
        var doc = _document();
        WidthText = FormatMm(ToMm(doc.Width, doc.Dpi));
        HeightText = FormatMm(ToMm(doc.Height, doc.Dpi));
    }

    private static string FormatMeasure(Measure w, Measure h)
    {
        string unit = Loc.Get(w.Unit switch { LengthUnit.Inches => "Unit.Short.Inches", LengthUnit.Dots => "Unit.Short.Dots", _ => "Unit.Short.Millimeters" });
        return $"{Math.Round(w.Value, 2).ToString("0.##", CultureInfo.CurrentCulture)} x {Math.Round(h.Value, 2).ToString("0.##", CultureInfo.CurrentCulture)} {unit}";
    }

    /// <summary>Short description of the chosen printer for summaries, for example a Windows printer name or "192.168.1.50:9100".</summary>
    public string Summary => UseNetwork ? $"{Host}:{PortText}" : (SelectedPrinterName ?? string.Empty);

    private void LoadFromSettings()
    {
        var s = _settings;
        // Before the first-run branch: a printer the person answers for before ever printing must find a usable map too.
        NormalizeModeMap();
        if (!s.Configured)
        {
            // First run: start from the open label so width and resolution are sensible without typing anything.
            var doc = _document();
            SelectedDpi = doc.Dpi;
            WidthText = FormatMm(ToMm(doc.Width, doc.Dpi));
            HeightText = FormatMm(ToMm(doc.Height, doc.Dpi));
            SelectedMedia = doc.MediaTracking;
            LoadStatusChoice();
            if (_defaultPrinter is not null) SelectedPrinterName = _defaultPrinter;
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
        LoadStatusChoice();
    }

    private void LoadStatusChoice()
    {
        _statusCheckNetwork = _settings.StatusCheckNetwork;
        _statusCheckWindows = _settings.StatusCheckWindows;
        OnPropertyChanged(nameof(CheckStatus));
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
                // The organization's default printer is only a suggestion: when this computer does not have it, fall back quietly.
                if (!(saved == _defaultPrinter && !_settings.Configured)) PrintersNote = Loc.Get("Print.Printers.Missing", saved);
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
        // A fresh list brings the driver and port names the suggestion reads, and a language change brings new texts; the selection itself may
        // not have changed, so its change handler would not raise these.
        NotifyMode();
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
            // WithMode keeps a TCP target on ZPL whatever is asked; Mode is ZPL for a network printer anyway.
            target = PrinterTarget.TcpIp(Host.Trim(), port).WithStatus(StatusOptions).WithMode(Mode);
            return null;
        }
        if (string.IsNullOrWhiteSpace(SelectedPrinterName)) return Loc.Get("Print.Field.NoPrinter");
        target = PrinterTarget.Spooler(SelectedPrinterName).WithStatus(StatusOptions).WithMode(Mode);
        return null;
    }

    // ~HS is a ZPL command. A printer in picture mode does not understand it and would print it as text, so picture mode never asks around a job.
    private PrinterStatusOptions StatusOptions => CheckStatus && !IsDriverMode ? PrinterStatusOptions.Default : PrinterStatusOptions.Off;

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
        // Darkness and speed are printer-language settings; in driver (picture) mode their boxes are hidden and nothing sends them, so whatever
        // is typed there (even something invalid left from before) must neither be read nor block the print. The typed text itself is kept.
        if (!IsDriverMode && !string.IsNullOrWhiteSpace(DarknessText))
        {
            if (!int.TryParse(DarknessText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int d))
                return Loc.Get("Print.Field.Number", Loc.Get("Print.Profile.Darkness"));
            darkness = d;
        }
        if (!IsDriverMode && !string.IsNullOrWhiteSpace(SpeedText))
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
        // Not in driver mode: the boxes are hidden there, and writing them would clear a value the person set for printer-language mode
        // (an empty or ignored box would store null). The stored values stay, so switching back restores them.
        if (!IsDriverMode)
        {
            _settings.Darkness = int.TryParse(DarknessText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int d) ? d : null;
            _settings.SpeedIps = int.TryParse(SpeedText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int sp) ? sp : null;
        }
        _settings.StatusCheckNetwork = _statusCheckNetwork;
        _settings.StatusCheckWindows = _statusCheckWindows;
        _save();
    }
}
