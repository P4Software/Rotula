using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Prompts;
using Rotula.Core.Zpl;
using Rotula.Files;
using Rotula.Files.Station;
using Rotula.Printing;

namespace Rotula.App.ViewModels;

/// <summary>What the big status panel of the print station says.</summary>
public enum StationStatusKind
{
    /// <summary>Nothing to report yet.</summary>
    None,
    /// <summary>The printer is being asked for its status.</summary>
    Checking,
    /// <summary>The printer answered and can print.</summary>
    Ready,
    /// <summary>The printer status could not be read; printing is still possible.</summary>
    Unknown,
    /// <summary>A job is being sent (and checked).</summary>
    Printing,
    /// <summary>The printer reported a fault (head open, out of labels...), so nothing was sent.</summary>
    HeldBack,
    /// <summary>The labels were sent; the printer was not asked afterwards.</summary>
    Printed,
    /// <summary>The labels were sent and the printer says it finished without a fault.</summary>
    Verified,
    /// <summary>The labels were sent but the printer could not confirm them.</summary>
    NotVerified,
    /// <summary>Nothing (or not everything) was printed: a wrong answer, a label problem, a printer fault after the job, a connection failure.</summary>
    Problem,
}

/// <summary>A picture of a label drawn by the preview renderer, as premultiplied BGRA pixels (the view turns it into a bitmap).</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Pixels">The pixels, row after row.</param>
/// <param name="Stride">Bytes per row.</param>
public sealed record StationImage(int Width, int Height, byte[] Pixels, int Stride)
{
    /// <summary>Draws the label of <paramref name="preview"/> as it prints, at most <paramref name="maxPixels"/> on its longer side. Null when it cannot be drawn.</summary>
    public static StationImage? Render(PreviewCache preview, LabelDocument doc, int maxPixels)
    {
        var label = preview.GetLabel();
        int longest = Math.Max(label.WidthDots, label.HeightDots);
        if (longest <= 0) return null;
        double scale = Math.Min(1.0, maxPixels / (double)longest);
        using var result = preview.RenderPrinted(scale, out _);
        if (result is null) return null;
        var bmp = result.Bitmap;
        return new StationImage(bmp.Width, bmp.Height, bmp.Bytes, bmp.RowBytes);
    }
}

/// <summary>What the print station needs from the application, behind interfaces so the view-model runs in tests with fakes.</summary>
/// <param name="Printing">The print service (the only way anything is printed).</param>
/// <param name="Variables">Counter memory, remembered answers and the text rasterizer, shared with the designer.</param>
/// <param name="Messages">Message boxes (the "Print N labels?" question of a data print).</param>
/// <param name="DesignerPrinter">The printer last used in the designer; labels without a printer of their own use it.</param>
/// <param name="Database">Saved database connections, for labels whose data comes from a database; null uses the real ones.</param>
/// <param name="LoadLabel">Reads a .rotula file; null uses <see cref="RotulaFileSerializer.LoadFile"/>.</param>
/// <param name="Time">Clock for the PIN lock-out; null uses the computer's.</param>
public sealed record StationServices(IPrintService Printing, IVariableServices Variables, IMessageService Messages, PrintSettings? DesignerPrinter = null,
    IDatabaseServices? Database = null, Func<string, RotulaFileContent>? LoadLabel = null, TimeProvider? Time = null);

/// <summary>One label tile of the print station: its name, scan key, thumbnail, and why it cannot be used when it cannot.</summary>
public sealed partial class StationLabelViewModel : ObservableObject
{
    internal StationLabelViewModel(StationLabel entry) => Entry = entry;

    /// <summary>The label as the configuration describes it.</summary>
    public StationLabel Entry { get; }

    /// <summary>The name on the tile.</summary>
    public string Name => Entry.Name;

    /// <summary>The scan key, or empty.</summary>
    public string Key => Entry.Key ?? string.Empty;

    /// <summary>True when the label has a scan key (shown on the tile).</summary>
    public bool HasKey => Key.Length > 0;

    /// <summary>The design, or null when the file could not be read.</summary>
    public LabelDocument? Document { get; internal set; }

    /// <summary>The data settings saved with the label, or null.</summary>
    public Rotula.Files.DataMergeSettings? Merge { get; internal set; }

    /// <summary>The thumbnail, or null when the label could not be drawn.</summary>
    [ObservableProperty]
    private StationImage? _thumbnail;

    /// <summary>Why the label cannot be opened, in plain language; empty when it can.</summary>
    public string LoadError { get; internal set; } = string.Empty;

    /// <summary>True when the label was read and can be opened.</summary>
    public bool IsAvailable => Document is not null;

    /// <summary>True when the search text finds this label (by name, key or file name).</summary>
    internal bool Matches(string search) =>
        search.Length == 0 || Name.Contains(search, StringComparison.OrdinalIgnoreCase) || Key.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(Entry.Path).Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The print station: a locked, print-only screen for the warehouse floor. The operator picks a label (or scans its code), answers its questions
/// (typed or scanned; Enter or Tab moves on), sets the quantity and prints. Printing goes through <see cref="IPrintService"/> exactly like the Print
/// dialog, with the printer status check, and the result is shown in large plain words. Leaving the station needs the PIN when one is set.
/// </summary>
public sealed partial class StationViewModel : OperationViewModel
{
    /// <summary>Wrong PINs in a row before the PIN box locks.</summary>
    public const int MaxPinAttempts = 5;

    /// <summary>How long the PIN box stays locked after too many wrong PINs.</summary>
    public static readonly TimeSpan PinLockout = TimeSpan.FromSeconds(30);

    private const int ThumbnailPixels = 240;
    private const int PreviewPixels = 900;

    private readonly StationConfig _config;
    private readonly StationServices _services;
    private readonly IPrintService _service;
    private readonly StationAnswers _answers;
    private readonly TimeProvider _time;
    private PromptSet? _set;
    private EditorSession? _session;
    private PreviewCache? _previewCache;
    private int _statusVersion;
    private int _pinFailures;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    /// <summary>Creates the station and reads every label of the configuration (thumbnails included).</summary>
    /// <param name="config">The checked configuration.</param>
    /// <param name="warnings">Warnings of the configuration (labels left out), shown on the label list.</param>
    /// <param name="services">Printing and the rest.</param>
    public StationViewModel(StationConfig config, IReadOnlyList<ValidationIssue> warnings, StationServices services)
    {
        _config = config;
        _services = services;
        _service = services.Printing;
        _time = services.Time ?? TimeProvider.System;
        _answers = new StationAnswers(this, services.Variables);
        Scanner = new ScanSequencer(config.PrintOnLastScan, config.ScanToSelect);
        var notes = warnings.Select(Loc.FormatIssue).ToList();
        foreach (var entry in config.Labels)
        {
            var label = new StationLabelViewModel(entry);
            Read(label);
            if (!label.IsAvailable) notes.Add(Loc.Get("Station.Label.LoadFailed", label.Name, label.LoadError));
            Labels.Add(label);
        }
        WarningsText = string.Join(Environment.NewLine, notes);
        RefreshVisible();
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy)) { OnPropertyChanged(nameof(CanPrint)); OnPropertyChanged(nameof(CanPrintRecords)); } };
    }

    /// <summary>Lists configuration problems in plain language, one per line (errors first).</summary>
    public static string DescribeIssues(IEnumerable<ValidationIssue> issues) =>
        string.Join(Environment.NewLine, issues.OrderBy(i => i.Severity == ValidationSeverity.Error ? 0 : 1).Select(Loc.FormatIssue));

    /// <summary>Opens the only label at once when the station offers exactly one; call once the window is shown.</summary>
    public Task StartAsync() => Labels.Count(l => l.IsAvailable) == 1 && Labels.Count == 1 ? OpenAsync(Labels[0]) : Task.CompletedTask;

    // ------------------------------------------------------------------------------------ label list

    /// <summary>The terminator logic for the scanner (used by the window for every Enter and Tab).</summary>
    public ScanSequencer Scanner { get; }

    /// <summary>Title at the top of the screen.</summary>
    public string Title => _config.Title.Length > 0 ? _config.Title : Loc.Get("Station.Title");

    /// <summary>True when the station fills the screen without a title bar.</summary>
    public bool FullScreen => _config.FullScreen;

    /// <summary>Every label of the configuration.</summary>
    public ObservableCollection<StationLabelViewModel> Labels { get; } = new();

    /// <summary>The labels the search finds.</summary>
    public ObservableCollection<StationLabelViewModel> VisibleLabels { get; } = new();

    /// <summary>Labels left out or unreadable, one per line; empty when everything is fine.</summary>
    public string WarningsText { get; }

    /// <summary>True when <see cref="WarningsText"/> has something to say.</summary>
    public bool HasWarnings => WarningsText.Length > 0;

    /// <summary>What is typed (or scanned) into the search box.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Why the last scan on the label list opened nothing; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchMessage))]
    private string _searchMessage = string.Empty;

    /// <summary>True when <see cref="SearchMessage"/> has text.</summary>
    public bool HasSearchMessage => SearchMessage.Length > 0;

    /// <summary>True when the search finds nothing.</summary>
    public bool NoLabelsFound => VisibleLabels.Count == 0;

    partial void OnSearchTextChanged(string value)
    {
        SearchMessage = string.Empty;
        RefreshVisible();
    }

    private void RefreshVisible()
    {
        string search = ScanSequencer.Clean(SearchText);
        VisibleLabels.Clear();
        foreach (var l in Labels.Where(l => l.Matches(search))) VisibleLabels.Add(l);
        OnPropertyChanged(nameof(NoLabelsFound));
    }

    /// <summary>
    /// Enter in the search box: a scanned key opens its label, a search that leaves one label opens it, anything else says that no label has that code.
    /// </summary>
    public async Task SubmitSearchAsync()
    {
        var hits = VisibleLabels.ToList();
        var kind = Scanner.SelectLabel(SearchText, Labels, l => l.Entry.Key, hits, out var match);
        if (kind == LabelScanKind.Open && match is not null)
        {
            await OpenAsync(match);
            return;
        }
        if (kind == LabelScanKind.Unknown) SearchMessage = Loc.Get("Station.UnknownCode", ScanSequencer.Clean(SearchText));
        // The code is still in the box: the next scan replaces it.
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens a label tile (a click or a tap).</summary>
    [RelayCommand]
    private Task OpenLabel(StationLabelViewModel? label) => label is null ? Task.CompletedTask : OpenAsync(label);

    /// <summary>True when the label list can be shown again (the station offers more than one label).</summary>
    public bool CanGoBack => Labels.Count > 1;

    /// <summary>Goes back to the label list.</summary>
    [RelayCommand]
    private void BackToList()
    {
        if (IsBusy || !CanGoBack) return;
        CloseLabel();
        SearchText = string.Empty;
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Read(StationLabelViewModel label)
    {
        try
        {
            var content = (_services.LoadLabel ?? RotulaFileSerializer.LoadFile)(label.Entry.Path);
            label.Document = content.Document;
            label.Merge = content.Merge;
            var cache = new PreviewCache(new EditorSession(content.Document));
            label.Thumbnail = StationImage.Render(cache, content.Document, ThumbnailPixels);
            cache.Dispose();
        }
        catch (RotulaFileException ex)
        {
            label.LoadError = Loc.Get(ex.MessageKey, ex.Args.Cast<object>().Prepend(Path.GetFileName(label.Entry.Path)).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            label.LoadError = Loc.Get("File.Error.Read", Path.GetFileName(label.Entry.Path));
        }
    }

    // ------------------------------------------------------------------------------------ the open label

    /// <summary>The open label, or null while the list is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLabelOpen), nameof(IsChoosing), nameof(CanPrint))]
    private StationLabelViewModel? _current;

    /// <summary>True while a label is open.</summary>
    public bool IsLabelOpen => Current is not null;

    /// <summary>True while the label list is shown.</summary>
    public bool IsChoosing => Current is null;

    /// <summary>The questions of the open label, in the order the label declares them.</summary>
    public ObservableCollection<PromptFieldViewModel> Fields { get; } = new();

    /// <summary>True when the open label asks no questions.</summary>
    public bool HasNoFields => Fields.Count == 0;

    /// <summary>The printer of the open label (target and profile), or null when none is set up.</summary>
    public PrinterSettingsViewModel? Printer { get; private set; }

    /// <summary>"Printer: ..." for the open label.</summary>
    public string PrinterText => Printer is null ? Loc.Get("Station.Printer.None") : Loc.Get("Station.Printer", Printer.Summary);

    /// <summary>The quantity as typed.</summary>
    [ObservableProperty]
    private string _copiesText = "1";

    /// <summary>The most copies one print of the open label may have.</summary>
    public int MaxCopies => Current?.Entry.MaxCopies ?? 1;

    /// <summary>"1 to N labels" under the quantity box.</summary>
    public string CopiesHint => Loc.Get("Station.Copies.Hint", MaxCopies);

    /// <summary>The live preview of the open label with the answers typed so far.</summary>
    [ObservableProperty]
    private StationImage? _preview;

    /// <summary>True when Print may run.</summary>
    public bool CanPrint => IsIdle && Current is not null && Printer is not null;

    /// <summary>The data the label was saved with, when the station allows printing its records; null otherwise.</summary>
    public DataMergeViewModel? Records { get; private set; }

    /// <summary>True when the open label's records can be printed.</summary>
    public bool CanPrintRecords => IsIdle && Records is { HasRecords: true } && Printer is not null;

    /// <summary>True when the open label may print its records (whether or not they could be loaded).</summary>
    public bool ShowRecords => Records is not null;

    /// <summary>"12 records from the label's data", or why they could not be read.</summary>
    public string RecordsText => Records is null ? string.Empty
        : Records.HasRecords ? Loc.Get("Station.Records", Records.RecordCount)
        : Loc.Get("Station.Records.NotLoaded", Records.ResultText.Length > 0 ? Records.ResultText : Loc.Get("Station.Records.None"));

    /// <summary>Raised when the keyboard should go to the question at the given index.</summary>
    public event EventHandler<int>? FocusFieldRequested;

    /// <summary>Raised when the keyboard should go to the quantity and the Print button.</summary>
    public event EventHandler? FocusPrintRequested;

    /// <summary>Raised when the keyboard should go to the search box.</summary>
    public event EventHandler? FocusSearchRequested;

    /// <summary>Raised when an answer changed and the preview should be drawn again (the window waits a moment, then calls <see cref="RenderPreview"/>).</summary>
    public event EventHandler? PreviewRefreshRequested;

    /// <summary>Opens a label: its questions, quantity, printer, preview, and the printer status check.</summary>
    public async Task OpenAsync(StationLabelViewModel label)
    {
        if (IsBusy) return;
        if (!label.IsAvailable)
        {
            SearchMessage = Loc.Get("Station.Label.LoadFailed", label.Name, label.LoadError);
            return;
        }
        CloseLabel();
        var doc = label.Document!;
        _session = new EditorSession(doc);
        _previewCache = new PreviewCache(_session);
        Printer = BuildPrinter(label, doc);
        _set = PromptSet.Build(doc, null, _services.Variables.PromptMemory);
        foreach (var item in _set.Items)
        {
            var field = new PromptFieldViewModel(item);
            field.PropertyChanged += OnFieldChanged;
            Fields.Add(field);
        }
        int copies = label.Entry.DefaultCopies ?? Math.Max(1, doc.Print.Quantity);
        CopiesText = Math.Clamp(copies, 1, label.Entry.MaxCopies).ToString(CultureInfo.InvariantCulture);
        Current = label;
        ResultText = string.Empty;
        SetStatus(StationStatusKind.None, string.Empty, string.Empty);
        OnPropertyChanged(nameof(HasNoFields));
        OnPropertyChanged(nameof(PrinterText));
        OnPropertyChanged(nameof(MaxCopies));
        OnPropertyChanged(nameof(CopiesHint));
        OnPropertyChanged(nameof(CanPrint));
        RenderPreview();
        RequestFirstFocus();
        if (Printer is null) SetStatus(StationStatusKind.Problem, Loc.Get("Station.Status.Problem"), Loc.Get("Station.Printer.None"));

        if (label.Entry.AllowDataMerge && label.Merge is not null && Printer is not null)
        {
            Records = new DataMergeViewModel(_service, Printer, () => doc, () => label.Name, NoFileDialogs.Instance, _services.Messages, _services.Database,
                null, _answers);
            Records.Apply(label.Merge);
            OnPropertyChanged(nameof(ShowRecords));
            await Records.ReloadRememberedDataAsync();
            if (!ReferenceEquals(Current, label)) return;
            OnPropertyChanged(nameof(RecordsText));
            OnPropertyChanged(nameof(CanPrintRecords));
        }
        await CheckPrinterAsync();
    }

    private void CloseLabel()
    {
        _statusVersion++;
        foreach (var f in Fields) f.PropertyChanged -= OnFieldChanged;
        Fields.Clear();
        _previewCache?.Dispose();
        _previewCache = null;
        _session = null;
        _set = null;
        Records = null;
        Printer = null;
        Preview = null;
        Current = null;
        OnPropertyChanged(nameof(ShowRecords));
        OnPropertyChanged(nameof(RecordsText));
        OnPropertyChanged(nameof(CanPrintRecords));
        OnPropertyChanged(nameof(HasNoFields));
        SetStatus(StationStatusKind.None, string.Empty, string.Empty);
    }

    private PrinterSettingsViewModel? BuildPrinter(StationLabelViewModel label, LabelDocument doc)
    {
        PrintSettings settings;
        if (label.Entry.Printer is { } p)
        {
            settings = new PrintSettings
            {
                Configured = true,
                Transport = p.Host is null ? "Spooler" : "Tcp",
                PrinterName = p.WindowsPrinter ?? string.Empty,
                Host = p.Host ?? string.Empty,
                Port = p.Port,
                Dpi = (p.Dpi is { } dpi ? (PrinterDpi)dpi : doc.Dpi).ToString(),
                MediaTracking = doc.MediaTracking.ToString(),
                Darkness = p.Darkness,
                SpeedIps = p.Speed,
                StatusCheckNetwork = p.CheckStatus,
                StatusCheckWindows = p.CheckStatus,
            };
        }
        else if (_services.DesignerPrinter is { Configured: true } d)
        {
            // A copy: the station never changes the designer's own settings.
            settings = new PrintSettings
            {
                Configured = true, Transport = d.Transport, PrinterName = d.PrinterName, Host = d.Host, Port = d.Port, Dpi = d.Dpi, WidthMm = d.WidthMm,
                HeightMm = d.HeightMm, MediaTracking = d.MediaTracking, Darkness = d.Darkness, SpeedIps = d.SpeedIps,
                StatusCheckNetwork = d.StatusCheckNetwork, StatusCheckWindows = d.StatusCheckWindows,
            };
        }
        else return null;
        // No driver detection: the station prints at the resolution it was set up with, the same on every PC.
        var vm = new PrinterSettingsViewModel(_service, settings, () => { }, () => doc, (_, _) => Task.FromResult(PrinterDetection.None));
        vm.UseDesignSize();
        return vm;
    }

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PromptFieldViewModel.Value)) return;
        PreviewRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Draws the preview again with the current answers (and the shown data record, when the label's data is loaded).</summary>
    public void RenderPreview()
    {
        if (_previewCache is null || _session is null || Current?.Document is not { } doc) return;
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Records?.CurrentData is { } record) foreach (var kv in record) data[kv.Key] = kv.Value;
        // Typed answers show as the record's values; an empty one keeps the label's default in the picture.
        foreach (var f in Fields) if (f.Value.Length > 0) data[f.Name] = f.Item.Uppercase ? f.Value.ToUpperInvariant() : f.Value;
        _previewCache.Data = data;
        _session.Touch();
        Preview = StationImage.Render(_previewCache, doc, PreviewPixels);
    }

    private void RequestFirstFocus()
    {
        if (Fields.Count > 0) FocusFieldRequested?.Invoke(this, 0);
        else FocusPrintRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------------------------ scanner

    /// <summary>
    /// Enter, Tab or Shift+Tab in a question field (typed, or sent by the scanner after its code). Checks that field's answer, moves the keyboard on,
    /// and prints after the last field when "print on last scan" is on.
    /// </summary>
    public async Task FieldTerminatedAsync(PromptFieldViewModel field, ScanTerminator terminator)
    {
        int index = Fields.IndexOf(field);
        if (index < 0 || _set is null) return;
        bool accepted = true;
        if (terminator != ScanTerminator.ShiftTab)
        {
            var check = _set.Validate(Typed());
            string error = ErrorsFor(field.Name, check);
            field.Error = error;
            accepted = error.Length == 0;
        }
        var step = Scanner.Next(index, Fields.Count, terminator, accepted);
        switch (step.Kind)
        {
            case ScanStepKind.Stay:
            case ScanStepKind.MoveTo:
                FocusFieldRequested?.Invoke(this, step.FieldIndex);
                break;
            case ScanStepKind.FocusPrint:
                FocusPrintRequested?.Invoke(this, EventArgs.Empty);
                break;
            case ScanStepKind.Print:
                await PrintAsync();
                break;
        }
    }

    private Dictionary<string, string> Typed() => Fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.OrdinalIgnoreCase);

    private static string ErrorsFor(string name, PromptValidation check) =>
        string.Join(" ", check.Issues.Where(i => i.Args.Count > 0 && string.Equals(i.Args[0], name, StringComparison.OrdinalIgnoreCase)).Select(Loc.FormatIssue));

    /// <summary>
    /// Checks every answer against the label's own rules (required, length, pattern, type) and marks the refused ones. Returns the accepted answers,
    /// or null when one is refused (the keyboard then goes to the first refused field).
    /// </summary>
    private IReadOnlyDictionary<string, string>? CheckAnswers(PromptSet set)
    {
        var typed = Typed();
        var check = set.Validate(typed);
        foreach (var f in Fields) f.Error = ErrorsFor(f.Name, check);
        if (check.IsValid) return check.Values;
        int first = Fields.ToList().FindIndex(f => f.HasError);
        if (first >= 0) FocusFieldRequested?.Invoke(this, first);
        SetStatus(StationStatusKind.Problem, Loc.Get("Station.Status.Problem"), Loc.Get("Station.Fields.Fix"));
        return null;
    }

    // ------------------------------------------------------------------------------------ printing

    /// <summary>Prints the open label with the answers and the quantity, through the print service with the printer status check.</summary>
    [RelayCommand]
    public async Task PrintAsync()
    {
        if (IsBusy || Current?.Document is not { } source || _set is null) return;
        if (Printer is null)
        {
            SetStatus(StationStatusKind.Problem, Loc.Get("Station.Status.Problem"), Loc.Get("Station.Printer.None"));
            return;
        }
        var answers = CheckAnswers(_set);
        if (answers is null) return;
        if (!int.TryParse(CopiesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int copies) || copies < 1 || copies > MaxCopies)
        {
            SetStatus(StationStatusKind.Problem, Loc.Get("Station.Status.Problem"), Loc.Get("Station.Copies.Invalid", MaxCopies));
            FocusPrintRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        string? error = Printer.TryBuildTarget(out var target);
        PrinterProfile? profile = null;
        error ??= Printer.TryBuildProfile(out profile, source.Encoding);
        if (error is not null) { SetStatus(StationStatusKind.Problem, Loc.Get("Station.Status.Problem"), error); return; }

        // A private copy: the quantity and the resolution conversion never touch the label the station keeps.
        var doc = ElementCloner.CloneDocument(source);
        doc.Print.Quantity = copies;
        var converted = Printer.ConvertForPrinter(doc, out _, out string? convertError);
        if (converted is null) { SetStatus(StationStatusKind.Problem, Loc.Get("Station.Status.Problem"), convertError!); return; }
        doc = converted;
        // The design is checked with the answers before anything is reserved or sent, so the operator sees the real reason.
        var variables = _services.Variables;
        var gen = VariableEngine.HasDefinitions(doc)
            ? ZplGenerator.GenerateWithVariables(doc, new VariableContext { PromptAnswers = answers, CounterStore = variables.CounterStore }, null, variables.TextRasterizer)
            : ZplGenerator.Generate(doc, null, variables.TextRasterizer);
        if (gen.HasErrors)
        {
            SetStatus(StationStatusKind.Problem, Loc.Get("Station.Status.Problem"), Loc.Get("Print.Error.InvalidLabel") + Environment.NewLine + PrintMessages.DescribeIssues(gen.Issues));
            return;
        }

        var options = VariableServices.Options(variables, answers);
        _statusVersion++;
        SetStatus(StationStatusKind.Printing, Loc.Get("Station.Status.Printing"), target!.Status.Enabled ? Loc.Get("Station.Status.Printing.Checked") : string.Empty);
        PrintResult? result = null;
        bool finished = await RunAsync(Loc.Get("Station.Status.Printing"), async ct => result = await _service.PrintAsync(target, doc, null, profile, options, ct));
        if (!finished || result is null)
        {
            // Stopped or crashed: RunAsync wrote the reason; nothing is reset so the operator can try again.
            SetStatus(ResultIsError ? StationStatusKind.Problem : StationStatusKind.None, ResultIsError ? Loc.Get("Station.Status.Problem") : string.Empty, ResultText);
            return;
        }
        if (ShowResult(result, Loc.Get("Print.Done.Label", copies)))
        {
            _set.Remember(answers, variables.PromptMemory);
            ResetFields();
        }
    }

    /// <summary>Prints every ticked record of the label's data (through the Data merge batch print), with the answers typed here.</summary>
    [RelayCommand]
    private async Task PrintRecordsAsync()
    {
        if (IsBusy || Records is not { HasRecords: true } records || _set is null) return;
        if (!int.TryParse(CopiesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int copies) || copies < 1 || copies > MaxCopies)
        {
            SetStatus(StationStatusKind.Problem, Loc.Get("Station.Status.Problem"), Loc.Get("Station.Copies.Invalid", MaxCopies));
            return;
        }
        SetStatus(StationStatusKind.Printing, Loc.Get("Station.Status.Printing"), string.Empty);
        var outcome = await records.PrintBatchAsync(RunAsync, p => Progress = p, copies);
        if (outcome is not { } o)
        {
            SetStatus(ResultIsError ? StationStatusKind.Problem : StationStatusKind.None, ResultIsError ? Loc.Get("Station.Status.Problem") : string.Empty, ResultText);
            return;
        }
        SetStatus(o.IsError ? StationStatusKind.Problem : StationStatusKind.Printed, Loc.Get(o.IsError ? "Station.Status.Problem" : "Station.Status.Printed"), o.Text);
        if (!o.IsError) ResetFields();
    }

    /// <summary>Shows a print result in the status panel. Returns true when the labels went out (the questions are then reset for the next item).</summary>
    private bool ShowResult(PrintResult result, string doneText)
    {
        string notes = PrintMessages.DescribeNotices(result);
        if (!result.Succeeded)
        {
            var error = result.Error!;
            string text = PrintMessages.Describe(error) + (notes.Length > 0 ? Environment.NewLine + notes : string.Empty);
            bool heldBack = error.Kind is PrintErrorKind.HeadOpen or PrintErrorKind.PaperOut or PrintErrorKind.RibbonOut or PrintErrorKind.HeadTooHot
                or PrintErrorKind.DiagnosticMode or PrintErrorKind.BufferFull or PrintErrorKind.Paused;
            SetStatus(heldBack ? StationStatusKind.HeldBack : StationStatusKind.Problem, Loc.Get(heldBack ? "Station.Status.HeldBack" : "Station.Status.Problem"), text);
            return false;
        }
        var keys = result.Notices.Select(n => n.MessageKey).ToHashSet(StringComparer.Ordinal);
        var kind = keys.Contains(PrinterStatusKeys.FaultAfterJob) ? StationStatusKind.Problem
            : keys.Contains(PrinterStatusKeys.Verified) ? StationStatusKind.Verified
            : keys.Contains(PrinterStatusKeys.NotVerified) || keys.Contains(PrinterStatusKeys.NotChecked) ? StationStatusKind.NotVerified
            : StationStatusKind.Printed;
        string title = Loc.Get(kind switch
        {
            StationStatusKind.Problem => "Station.Status.Fault",
            StationStatusKind.Verified => "Station.Status.Verified",
            StationStatusKind.NotVerified => "Station.Status.NotVerified",
            _ => "Station.Status.Printed",
        });
        SetStatus(kind, title, notes.Length == 0 ? doneText : doneText + Environment.NewLine + notes);
        return true;
    }

    /// <summary>
    /// Gets the questions ready for the next item: answers go back to their defaults (or the remembered last answer), except those the station keeps.
    /// </summary>
    private void ResetFields()
    {
        if (Current?.Document is not { } doc) return;
        var entry = Current.Entry;
        var keep = new HashSet<string>(entry.KeepFields, StringComparer.OrdinalIgnoreCase);
        _set = PromptSet.Build(doc, null, _services.Variables.PromptMemory);
        var fresh = _set.Items.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var f in Fields)
        {
            if (!entry.KeepAllValues && !keep.Contains(f.Name) && fresh.TryGetValue(f.Name, out var item)) f.Value = item.InitialValue;
            f.Error = string.Empty;
        }
        RenderPreview();
        RequestFirstFocus();
    }

    /// <summary>Asks the printer of the open label whether it can print, and shows the answer. Does nothing when the status check is off.</summary>
    [RelayCommand]
    public async Task CheckPrinterAsync()
    {
        if (Printer is null || Printer.TryBuildTarget(out var target) is not null || target is null || !target.Status.Enabled) return;
        int version = ++_statusVersion;
        SetStatus(StationStatusKind.Checking, Loc.Get("Station.Status.Checking"), string.Empty);
        PrinterStatus status;
        try { status = await _service.GetStatusAsync(target); }
        catch (OperationCanceledException) { return; }
        // A print that started meanwhile has its own, newer answer.
        if (version != _statusVersion) return;
        (string text, bool problem) = PrintMessages.DescribeStatus(status);
        var kind = !status.IsAvailable ? StationStatusKind.Unknown : problem ? StationStatusKind.HeldBack : StationStatusKind.Ready;
        SetStatus(kind, Loc.Get(kind switch
        {
            StationStatusKind.Unknown => "Station.Status.Unknown",
            StationStatusKind.HeldBack => "Station.Status.NeedsAttention",
            _ => "Station.Status.Ready",
        }), text);
    }

    // ------------------------------------------------------------------------------------ status panel

    /// <summary>What the status panel shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(StatusIsGood), nameof(StatusIsBad), nameof(StatusIsNeutral))]
    private StationStatusKind _statusKind;

    /// <summary>The short, large line of the status panel ("Printed and verified").</summary>
    [ObservableProperty]
    private string _statusTitle = string.Empty;

    /// <summary>The explanation under it: what happened and what to do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    private string _statusDetail = string.Empty;

    /// <summary>True when the status panel shows something.</summary>
    public bool HasStatus => StatusKind != StationStatusKind.None;

    /// <summary>True when <see cref="StatusDetail"/> has text.</summary>
    public bool HasStatusDetail => StatusDetail.Length > 0;

    /// <summary>Green panel: ready, printed, verified.</summary>
    public bool StatusIsGood => StatusKind is StationStatusKind.Ready or StationStatusKind.Printed or StationStatusKind.Verified;

    /// <summary>Red panel: held back or not printed.</summary>
    public bool StatusIsBad => StatusKind is StationStatusKind.HeldBack or StationStatusKind.Problem;

    /// <summary>Amber panel: checking, printing, status unknown, not confirmed.</summary>
    public bool StatusIsNeutral => StatusKind is StationStatusKind.Checking or StationStatusKind.Printing or StationStatusKind.Unknown or StationStatusKind.NotVerified;

    private void SetStatus(StationStatusKind kind, string title, string detail)
    {
        StatusKind = kind;
        StatusTitle = title;
        StatusDetail = detail;
    }

    // ------------------------------------------------------------------------------------ leaving the station

    /// <summary>True when leaving the station needs the PIN.</summary>
    public bool HasPin => _config.ExitPinHash is not null;

    /// <summary>True once leaving was allowed (the PIN was right, or there is none); the window may then close.</summary>
    public bool ExitAuthorized { get; private set; }

    /// <summary>True while the PIN box is shown.</summary>
    [ObservableProperty]
    private bool _isPinPromptOpen;

    /// <summary>Why the PIN was refused; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPinError))]
    private string _pinError = string.Empty;

    /// <summary>True when <see cref="PinError"/> has text.</summary>
    public bool HasPinError => PinError.Length > 0;

    /// <summary>Raised when the station may close.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The Exit button (and closing the window): leaves at once without a PIN, otherwise shows the PIN box.</summary>
    [RelayCommand]
    public void RequestExit()
    {
        if (!HasPin)
        {
            Leave();
            return;
        }
        PinError = string.Empty;
        IsPinPromptOpen = true;
    }

    /// <summary>Closes the PIN box and stays in the station.</summary>
    [RelayCommand]
    public void CancelExit()
    {
        IsPinPromptOpen = false;
        PinError = string.Empty;
    }

    /// <summary>
    /// Checks a typed PIN. A right PIN leaves the station; after <see cref="MaxPinAttempts"/> wrong ones in a row the box is locked for
    /// <see cref="PinLockout"/>, so trying every PIN by hand is not practical. Returns true when the station may close.
    /// </summary>
    public async Task<bool> SubmitPinAsync(string pin)
    {
        if (!HasPin) { Leave(); return true; }
        var now = _time.GetUtcNow();
        if (now < _lockedUntil)
        {
            PinError = Loc.Get("Station.Pin.Locked", (int)Math.Ceiling((_lockedUntil - now).TotalSeconds));
            return false;
        }
        string hash = _config.ExitPinHash!;
        // PBKDF2 takes a moment on purpose; it runs off the UI thread so the screen stays responsive.
        bool ok = await Task.Run(() => StationPin.Verify(pin, hash));
        if (ok)
        {
            _pinFailures = 0;
            IsPinPromptOpen = false;
            Leave();
            return true;
        }
        if (++_pinFailures >= MaxPinAttempts)
        {
            _pinFailures = 0;
            _lockedUntil = _time.GetUtcNow() + PinLockout;
            PinError = Loc.Get("Station.Pin.Locked", (int)PinLockout.TotalSeconds);
        }
        else PinError = Loc.Get("Station.Pin.Wrong");
        return false;
    }

    private void Leave()
    {
        ExitAuthorized = true;
        CancelOperation();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Answers the label's questions for the Data merge batch print from the station's own fields, so a data print never opens the prompt dialog.
    /// </summary>
    private sealed class StationAnswers(StationViewModel station, IVariableServices inner) : IVariableServices
    {
        public Rotula.Core.Variables.Counters.ICounterStore? CounterStore => inner.CounterStore;
        public IPromptMemory PromptMemory => inner.PromptMemory;
        public Rotula.Core.Text.ITextRasterizer? TextRasterizer => inner.TextRasterizer;

        public IReadOnlyDictionary<string, string>? Ask(PromptSet set)
        {
            if (set.IsEmpty) return new Dictionary<string, string>();
            var answers = station.CheckAnswers(set);
            if (answers is null) return null;
            set.Remember(answers, inner.PromptMemory);
            return answers;
        }
    }

    /// <summary>The station opens no file dialogs: everything it prints comes from its configured folder.</summary>
    private sealed class NoFileDialogs : IFileDialogService
    {
        public static readonly NoFileDialogs Instance = new();
        public string? PickOpenLabel() => null;
        public string? PickSaveLabel(string suggestedFileName) => null;
        public string? PickExportFile(ExportKind kind, string suggestedFileName) => null;
        public string? PickOpenZpl() => null;
        public string? PickOpenImage() => null;
        public string? PickOpenData() => null;
        public string? PickOpenDatabaseFile() => null;
    }
}
