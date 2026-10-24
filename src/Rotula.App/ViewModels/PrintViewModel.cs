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
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(CanPrint)); };
    }

    /// <summary>Stops listening to the data view-model; called when the dialog closes.</summary>
    public void Detach()
    {
        if (Records is not null) Records.PropertyChanged -= OnRecordsChanged;
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
        // Questions come before anything is reserved or sent; cancelling them cancels the print without a message (the operator chose it).
        var answers = AskPrompts(doc, data);
        if (answers is null) return;
        var options = VariableServices.Options(_variables, answers);
        await SendAsync(Loc.Get("Print.Busy.Printing"), ct => _service.PrintAsync(target!, doc, data, profile, options, ct),
            Loc.Get("Print.Done.Label", copies));
    }

    /// <summary>
    /// Prints the ticked records through the same path as the Data merge window's print button. The copies typed here are the copies of each
    /// record's label (unless the Data window sets copies per record itself).
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
        var outcome = await Records.PrintBatchAsync(RunAsync, p => Progress = p, copies);
        if (outcome is { } o) SetResult(o.Text, o.IsError);
    }

    /// <summary>Prints the built-in test label (checks the connection, the width and the character set).</summary>
    [RelayCommand]
    private async Task PrintTestAsync()
    {
        if (IsBusy) return;
        if (!TryPrepare(out var target, out var profile)) return;
        await SendAsync(Loc.Get("Print.Busy.Test"), ct => _service.PrintTestLabelAsync(target!, profile!, ct), Loc.Get("Print.Done.Test"));
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
            string text = PrintMessages.Describe(result.Error!);
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
