using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.App.ViewModels;

/// <summary>
/// The Print dialog: choose where to print and how the printer is set up, print the label (or a test label), or save the
/// ZPL to a file. Sending is async and cancellable, so the window never freezes.
/// </summary>
public sealed partial class PrintViewModel : OperationViewModel
{
    private readonly IPrintService _service;
    private readonly Func<LabelDocument> _document;
    private readonly Func<IReadOnlyDictionary<string, string>?> _data;
    private readonly IFileDialogService _files;
    private readonly string _documentName;

    /// <summary>Creates the dialog's view-model.</summary>
    /// <param name="service">Printing service (a fake in tests; tests never reach a real printer).</param>
    /// <param name="printer">Printer and profile fields, shared with the data merge window.</param>
    /// <param name="document">The open label.</param>
    /// <param name="data">Values for the record shown on the canvas, or null to print the sample values.</param>
    /// <param name="files">File dialogs (Save as ZPL).</param>
    /// <param name="documentName">File name stem suggested for the ZPL file.</param>
    public PrintViewModel(IPrintService service, PrinterSettingsViewModel printer, Func<LabelDocument> document,
        Func<IReadOnlyDictionary<string, string>?> data, IFileDialogService files, string documentName = "label")
    {
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
    }

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

    /// <summary>Re-checks the label for errors that block printing. The test label does not depend on this.</summary>
    public void RefreshLabelProblems()
    {
        var gen = ZplGenerator.Generate(_document(), _data());
        LabelProblems = gen.HasErrors ? PrintMessages.DescribeIssues(gen.Issues) : string.Empty;
    }

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
        var data = _data();
        await SendAsync(Loc.Get("Print.Busy.Printing"), ct => _service.PrintAsync(target!, doc, data, profile, ct),
            Loc.Get("Print.Done.Label", copies));
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
        if (result.Succeeded)
        {
            SetResult(successText, false);
            Printer.Persist();
        }
        else SetResult(PrintMessages.Describe(result.Error!), result.Error!.Kind != PrintErrorKind.Cancelled);
    }

    /// <summary>Saves the ZPL (printer set-up commands plus the label) to a file instead of printing.</summary>
    [RelayCommand]
    private async Task SaveZplAsync()
    {
        if (IsBusy) return;
        if (!TryCopies(out int copies) || !TryPrepare(out _, out var profile, needTarget: false)) return;
        var doc = ElementCloner.CloneDocument(_document());
        doc.Print.Quantity = copies;
        var gen = ZplGenerator.Generate(doc, _data());
        if (gen.HasErrors)
        {
            SetResult(Loc.Get("Print.Error.InvalidLabel") + Environment.NewLine + PrintMessages.DescribeIssues(gen.Issues), true);
            return;
        }
        string? path = _files.PickExportFile(ExportKind.Zpl, _documentName + ".zpl");
        if (path is null) return;
        string zpl = (profile is null ? string.Empty : PrinterSetup.BuildSetupZpl(profile)) + gen.Zpl;
        byte[]? bytes = ZplFiles.TryEncode(zpl, doc.Encoding, ByteExactBlocks.From(doc));
        if (bytes is null)
        {
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
        if (!ok) return;
        if (writeError is not null) { SetResult(writeError, true); return; }
        Printer.Persist();
        SetResult(Loc.Get("Print.Done.Saved", Path.GetFileName(path)), false);
    }

    /// <summary>Closes the dialog (stopping any running job first).</summary>
    [RelayCommand]
    private void Close()
    {
        Printer.Persist();
        RequestClose();
    }
}
