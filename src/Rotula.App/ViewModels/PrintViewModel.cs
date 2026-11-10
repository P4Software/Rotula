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
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.App.ViewModels;

/// <summary>
/// The Print dialog: choose where to print and how the printer is set up, print the label (or a test label), or save the
/// ZPL to a file. Sending is async and cancellable, so the window never freezes. When a data file or database is loaded, this dialog is also where
/// the operator picks the records (the same ticks as in the Data merge window) and prints them as a batch.
/// <para>
/// For a printer in Windows-driver (picture) mode, printing first runs the driver pre-flight on the exact document that will be printed and
/// lists its findings (<see cref="PreflightLines"/>). An error finding stops the job; warnings need one tick (<see cref="Acknowledged"/>) that
/// belongs to exactly those findings. A printer in printer-language (ZPL) mode never gets a pre-flight: it prints exactly as before.
/// </para>
/// </summary>
public sealed partial class PrintViewModel : OperationViewModel
{
    private readonly IPrintService _service;
    private readonly Func<LabelDocument> _document;
    private readonly Func<IReadOnlyDictionary<string, string>?> _data;
    private readonly IFileDialogService _files;
    private readonly string _documentName;
    private readonly IVariableServices _variables;
    private readonly Action? _showSheets;

    /// <summary>Fingerprint of the findings on screen (what the person can tick for), or null when none are shown.</summary>
    private string? _shownFingerprint;

    /// <summary>The printer, transport and mode the shown findings were worked out for, or null when none are shown.</summary>
    private PrinterChoice? _findingsFor;

    /// <summary>What a pre-flight depends on besides the design: which printer, by which transport, in which mode.</summary>
    private readonly record struct PrinterChoice(bool UseNetwork, string? PrinterName, PrinterOutputMode Mode);

    /// <summary>Creates the dialog's view-model.</summary>
    /// <param name="service">Printing service (a fake in tests; tests never reach a real printer).</param>
    /// <param name="printer">Printer and profile fields, shared with the data merge window.</param>
    /// <param name="document">The open label.</param>
    /// <param name="data">Values for the record shown on the canvas, or null to print the sample values.</param>
    /// <param name="files">File dialogs (Save as ZPL).</param>
    /// <param name="documentName">File name stem suggested for the ZPL file.</param>
    /// <param name="variables">Counter memory and the print-time questions; null means no counter memory and no questions (tests).</param>
    /// <param name="records">The data merge view-model that holds the loaded records and their ticks; null (or no records) keeps the single-label dialog.</param>
    /// <param name="showSheets">Opens the "Label sheets" dialog (office printer sheets as a PDF); null hides the button.</param>
    public PrintViewModel(IPrintService service, PrinterSettingsViewModel printer, Func<LabelDocument> document,
        Func<IReadOnlyDictionary<string, string>?> data, IFileDialogService files, string documentName = "label", IVariableServices? variables = null,
        DataMergeViewModel? records = null, Action? showSheets = null)
    {
        _showSheets = showSheets;
        Records = records;
        _variables = variables ?? new VariableServices(null, null);
        _service = service;
        Printer = printer;
        _document = document;
        _data = data;
        _files = files;
        _documentName = documentName;
        _copiesText = Math.Max(1, document().Print.Quantity).ToString(CultureInfo.InvariantCulture);
        // The test label and the set-up width follow the open design (not a size stored by an earlier session); the user can still change them here.
        printer.UseDesignSize();
        printer.RefreshDpiMismatch();
        RefreshLabelProblems();
        // A plain subscription, removed by Detach when the dialog closes: the data view-model outlives this dialog and must not keep it alive.
        if (records is not null) records.PropertyChanged += OnRecordsChanged;
        // Same for the printer view-model, which belongs to the main window: the mode line follows it and a new printer or mode drops the findings.
        printer.PropertyChanged += OnPrinterChanged;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(CanPrint)); };
    }

    /// <summary>Stops listening to the data and printer view-models (both outlive this dialog); called when the dialog closes.</summary>
    public void Detach()
    {
        if (Records is not null) Records.PropertyChanged -= OnRecordsChanged;
        Printer.PropertyChanged -= OnPrinterChanged;
    }

    private void OnPrinterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PrinterSettingsViewModel.ModeExplanation)) OnPropertyChanged(nameof(ModeExplanation));
        // Findings belong to one printer in one mode. Two printers can report identical findings, so the tick is dropped on any change of
        // printer, transport or mode, not only when the fingerprint differs. Comparing the choice, instead of clearing on every notification,
        // keeps the findings through notifications that change nothing about it (a language switch, for example).
        if (_findingsFor is { } shownFor && shownFor != CurrentChoice()) ClearPreflight();
    }

    private PrinterChoice CurrentChoice() => new(Printer.UseNetwork, Printer.SelectedPrinterName, Printer.Mode);

    // ------------------------------------------------------------------------------- Windows-driver (picture) mode

    /// <summary>
    /// The findings of the last driver pre-flight, one sentence each (worded by <see cref="DriverPreflightGate"/>), errors and warnings in the
    /// order the library reported them. Empty for a printer in printer-language mode, before the first print, and after a change of printer or mode.
    /// </summary>
    public ObservableCollection<string> PreflightLines { get; } = new();

    /// <summary>True when <see cref="PreflightLines"/> has something to show (the findings list appears only then).</summary>
    public bool HasPreflightLines => PreflightLines.Count > 0;

    private bool _needsAcknowledgement;

    /// <summary>
    /// True when the shown findings are warnings only, so the job may print once <see cref="Acknowledged"/> is ticked (the box appears only then).
    /// False when any finding is an error: no tick can let such a label through.
    /// </summary>
    public bool NeedsAcknowledgement
    {
        get => _needsAcknowledgement;
        private set => SetProperty(ref _needsAcknowledgement, value);
    }

    /// <summary>
    /// "I understand and want to print anyway": the person accepts the shown warnings for this job. It belongs to exactly those findings: when a
    /// later pre-flight finds anything else (the design, the printer or the mode changed) it is cleared and has to be ticked again.
    /// </summary>
    [ObservableProperty]
    private bool _acknowledged;

    /// <summary>One plain sentence: which print mode the chosen printer uses and why (see <see cref="PrinterSettingsViewModel.ModeExplanation"/>).</summary>
    public string ModeExplanation => Printer.ModeExplanation;

    /// <summary>True for a target printed as a picture through its Windows driver; every other target sends ZPL and is never pre-flighted.</summary>
    private static bool IsDriverTarget(PrinterTarget target) => target is { Transport: PrinterTransport.Spooler, Mode: PrinterOutputMode.WindowsDriver };

    /// <summary>
    /// The picture-mode gate in front of a print. For a printer-language target it returns <paramref name="target"/> at once and asks nothing,
    /// so a ZPL print is exactly what it was. For a driver target it runs <see cref="IPrintService.PreflightAsync(PrinterTarget, LabelDocument, long, CancellationToken)"/> on
    /// <paramref name="document"/>, which must be the very document that is then printed (after copies and any conversion): the service works
    /// the fingerprint out again from what it receives, and a different document would be refused as not acknowledged. <paramref name="labelCount"/>
    /// is how many labels the whole job prints (copies included; for ticked records the whole batch): more than the pre-flight's limit is an error
    /// finding, shown and worded like any other, and nothing is printed.
    /// </summary>
    /// <returns>
    /// The target to print to (carrying the acknowledgement when warnings were ticked), or null when printing must stop; the reason is then
    /// already shown as the result.
    /// </returns>
    private async Task<PrinterTarget?> GateDriverAsync(PrinterTarget target, LabelDocument document, long labelCount)
    {
        if (!IsDriverTarget(target)) return target;
        DriverPreflightResult? preflight = null;
        // Inside RunAsync: asking a driver can take a moment, and the person can stop it like any print.
        bool finished = await RunAsync(Loc.Get("Print.Busy.Checking"), async ct => preflight = await _service.PreflightAsync(target, document, labelCount, ct));
        // Not finished: RunAsync already showed "stopped" or the failure.
        if (!finished || preflight is null) return null;
        if (preflight.Findings.Count == 1 && preflight.Findings[0].MessageKey == DriverPreflight.Cancelled)
        {
            // The person stopped the check before the driver answered: a calm stop, not a list of problems to fix.
            SetResult(Loc.Get("Common.OperationCancelled"), false);
            return null;
        }
        ShowFindings(preflight);
        if (preflight.HasErrors)
        {
            SetResult(ErrorResult(preflight.Findings.Where(f => f.Severity == ValidationSeverity.Error).ToList()), true);
            return null;
        }
        if (!preflight.NeedsAcknowledgement) return target;
        if (!Acknowledged)
        {
            SetResult(Loc.Get("Print.Driver.TickToContinue"), true);
            return null;
        }
        // ShowFindings cleared the tick if these findings differ from the ones it was given for, so this is the fingerprint the person accepted.
        return target.WithAcknowledgement(preflight.Fingerprint);
    }

    /// <summary>Most error findings written out in the result line; the findings card above lists every one.</summary>
    private const int MaxErrorLinesInResult = 2;

    /// <summary>
    /// The result line for error findings: the first <see cref="MaxErrorLinesInResult"/> of them, then how many more the findings card lists.
    /// The result banner is docked under the scrolled content and never scrolls, so a label with many unprintable barcodes (one sentence each)
    /// would otherwise squeeze the scrolled part (the findings, the printer and the way back to ZPL) down to nothing at the minimum window size.
    /// </summary>
    private static string ErrorResult(IReadOnlyList<ValidationIssue> errors)
    {
        var lines = DriverPreflightGate.Lines(errors.Take(MaxErrorLinesInResult)).ToList();
        if (errors.Count > MaxErrorLinesInResult) lines.Add(Loc.Get("Print.Driver.MoreInList", errors.Count - MaxErrorLinesInResult));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Shows the findings of a pre-flight, and drops the tick when they are not the findings it was given for.</summary>
    private void ShowFindings(DriverPreflightResult preflight)
    {
        string fingerprint = preflight.Fingerprint;
        if (!string.Equals(fingerprint, _shownFingerprint, StringComparison.Ordinal))
        {
            Acknowledged = false;
            _shownFingerprint = fingerprint;
        }
        _findingsFor = CurrentChoice();
        PreflightLines.Clear();
        foreach (string line in DriverPreflightGate.Lines(preflight.Findings)) PreflightLines.Add(line);
        NeedsAcknowledgement = preflight.NeedsAcknowledgement && !preflight.HasErrors;
        OnPropertyChanged(nameof(HasPreflightLines));
    }

    /// <summary>Forgets the shown findings and the tick (another printer or mode was chosen).</summary>
    private void ClearPreflight()
    {
        _shownFingerprint = null;
        _findingsFor = null;
        PreflightLines.Clear();
        Acknowledged = false;
        NeedsAcknowledgement = false;
        OnPropertyChanged(nameof(HasPreflightLines));
    }

    /// <summary>
    /// True when the chosen printer's mode is settled. A Windows printer that never answered "How does this printer understand labels?" does
    /// not print in either mode (the answer decides what is sent); the result then points at the question, or, for a queue that is not
    /// installed (no question is shown for it), says to refresh the list or choose another printer.
    /// </summary>
    private bool ModeSettled()
    {
        if (!Printer.NeedsModeQuestion) return true;
        SetResult(Printer.PendingModeText(), true);
        return false;
    }

    /// <summary>
    /// The profile the test label is built from, exactly as <see cref="IPrintService.PrintTestLabelAsync"/> would build it: when the profile asks
    /// for the driver's detected settings, they are read once here and the result is passed on with the request switched off, so the service
    /// builds the identical test label that was pre-flighted instead of reading the driver a second time. Null when the person stopped it.
    /// The dialog's own profile does not ask for detected settings today (<see cref="PrinterSettingsViewModel.TryBuildProfile"/> leaves it off,
    /// the resolution box already shows what the driver reported); this keeps the check on the right label if that ever changes.
    /// </summary>
    private async Task<PrinterProfile?> ResolveTestProfileAsync(PrinterTarget target, PrinterProfile profile)
    {
        if (!profile.UseDetectedSettings) return profile;
        PrinterProfile? resolved = null;
        bool finished = await RunAsync(Loc.Get("Print.Busy.Checking"), async ct =>
            resolved = PrinterProfile.FromInfo(await _service.GetPrinterInfoAsync(target, ct), profile) with { UseDetectedSettings = false });
        return finished ? resolved : null;
    }

    private void OnRecordsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ticks, the loaded data and the copies setting all change what the Records card says and whether Print may run.
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(UsesRecordData));
        OnPropertyChanged(nameof(ShowRecordBanner));
        OnPropertyChanged(nameof(SelectionLine));
        OnPropertyChanged(nameof(RecordsBlockedText));
        OnPropertyChanged(nameof(HasRecordsBlockedText));
        OnPropertyChanged(nameof(CopiesFromData));
        OnPropertyChanged(nameof(CanPrint));
    }

    /// <summary>The loaded records and their ticks (shared with the Data merge window), or null when this dialog has no data.</summary>
    public DataMergeViewModel? Records { get; }

    /// <summary>True when data with at least one record is loaded: the Records card shows and Print prints the ticked records.</summary>
    public bool HasRecords => Records is { HasRecords: true };

    /// <summary>"3 of 5 selected", always visible while records exist.</summary>
    public string SelectionLine => Records?.SelectionSummary ?? string.Empty;

    /// <summary>Why Print is disabled (records exist but none is ticked), in plain language, or empty.</summary>
    public string RecordsBlockedText => HasRecords && Records!.SelectedCount == 0 ? Loc.Get("Print.Records.NoneSelected") : string.Empty;

    /// <summary>True when <see cref="RecordsBlockedText"/> has something to say.</summary>
    public bool HasRecordsBlockedText => RecordsBlockedText.Length > 0;

    /// <summary>True when the Data window sets the copies per record (a fixed number or a column), which then wins over the number typed here.</summary>
    public bool CopiesFromData => HasRecords && Records!.CopiesSource != CopiesSource.LabelDefault;

    /// <summary>True when the Print button may run: idle, and when records exist at least one is ticked.</summary>
    public bool CanPrint => IsIdle && !HasRecordsBlockedText && Records is not { IsBusy: true };

    /// <summary>Printer selection and profile fields.</summary>
    public PrinterSettingsViewModel Printer { get; }

    /// <summary>Number of copies as typed (it replaces the label's own print quantity for this job).</summary>
    [ObservableProperty]
    private string _copiesText;

    /// <summary>Problems in the label that block printing (empty when it is fine).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLabelProblems))]
    private string _labelProblems = string.Empty;

    /// <summary>True when the label has errors.</summary>
    public bool HasLabelProblems => LabelProblems.Length > 0;

    /// <summary>True when the label is printed with the values of the record shown on the canvas.</summary>
    public bool UsesRecordData => _data() is not null;

    /// <summary>True when the "printed with the record on the canvas" note shows: data is shown on the canvas but there is no records card to pick from.</summary>
    public bool ShowRecordBanner => UsesRecordData && !HasRecords;

    /// <summary>Re-checks the label for errors that block printing. The test label does not depend on this.</summary>
    public void RefreshLabelProblems()
    {
        var gen = Check(_document(), _data());
        LabelProblems = gen.HasErrors ? PrintMessages.DescribeIssues(gen.Issues) : string.Empty;
    }

    /// <summary>
    /// Generates the label for checking only. A label with variable definitions is resolved (dates, computed values, the next serial number)
    /// without reserving anything or asking, so a broken formula or a counter that cannot remember shows up before the operator presses Print.
    /// </summary>
    private ZplGenerationResult Check(LabelDocument doc, IReadOnlyDictionary<string, string>? data) =>
        VariableEngine.HasDefinitions(doc)
            ? ZplGenerator.GenerateWithVariables(doc, new VariableContext { Record = data, CounterStore = _variables.CounterStore }, null, _variables.TextRasterizer)
            : ZplGenerator.Generate(doc, data, _variables.TextRasterizer);

    /// <summary>
    /// Asks the label's print-time questions. Returns the answers (empty when there is nothing to ask), or null when the operator cancelled.
    /// A question whose name the shown data record already answers is not asked.
    /// </summary>
    private IReadOnlyDictionary<string, string>? AskPrompts(LabelDocument doc, IReadOnlyDictionary<string, string>? data) =>
        _variables.Ask(Rotula.Core.Variables.Prompts.PromptSet.Build(doc, data, _variables.PromptMemory));

    /// <summary>Loads the printer list; called when the dialog opens.</summary>
    [RelayCommand]
    private Task RefreshPrintersAsync() => Printer.RefreshPrintersAsync();

    private bool TryCopies(out int copies)
    {
        if (int.TryParse(CopiesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out copies) && copies is >= 1 and <= 99999999) return true;
        SetResult(Loc.Get("Print.Field.Copies"), true);
        return false;
    }

    private bool TryPrepare(out PrinterTarget? target, out PrinterProfile? profile, bool needTarget = true)
    {
        target = null;
        profile = null;
        string? error = needTarget ? Printer.TryBuildTarget(out target) : null;
        error ??= Printer.TryBuildProfile(out profile, _document().Encoding);
        if (error is null) return true;
        SetResult(error, true);
        return false;
    }

    /// <summary>Prints the label.</summary>
    [RelayCommand]
    private async Task PrintAsync()
    {
        if (IsBusy) return;
        // Before the records branch too: whether a printer gets ZPL or a picture is the person's answer, so nothing goes out without it.
        if (!ModeSettled()) return;
        if (HasRecords) { await PrintRecordsAsync(); return; }
        if (!TryCopies(out int copies) || !TryPrepare(out var target, out var profile)) return;
        RefreshLabelProblems();
        if (HasLabelProblems)
        {
            SetResult(Loc.Get("Print.Error.InvalidLabel") + Environment.NewLine + LabelProblems, true);
            return;
        }
        // Print a copy so the dialog's copy count never alters the open label.
        var doc = ElementCloner.CloneDocument(_document());
        doc.Print.Quantity = copies;
        // A label designed for another resolution is converted to the printer's, so it prints at the same physical size.
        var converted = Printer.ConvertForPrinter(doc, out _, out string? convertError);
        if (converted is null) { SetResult(convertError!, true); return; }
        doc = converted;
        var data = _data();
        // Picture mode: the pre-flight judges this final document (copies set, conversion done), the one handed to the service below, and comes
        // before the questions so a label the driver cannot print does not ask them first. Printer language: returns the target untouched.
        var sendTo = await GateDriverAsync(target!, doc, Math.Max(1, doc.Print.Quantity));
        if (sendTo is null) return;
        // Questions come before anything is reserved or sent; cancelling them cancels the print without a message (the operator chose it).
        var answers = AskPrompts(doc, data);
        if (answers is null) return;
        var options = VariableServices.Options(_variables, answers);
        await SendAsync(Loc.Get("Print.Busy.Printing"), ct => _service.PrintAsync(sendTo, doc, data, profile, options, ct),
            Loc.Get("Print.Done.Label", copies));
    }

    /// <summary>
    /// Prints the ticked records through the same path as the Data merge window's print button. The copies typed here are the copies of each
    /// record's label (unless the Data window sets copies per record itself). For a printer in picture mode the batch is checked once for the
    /// design through this dialog's own gate (<see cref="GateDriverAsync"/>): its findings card and its one tick, never a second question on top.
    /// </summary>
    private async Task PrintRecordsAsync()
    {
        if (Records!.IsBusy) return;
        if (Records.SelectedCount == 0) { SetResult(RecordsBlockedText, true); return; }
        if (!TryCopies(out int copies)) return;
        RefreshLabelProblems();
        if (HasLabelProblems)
        {
            SetResult(Loc.Get("Print.Error.InvalidLabel") + Environment.NewLine + LabelProblems, true);
            return;
        }
        var outcome = await Records.PrintBatchAsync(RunAsync, p => Progress = p, copies, GateDriverAsync);
        if (outcome is { } o) SetResult(o.Text, o.IsError);
    }

    /// <summary>Prints the built-in test label (checks the connection, the width and the character set).</summary>
    [RelayCommand]
    private async Task PrintTestAsync()
    {
        if (IsBusy) return;
        if (!ModeSettled()) return;
        if (!TryPrepare(out var target, out var profile)) return;
        var sendTo = target!;
        if (IsDriverTarget(sendTo))
        {
            // The service builds the test label itself, so the same label is built here from the same profile and pre-flighted; anything else
            // would give another fingerprint and the service would refuse the job as not acknowledged.
            var testProfile = await ResolveTestProfileAsync(sendTo, profile!);
            if (testProfile is null) return;
            var gated = await GateDriverAsync(sendTo, TestLabel.Build(testProfile), 1);
            if (gated is null) return;
            sendTo = gated;
            profile = testProfile;
        }
        await SendAsync(Loc.Get("Print.Busy.Test"), ct => _service.PrintTestLabelAsync(sendTo, profile!, ct), Loc.Get("Print.Done.Test"));
    }

    private async Task SendAsync(string busyText, Func<CancellationToken, Task<PrintResult>> send, string successText)
    {
        PrintResult? result = null;
        bool finished = await RunAsync(busyText, async ct => result = await send(ct));
        if (!finished || result is null) return;
        string notes = PrintMessages.DescribeNotices(result);
        if (result.Succeeded)
        {
            // Notes (for example skipped serial numbers, or the printer's answer after the job) are shown with the success line so the
            // operator reads them. A fault the printer reported after the job turns the line into a problem: labels may be missing.
            SetResult(notes.Length == 0 ? successText : successText + Environment.NewLine + notes, PrintMessages.HasFaultAfterJob(result));
            Printer.Persist();
        }
        else
        {
            // The result, not only its error: a picture-mode job that stopped after pages went out must say so (see PrintMessages.Describe).
            string text = PrintMessages.Describe(result)!;
            if (notes.Length > 0) text += Environment.NewLine + notes;
            SetResult(text, result.Error!.Kind != PrintErrorKind.Cancelled);
        }
    }

    /// <summary>Saves the ZPL (printer set-up commands plus the label) to a file instead of printing.</summary>
    [RelayCommand]
    private async Task SaveZplAsync()
    {
        if (IsBusy) return;
        if (HasRecords) { await SaveRecordsZplAsync(); return; }
        if (!TryCopies(out int copies) || !TryPrepare(out _, out var profile, needTarget: false)) return;
        var doc = ElementCloner.CloneDocument(_document());
        doc.Print.Quantity = copies;
        // Same conversion as printing, so the saved file matches what the chosen printer would receive.
        if (Printer.PrinterChosen)
        {
            var converted = Printer.ConvertForPrinter(doc, out _, out string? convertError);
            if (converted is null) { SetResult(convertError!, true); return; }
            doc = converted;
        }
        var data = _data();
        var gen = Check(doc, data);
        if (gen.HasErrors)
        {
            SetResult(Loc.Get("Print.Error.InvalidLabel") + Environment.NewLine + PrintMessages.DescribeIssues(gen.Issues), true);
            return;
        }
        string? path = _files.PickExportFile(ExportKind.Zpl, _documentName + ".zpl");
        if (path is null) return;
        var answers = AskPrompts(doc, data);
        if (answers is null) return;
        // A saved file will be printed later, so its serial numbers count as used: the safe side, a number is never printed twice.
        var run = LabelPrintRun.Begin(doc, data, VariableServices.Options(_variables, answers));
        if (!run.CanPrint)
        {
            SetResult(Loc.Get("Print.Error.InvalidLabel") + Environment.NewLine + PrintMessages.DescribeIssues(run.Issues), true);
            return;
        }
        string zpl = (profile is null ? string.Empty : PrinterSetup.BuildSetupZpl(profile)) + string.Concat(run.Blocks().Select(b => b.Zpl));
        byte[]? bytes = ZplFiles.TryEncode(zpl, doc.Encoding, ByteExactBlocks.From(doc));
        if (bytes is null)
        {
            run.Cancel();
            SetResult(Loc.Get("Print.Error.EncodingFailed", doc.Encoding.ToString(), string.Empty), true);
            return;
        }
        string? writeError = null;
        bool ok = await RunAsync(Loc.Get("Print.Busy.Saving"), async ct =>
        {
            try { await File.WriteAllBytesAsync(path, bytes, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Say what to do (pick another folder) instead of showing the technical exception text.
                writeError = Loc.Get("File.Error.Write", path);
            }
        });
        if (!ok || writeError is not null)
        {
            // Nothing was saved, so the reserved numbers go back.
            run.Cancel();
            if (writeError is not null) SetResult(writeError, true);
            return;
        }
        var closing = run.Complete(run.LabelCount);
        Printer.Persist();
        string notes = string.Join(Environment.NewLine, run.Issues.Where(i => i.Severity != ValidationSeverity.Error).Concat(closing).Select(Loc.FormatIssue));
        string saved = Loc.Get("Print.Done.Saved", Path.GetFileName(path));
        SetResult(notes.Length == 0 ? saved : saved + Environment.NewLine + notes, false);
    }

    /// <summary>Saves the ticked records as ZPL (one file, or one per job as set in the Data window), through the Data window's export path.</summary>
    private async Task SaveRecordsZplAsync()
    {
        if (Records!.IsBusy) return;
        if (Records.SelectedCount == 0) { SetResult(RecordsBlockedText, true); return; }
        if (!TryCopies(out int copies)) return;
        var outcome = await Records.ExportBatchCoreAsync(RunAsync, p => Progress = p, copies);
        if (outcome is not { } o) return;
        SetResult(o.Text, o.IsError);
        if (!o.IsError) Printer.Persist();
    }

    /// <summary>True when the "Label sheets" button is offered.</summary>
    public bool CanShowSheets => _showSheets is not null;

    /// <summary>Opens the "Label sheets" dialog, for labels printed on sheets by an office printer instead of a label printer.</summary>
    [RelayCommand]
    private void ShowSheets()
    {
        if (IsBusy) return;
        _showSheets?.Invoke();
    }

    /// <summary>Closes the dialog (stopping any running job first).</summary>
    [RelayCommand]
    private void Close()
    {
        Printer.Persist();
        Detach();
        RequestClose();
    }
}
