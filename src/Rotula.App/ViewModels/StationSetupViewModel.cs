using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Model;
using Rotula.Files;
using Rotula.Files.Station;
using Rotula.Printing;

namespace Rotula.App.ViewModels;

/// <summary>
/// One label of the station in the set-up tool: its printer and print mode, what the Windows-driver pre-flight found, and the box with which the
/// person setting up the station accepts the findings for the operators.
/// </summary>
public sealed partial class StationSetupLabelViewModel : ObservableObject
{
    internal StationSetupLabelViewModel(StationLabel entry) => Entry = entry;

    /// <summary>The label as the configuration describes it.</summary>
    public StationLabel Entry { get; }

    /// <summary>The name of the label (the name on the station's tile).</summary>
    public string Name => Entry.Name;

    /// <summary>The printer the label was checked with ("Office Laser", "192.168.1.50:9100"); empty before the check.</summary>
    internal string PrinterSummary { get; set; } = string.Empty;

    /// <summary>"Printer: ..." once checked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrinterText))]
    private string _printerText = string.Empty;

    /// <summary>True when <see cref="PrinterText"/> has something to say.</summary>
    public bool HasPrinterText => PrinterText.Length > 0;

    /// <summary>"Print mode: ..." once checked; empty when the label has no printer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModeText))]
    private string _modeText = string.Empty;

    /// <summary>True when <see cref="ModeText"/> has something to say.</summary>
    public bool HasModeText => ModeText.Length > 0;

    /// <summary>One plain sentence: what the station does with this label and, when something is needed, what to do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    /// <summary>True when <see cref="StatusText"/> has something to say.</summary>
    public bool HasStatusText => StatusText.Length > 0;

    /// <summary>True when <see cref="StatusText"/> describes something that stops the station from printing this label (shown in red).</summary>
    [ObservableProperty]
    private bool _statusIsProblem;

    /// <summary>The pre-flight findings, one sentence each, worded by <see cref="DriverPreflightGate"/> as in the Print dialog.</summary>
    public ObservableCollection<string> FindingLines { get; } = new();

    /// <summary>True when there are findings to list.</summary>
    public bool HasFindingLines => FindingLines.Count > 0;

    /// <summary>True when a finding is an error: no acknowledgement lets this label through, so the box is hidden.</summary>
    [ObservableProperty]
    private bool _hasErrors;

    /// <summary>True when the findings are warnings only: the station prints this label once the box is ticked and the set-up saved.</summary>
    [ObservableProperty]
    private bool _needsAcknowledgement;

    /// <summary>
    /// "Let the station print this label anyway": the person setting up the station accepts exactly the listed findings for the operators.
    /// Ticked from the start when the acknowledgements file (station.acknowledged.json) already holds the fingerprint of these findings, or when
    /// the person ticked these same findings before pressing "Check again".
    /// </summary>
    [ObservableProperty]
    private bool _acknowledged;

    /// <summary>The fingerprint of the listed findings when they need the acknowledgement; null otherwise.</summary>
    internal string? Fingerprint { get; private set; }

    /// <summary>
    /// True once the label was checked to the end: its file opened, its printer answered, and the result is known. Only checked labels change
    /// what is stored when saving. A label that could not be checked (its file cannot be read, it has no printer, its printer cannot be
    /// reached, or the check was stopped) keeps the acknowledgement it had, so a passing problem never erases an earlier set-up.
    /// </summary>
    internal bool Checked { get; private set; }

    /// <summary>Forgets the result of an earlier check.</summary>
    internal void Reset()
    {
        PrinterText = ModeText = StatusText = string.Empty;
        PrinterSummary = string.Empty;
        StatusIsProblem = HasErrors = NeedsAcknowledgement = Acknowledged = false;
        Fingerprint = null;
        Checked = false;
        FindingLines.Clear();
        OnPropertyChanged(nameof(HasFindingLines));
    }

    /// <summary>A label that cannot be checked; what is stored for it is left as it was.</summary>
    internal void ShowProblem(string text)
    {
        StatusText = text;
        StatusIsProblem = true;
    }

    /// <summary>A label checked to the end that needs nothing from the person (printer language, or a picture with no findings).</summary>
    internal void ShowNothingToAccept(string text)
    {
        StatusText = text;
        Checked = true;
    }

    /// <summary>
    /// Shows the pre-flight result. The box is ticked when <paramref name="stored"/> is the fingerprint of these very findings, or when
    /// <paramref name="tickedBefore"/> says the person ticked these very findings before the check ran again.
    /// </summary>
    internal void ShowFindings(DriverPreflightResult preflight, string? stored, Func<string, bool> tickedBefore)
    {
        foreach (string line in DriverPreflightGate.Lines(preflight.Findings)) FindingLines.Add(line);
        OnPropertyChanged(nameof(HasFindingLines));
        HasErrors = preflight.HasErrors;
        NeedsAcknowledgement = preflight.NeedsAcknowledgement && !preflight.HasErrors;
        Fingerprint = NeedsAcknowledgement ? preflight.Fingerprint : null;
        Acknowledged = Fingerprint is { } fingerprint && (string.Equals(stored, fingerprint, StringComparison.Ordinal) || tickedBefore(fingerprint));
        StatusText = Loc.Get(HasErrors ? "Station.Setup.Driver.Errors" : NeedsAcknowledgement ? "Station.Setup.Driver.Warnings" : "Station.Setup.Driver.Clear");
        StatusIsProblem = HasErrors;
        Checked = true;
    }
}

/// <summary>
/// "Set up print station": the designer tool with which the person who sets up a station checks its labels on this PC and accepts, once, what a
/// Windows driver cannot print the same way. For every label it builds the printer and the document exactly as the station does
/// (<see cref="StationPrinting"/>), runs the driver pre-flight for a printer in picture mode, and lists the findings with one box. Saving stores
/// the fingerprint of each accepted label in <c>station.acknowledged.json</c> next to station.json (<see cref="StationAcknowledgements"/>,
/// read into <see cref="StationConfig.AcknowledgedFindings"/>); the station then prints those labels without asking its operators anything, and
/// refuses a label whose findings were never accepted, or have changed since.
/// <para>
/// Every check reads station.json, the label files and the acknowledgements file again, so "Check again" after changing a label, the
/// configuration or a printer checks what is on disk now. The fingerprint includes what the driver reports (paper, resolution), so the check
/// must run on the PC where the station prints, signed in as the account it runs under (which also holds the print modes). station.json itself
/// is never written: a folder without one is told to create it first. Nothing is written when nothing changed, so a station that prints in
/// printer language never gets the acknowledgements file.
/// </para>
/// </summary>
public sealed partial class StationSetupViewModel : OperationViewModel
{
    private readonly IPrintService _service;
    private readonly PrintSettings? _designer;
    private readonly Func<string, RotulaFileContent> _loadLabel;

    /// <summary>What the station was opened with (its folder, as the File menu picks it); every check loads it again.</summary>
    private readonly string _stationPath;

    /// <summary>The configuration as the last check read it (the one the tool was opened with until the first check).</summary>
    private StationConfig _config;

    /// <summary>What the acknowledgements file held when the configuration was last read, or after the last save: full label path to fingerprint.</summary>
    private Dictionary<string, string> _stored;

    /// <summary>Why the last check could not open the configuration, in plain words; null when it opened. Nothing can be saved meanwhile.</summary>
    private string? _configProblem;

    /// <summary>Creates the tool for a station whose configuration opened without errors. The labels are read and checked by <see cref="CheckCommand"/>.</summary>
    /// <param name="config">The station configuration.</param>
    /// <param name="service">The print service; the pre-flight asks the printer drivers of this PC.</param>
    /// <param name="designerPrinter">The designer's printer settings (the printer of labels without one, and the saved print modes); read only.</param>
    /// <param name="loadLabel">Reads a .rotula file; null uses <see cref="RotulaFileSerializer.LoadFile"/>.</param>
    /// <param name="stationPath">What the configuration was loaded from (the station folder the person picked), loaded again by every check; null
    /// uses the configuration file, or the label folder of a station without one.</param>
    public StationSetupViewModel(StationConfig config, IPrintService service, PrintSettings? designerPrinter, Func<string, RotulaFileContent>? loadLabel = null,
        string? stationPath = null)
    {
        _service = service;
        _designer = designerPrinter;
        _loadLabel = loadLabel ?? RotulaFileSerializer.LoadFile;
        _stationPath = stationPath ?? config.ConfigPath ?? config.LabelFolder;
        _config = config;
        _stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Use(config);
    }

    /// <summary>
    /// The acknowledgements file the set-up is saved in (<c>station.acknowledged.json</c> beside station.json), or null when the station has no
    /// station.json: the tool never creates one, and the acknowledgements belong next to it. Worked out again by every check.
    /// </summary>
    public string? AcknowledgementsPath
    {
        get => _acknowledgementsPath;
        private set => SetProperty(ref _acknowledgementsPath, value);
    }

    private string? _acknowledgementsPath;

    /// <summary>"Settings file: ..." under the title, or, for a folder without station.json, the plain note that it has to be created first.</summary>
    public string StationText => _config.ConfigPath is { } configPath ? Loc.Get("Station.Setup.File", configPath) : Loc.Get("Station.Setup.NoStationJson");

    /// <summary>Every label of the station, in the order of the configuration as last read.</summary>
    public ObservableCollection<StationSetupLabelViewModel> Labels { get; } = new();

    /// <summary>Takes a freshly read configuration: its labels (one row each, not checked yet), its acknowledgements and where they are saved.</summary>
    private void Use(StationConfig config)
    {
        _config = config;
        _stored = new Dictionary<string, string>(config.AcknowledgedFindings, StringComparer.OrdinalIgnoreCase);
        AcknowledgementsPath = config.ConfigPath is { } configPath ? StationAcknowledgements.PathFor(configPath) : null;
        Labels.Clear();
        foreach (var entry in config.Labels) Labels.Add(new StationSetupLabelViewModel(entry));
        OnPropertyChanged(nameof(StationText));
    }

    /// <summary>
    /// Checks the station as it is on disk now. Every row is cleared first; then station.json, the acknowledgements file and every label file
    /// are read again, and each label's printer and print mode are worked out and, for a printer in Windows-driver (picture) mode, the pre-flight
    /// runs on the same document the station prints. The window runs it when it opens; "Check again" runs it after the person changed a label,
    /// station.json or a printer. A configuration that no longer opens leaves every row unchecked and says why (nothing can be saved until it
    /// opens again); a label file that no longer opens says so on its own row. A tick that was not saved yet survives when the label's findings
    /// are the same ones.
    /// </summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        await RunAsync(Loc.Get("Station.Setup.Checking"), async ct =>
        {
            var tickedBefore = new HashSet<string>(
                Labels.Where(r => r is { Acknowledged: true, Fingerprint: not null }).Select(r => TickKey(r.Entry.Path, r.Fingerprint!)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var row in Labels) row.Reset();

            var load = StationConfigLoader.Load(_stationPath);
            if (!load.CanOpen)
            {
                // Never a stale pass: the rows stay without results and Save refuses until the configuration opens again.
                _configProblem = Loc.Get("Station.Open.Failed") + Environment.NewLine + StationViewModel.DescribeIssues(load.Issues);
                foreach (var row in Labels) row.ShowProblem(Loc.Get("Station.Open.Failed"));
                SetResult(_configProblem, true);
                return;
            }
            _configProblem = null;
            Use(load.Config!);
            foreach (var row in Labels)
            {
                ct.ThrowIfCancellationRequested();
                await CheckRowAsync(row, fingerprint => tickedBefore.Contains(TickKey(row.Entry.Path, fingerprint)), ct);
            }
        });
    }

    /// <summary>One label and one set of findings, as a key for remembering an unsaved tick across a check.</summary>
    private static string TickKey(string labelPath, string fingerprint) => labelPath + "\n" + fingerprint;

    private async Task CheckRowAsync(StationSetupLabelViewModel row, Func<string, bool> tickedBefore, CancellationToken ct)
    {
        LabelDocument doc;
        try { doc = _loadLabel(row.Entry.Path).Document; }
        catch (RotulaFileException ex)
        {
            row.ShowProblem(Loc.Get("Station.Label.LoadFailed", row.Name,
                Loc.Get(ex.MessageKey, ex.Args.Cast<object>().Prepend(Path.GetFileName(row.Entry.Path)).ToArray())));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            row.ShowProblem(Loc.Get("Station.Label.LoadFailed", row.Name, Loc.Get("File.Error.Read", Path.GetFileName(row.Entry.Path))));
            return;
        }
        var printer = StationPrinting.BuildPrinter(_service, row.Entry, doc, _designer, out bool modeDefaulted);
        if (printer is null)
        {
            row.ShowProblem(Loc.Get("Station.Setup.NoPrinter"));
            return;
        }
        row.PrinterSummary = printer.Summary;
        row.PrinterText = Loc.Get("Station.Printer", printer.Summary);
        row.ModeText = Loc.Get("Station.Setup.Mode", Loc.Get(printer.IsDriverMode ? "Print.Mode.WindowsDriver" : "Print.Mode.PrinterLanguage"));
        if (!printer.IsDriverMode)
        {
            // Printer language: nothing to check (the station never pre-flights a ZPL print). A queue with no saved mode prints ZPL too, and the
            // person reads where the mode is chosen.
            row.ShowNothingToAccept(Loc.Get(modeDefaulted ? "Station.Setup.NoModeYet" : "Station.Setup.Zpl"));
            return;
        }
        string? error = printer.TryBuildTarget(out var target);
        // The quantity does not change the findings; the label's opening quantity keeps the document identical to the station's anyway.
        var prepared = error is null ? StationPrinting.PrepareDocument(printer, doc, StationPrinting.DefaultCopies(row.Entry, doc), out error) : null;
        if (error is not null || prepared is null || target is null)
        {
            row.ShowProblem(error ?? string.Empty);
            return;
        }
        var preflight = await _service.PreflightAsync(target, prepared, ct);
        // The driver was not asked because the check was stopped: report it as a stop (RunAsync says so), not as a finding to accept.
        if (preflight.Findings.Count == 1 && preflight.Findings[0].MessageKey == DriverPreflight.Cancelled) throw new OperationCanceledException(ct);
        if (preflight.Findings.Count == 1 && preflight.Findings[0].MessageKey == DriverPreflight.DriverUnavailable)
        {
            // The printer did not answer (switched off, disconnected, not installed here): that says nothing about the label, so it is a problem
            // to fix, not a result. The label stays unchecked and keeps the acknowledgement stored for it.
            row.ShowProblem(DriverPreflightGate.FormatFinding(preflight.Findings[0]));
            return;
        }
        _stored.TryGetValue(row.Entry.Path, out string? stored);
        row.ShowFindings(preflight, stored, tickedBefore);
    }

    /// <summary>
    /// Stores the fingerprint of every ticked label in the acknowledgements file and removes it for checked labels that are not ticked (or no
    /// longer need it) and for labels the station no longer offers. Nothing is written when nothing changed; the file is deleted when nothing is
    /// accepted any more (also a damaged file, or one of a newer version); station.json is never touched. Nothing is saved for a station without
    /// station.json, or while its configuration does not open.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (IsBusy) return;
        if (_configProblem is { } problem)
        {
            SetResult(problem, true);
            return;
        }
        if (AcknowledgementsPath is not { } path)
        {
            SetResult(Loc.Get("Station.Setup.NoStationJson"), true);
            return;
        }
        // Only labels the station still offers keep an acknowledgement; a label taken out of station.json leaves nothing behind.
        var offered = new HashSet<string>(_config.Labels.Select(l => l.Path), StringComparer.OrdinalIgnoreCase);
        var next = _stored.Where(kv => offered.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var checkedRows = Labels.Where(r => r.Checked).ToList();
        // Two passes: one file can be listed twice (for example on a ZPL printer and on a picture printer). The entry that needs nothing must
        // not erase what was accepted for the other, so every checked file is cleared first and the accepted fingerprints are added after.
        foreach (var row in checkedRows) next.Remove(row.Entry.Path);
        var accepted = checkedRows.Where(r => r is { NeedsAcknowledgement: true, Acknowledged: true, Fingerprint: not null }).ToList();
        foreach (var row in accepted) next[row.Entry.Path] = row.Fingerprint!;
        // One file accepted on two picture printers that find different points: only one fingerprint can be kept for it (the last one ticked).
        var shared = accepted.GroupBy(r => r.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Select(r => r.Fingerprint).Distinct(StringComparer.Ordinal).Count() > 1);

        bool exists = File.Exists(path);
        bool unchanged = next.Count == 0 ? !exists : exists && SameEntries(next, _stored);
        if (unchanged)
        {
            SetResult(Loc.Get("Station.Setup.Unchanged"), false);
            return;
        }
        // Written whole through a temporary file (or deleted when nothing is accepted any more), so a station that starts meanwhile reads the old
        // acknowledgements or the new ones, never half a file.
        try { StationAcknowledgements.Write(path, _config.LabelFolder, next); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetResult(Loc.Get("Station.Setup.SaveFailed", path), true);
            return;
        }
        _stored = next;
        if (next.Count == 0) SetResult(Loc.Get("Station.Setup.Cleared", path), false);
        else if (shared is not null) SetResult(Loc.Get("Station.Setup.SharedLabel", path, Path.GetFileName(shared.Key), shared.Last().PrinterSummary), true);
        else SetResult(Loc.Get("Station.Setup.Saved", path), false);
    }

    /// <summary>True when both maps hold the same labels with the same fingerprints (labels compared without regard to letter case).</summary>
    private static bool SameEntries(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out string? other) && string.Equals(kv.Value, other, StringComparison.Ordinal));
}
