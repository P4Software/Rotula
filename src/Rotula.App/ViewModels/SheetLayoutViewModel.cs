using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Model;
using Rotula.Core.Sheets;
using Rotula.Core.Validation;
using Rotula.Printing;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;

namespace Rotula.App.ViewModels;

/// <summary>One entry of the template list: a built-in template, or null for "Custom".</summary>
/// <param name="Template">The template, or null for a layout the user types.</param>
/// <param name="Name">The name shown, built from the dimensions (no brand names).</param>
public sealed record SheetTemplateChoice(SheetTemplate? Template, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>One label position in the sheet preview, in preview pixels.</summary>
/// <param name="Position">1-based position on the sheet.</param>
/// <param name="Left">Left edge in the preview.</param>
/// <param name="Top">Top edge in the preview.</param>
/// <param name="Width">Width in the preview.</param>
/// <param name="Height">Height in the preview.</param>
/// <param name="IsUsed">True for a position before the start position (already used on this sheet).</param>
/// <param name="IsFilled">True for a position that gets a label on the first sheet.</param>
/// <param name="Caption">The number of the label that goes there, or empty.</param>
/// <param name="Tip">Tooltip and accessible name.</param>
public sealed record SheetSlotItem(int Position, double Left, double Top, double Width, double Height, bool IsUsed, bool IsFilled, string Caption, string Tip);

/// <summary>
/// The "Label sheets" dialog: choose a sheet for an office printer (a built-in template or a custom layout), see where the labels go, start at a
/// later position on a partly used sheet, and save the labels as a PDF at the sheet's real size. With data loaded the ticked records are used,
/// through the same path as printing; otherwise the label is repeated for the copies, each copy with its own serial number when it has a counter.
/// </summary>
/// <remarks>
/// The PDF is printed from any PDF reader at actual size. Sending the sheets straight to a Windows printer is not offered: a driver's own margins
/// and "fit to page" scaling would move the labels, which a PDF printed at 100 % avoids.
/// </remarks>
public sealed partial class SheetLayoutViewModel : OperationViewModel
{
    /// <summary>Largest size of the preview page, in pixels.</summary>
    internal const double PreviewMaxWidth = 300, PreviewMaxHeight = 380;

    private readonly Func<LabelDocument> _document;
    private readonly Func<IReadOnlyDictionary<string, string>?> _data;
    private readonly IFileDialogService _files;
    private readonly IVariableServices _variables;
    private readonly string _documentName;
    private readonly Action<SheetSettings>? _remember;
    private bool _applying;

    /// <summary>Creates the dialog's view-model.</summary>
    /// <param name="document">The open label.</param>
    /// <param name="data">The record shown on the canvas (used when no records are loaded), or null.</param>
    /// <param name="files">File dialogs (where to save the PDF).</param>
    /// <param name="variables">Counter memory, the print-time questions and the Windows-font drawing.</param>
    /// <param name="initial">The layout to start from (the one saved with the label, or a suggestion).</param>
    /// <param name="documentName">File name stem suggested for the PDF.</param>
    /// <param name="records">The data merge view-model with the loaded records and their ticks; null (or no records) uses the label's copies.</param>
    /// <param name="remember">Receives the layout when a PDF is saved or the dialog closes, so it is saved with the label.</param>
    public SheetLayoutViewModel(Func<LabelDocument> document, Func<IReadOnlyDictionary<string, string>?> data, IFileDialogService files,
        IVariableServices variables, SheetSettings initial, string documentName = "label", DataMergeViewModel? records = null,
        Action<SheetSettings>? remember = null)
    {
        _document = document;
        _data = data;
        _files = files;
        _variables = variables;
        _documentName = documentName;
        _remember = remember;
        Records = records;
        Templates = new[] { new SheetTemplateChoice(null, Loc.Get("Print.Sheet.Custom")) }
            .Concat(SheetTemplates.All.Select(t => new SheetTemplateChoice(t, NameOf(t)))).ToList();
        PageKinds = Enum.GetValues<SheetPageKind>().Select(k => new Choice<SheetPageKind>(k, "Enum.SheetPageKind." + k)).ToList();
        Orders = Enum.GetValues<SheetOrder>().Select(k => new Choice<SheetOrder>(k, "Enum.SheetOrder." + k)).ToList();
        GuideChoices = Enum.GetValues<SheetGuides>().Select(k => new Choice<SheetGuides>(k, "Enum.SheetGuides." + k)).ToList();
        _copiesText = Math.Max(1, document().Print.Quantity).ToString(CultureInfo.InvariantCulture);
        var template = SheetTemplates.Find(initial.TemplateId);
        _useInches = template?.Inches ?? initial.Layout.PageKind is SheetPageKind.Letter or SheetPageKind.Legal;
        _selectedOrder = Orders.First(o => o.Value == initial.Order);
        _selectedGuides = GuideChoices.First(g => g.Value == initial.Guides);
        _selectedTemplate = Templates.FirstOrDefault(t => t.Template is not null && t.Template.Id == template?.Id) ?? Templates[0];
        _selectedPage = PageKinds.First(p => p.Value == initial.Layout.PageKind);
        ShowLayout(initial.Layout);
        Refresh();
    }

    // ------------------------------------------------------------------------------------ choices

    /// <summary>"Custom" followed by the built-in templates.</summary>
    public IReadOnlyList<SheetTemplateChoice> Templates { get; }

    /// <summary>Letter, A4, Legal, Custom.</summary>
    public IReadOnlyList<Choice<SheetPageKind>> PageKinds { get; }

    /// <summary>The fill orders.</summary>
    public IReadOnlyList<Choice<SheetOrder>> Orders { get; }

    /// <summary>None, crop marks, outlines.</summary>
    public IReadOnlyList<Choice<SheetGuides>> GuideChoices { get; }

    /// <summary>The loaded records and their ticks, or null.</summary>
    public DataMergeViewModel? Records { get; }

    /// <summary>True when the ticked records are written (data is loaded).</summary>
    public bool UsesRecords => Records is { HasRecords: true };

    /// <summary>The chosen template ("Custom" when the user typed the layout).</summary>
    [ObservableProperty]
    private SheetTemplateChoice _selectedTemplate;

    partial void OnSelectedTemplateChanged(SheetTemplateChoice value)
    {
        if (_applying || value.Template is null) { OnPropertyChanged(nameof(IsCustom)); return; }
        _applying = true;
        try
        {
            UseInches = value.Template.Inches;
            SelectedPage = PageKinds.First(p => p.Value == value.Template.Layout.PageKind);
            ShowLayout(value.Template.Layout);
        }
        finally { _applying = false; }
        OnPropertyChanged(nameof(IsCustom));
        Refresh();
    }

    /// <summary>True when the layout is the user's own (no template).</summary>
    public bool IsCustom => SelectedTemplate.Template is null;

    /// <summary>The paper size.</summary>
    [ObservableProperty]
    private Choice<SheetPageKind> _selectedPage;

    partial void OnSelectedPageChanged(Choice<SheetPageKind> value)
    {
        OnPropertyChanged(nameof(IsCustomPage));
        if (SheetLayout.PageSize(value.Value) is { } size)
        {
            bool was = _applying;
            _applying = true;
            PageWidthText = Format(size.WidthMm);
            PageHeightText = Format(size.HeightMm);
            _applying = was;
        }
        FieldEdited();
    }

    /// <summary>True when the paper size is typed (the width and height boxes are enabled).</summary>
    public bool IsCustomPage => SelectedPage.Value == SheetPageKind.Custom;

    /// <summary>The fill order.</summary>
    [ObservableProperty]
    private Choice<SheetOrder> _selectedOrder;

    partial void OnSelectedOrderChanged(Choice<SheetOrder> value) => Refresh();

    /// <summary>The guides drawn on the sheet.</summary>
    [ObservableProperty]
    private Choice<SheetGuides> _selectedGuides;

    /// <summary>True when sizes are shown and typed in inches, false for millimetres.</summary>
    [ObservableProperty]
    private bool _useInches;

    partial void OnUseInchesChanged(bool value)
    {
        // The numbers are shown again in the new unit; the layout itself does not change.
        if (TryBuildLayout(!value, out var layout, out _)) ShowLayout(layout);
        OnPropertyChanged(nameof(UnitText));
    }

    /// <summary>"mm" or "in", shown next to the size boxes.</summary>
    public string UnitText => UseInches ? Loc.Get("Print.Sheet.Unit.In") : Loc.Get("Print.Sheet.Unit.Mm");

    // ------------------------------------------------------------------------------------ the numbers as typed

    /// <summary>Paper width.</summary>
    [ObservableProperty] private string _pageWidthText = string.Empty;
    /// <summary>Paper height.</summary>
    [ObservableProperty] private string _pageHeightText = string.Empty;
    /// <summary>Top margin.</summary>
    [ObservableProperty] private string _topText = string.Empty;
    /// <summary>Left margin.</summary>
    [ObservableProperty] private string _leftText = string.Empty;
    /// <summary>Label width.</summary>
    [ObservableProperty] private string _labelWidthText = string.Empty;
    /// <summary>Label height.</summary>
    [ObservableProperty] private string _labelHeightText = string.Empty;
    /// <summary>Labels across.</summary>
    [ObservableProperty] private string _columnsText = string.Empty;
    /// <summary>Labels down.</summary>
    [ObservableProperty] private string _rowsText = string.Empty;
    /// <summary>Distance between the left edges of two columns.</summary>
    [ObservableProperty] private string _horizontalPitchText = string.Empty;
    /// <summary>Distance between the top edges of two rows.</summary>
    [ObservableProperty] private string _verticalPitchText = string.Empty;

    partial void OnPageWidthTextChanged(string value) => FieldEdited();
    partial void OnPageHeightTextChanged(string value) => FieldEdited();
    partial void OnTopTextChanged(string value) => FieldEdited();
    partial void OnLeftTextChanged(string value) => FieldEdited();
    partial void OnLabelWidthTextChanged(string value) => FieldEdited();
    partial void OnLabelHeightTextChanged(string value) => FieldEdited();
    partial void OnColumnsTextChanged(string value) => FieldEdited();
    partial void OnRowsTextChanged(string value) => FieldEdited();
    partial void OnHorizontalPitchTextChanged(string value) => FieldEdited();
    partial void OnVerticalPitchTextChanged(string value) => FieldEdited();

    /// <summary>1-based position on the first sheet where the first label goes.</summary>
    [ObservableProperty]
    private string _startPositionText = "1";

    partial void OnStartPositionTextChanged(string value) => Refresh();

    /// <summary>Copies of the label (or of each record when the Data window does not set them).</summary>
    [ObservableProperty]
    private string _copiesText;

    partial void OnCopiesTextChanged(string value) => Refresh();

    // ------------------------------------------------------------------------------------ what the dialog shows

    /// <summary>The positions of the sheet for the preview.</summary>
    public ObservableCollection<SheetSlotItem> Slots { get; } = new();

    /// <summary>Width of the preview page in pixels.</summary>
    [ObservableProperty] private double _previewWidth = PreviewMaxWidth;

    /// <summary>Height of the preview page in pixels.</summary>
    [ObservableProperty] private double _previewHeight = PreviewMaxHeight;

    /// <summary>Problems and notes of the layout, one per line (empty when there is nothing to say).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private string _problemsText = string.Empty;

    /// <summary>True when <see cref="ProblemsText"/> has an error (the export is blocked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _hasErrors;

    /// <summary>True when there is a problem or a note to show.</summary>
    public bool HasProblems => ProblemsText.Length > 0;

    /// <summary>"12 labels on 2 sheets" or what is known about the count.</summary>
    [ObservableProperty]
    private string _summaryText = string.Empty;

    /// <summary>True when the PDF can be made: the layout and numbers are fine and something is selected.</summary>
    public bool CanExport => !HasErrors;

    /// <summary>The layout as it would be saved with the label, or null while the numbers have an error.</summary>
    public SheetSettings? Settings =>
        TryBuildLayout(UseInches, out var layout, out _) && layout.IsValid
            ? new SheetSettings { TemplateId = SelectedTemplate.Template?.Id, Layout = layout, Order = SelectedOrder.Value, Guides = SelectedGuides.Value }
            : null;

    // ------------------------------------------------------------------------------------ editing

    /// <summary>Makes the clicked position the start position.</summary>
    [RelayCommand]
    private void PickStart(SheetSlotItem? slot)
    {
        if (slot is null) return;
        StartPositionText = slot.Position.ToString(CultureInfo.InvariantCulture);
    }

    private void FieldEdited()
    {
        if (_applying) return;
        // A typed number that no longer matches the template makes the layout the user's own.
        if (SelectedTemplate.Template is { } t && (!TryBuildLayout(UseInches, out var layout, out _) || !Same(layout, t.Layout)))
        {
            _applying = true;
            SelectedTemplate = Templates[0];
            _applying = false;
            OnPropertyChanged(nameof(IsCustom));
        }
        Refresh();
    }

    private static bool Same(SheetLayout a, SheetLayout b)
    {
        static bool Eq(double x, double y) => Math.Abs(x - y) < 0.01;
        return a.PageKind == b.PageKind && a.Columns == b.Columns && a.Rows == b.Rows && Eq(a.PageWidthMm, b.PageWidthMm) && Eq(a.PageHeightMm, b.PageHeightMm)
            && Eq(a.TopMarginMm, b.TopMarginMm) && Eq(a.LeftMarginMm, b.LeftMarginMm) && Eq(a.LabelWidthMm, b.LabelWidthMm) && Eq(a.LabelHeightMm, b.LabelHeightMm)
            && Eq(a.HorizontalPitchMm, b.HorizontalPitchMm) && Eq(a.VerticalPitchMm, b.VerticalPitchMm);
    }

    private void ShowLayout(SheetLayout layout)
    {
        bool was = _applying;
        _applying = true;
        try
        {
            PageWidthText = Format(layout.PageWidthMm);
            PageHeightText = Format(layout.PageHeightMm);
            TopText = Format(layout.TopMarginMm);
            LeftText = Format(layout.LeftMarginMm);
            LabelWidthText = Format(layout.LabelWidthMm);
            LabelHeightText = Format(layout.LabelHeightMm);
            ColumnsText = layout.Columns.ToString(CultureInfo.InvariantCulture);
            RowsText = layout.Rows.ToString(CultureInfo.InvariantCulture);
            HorizontalPitchText = Format(layout.HorizontalPitchMm);
            VerticalPitchText = Format(layout.VerticalPitchMm);
        }
        finally { _applying = was; }
    }

    private string Format(double mm) => UseInches
        ? Math.Round(mm / SheetLayout.MmPerInch, 4).ToString("0.####", CultureInfo.InvariantCulture)
        : Math.Round(mm, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Reads the typed numbers (a comma is accepted as the decimal point) into a layout; false with the first problem when one cannot be read.</summary>
    private bool TryBuildLayout(bool inches, out SheetLayout layout, out string? error)
    {
        layout = null!;
        string? firstError = null;
        double scale = inches ? SheetLayout.MmPerInch : 1;
        bool Num(string text, string fieldKey, out double mm)
        {
            mm = 0;
            if (double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
            {
                mm = v * scale;
                return true;
            }
            firstError ??= Loc.Get("Print.Sheet.Field.Invalid", Loc.Get(fieldKey));
            return false;
        }
        bool Int(string text, string fieldKey, out int n)
        {
            if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return true;
            firstError ??= Loc.Get("Print.Sheet.Field.Invalid", Loc.Get(fieldKey));
            return false;
        }
        bool ok = Num(PageWidthText, "Print.Sheet.PageWidth", out double pw) & Num(PageHeightText, "Print.Sheet.PageHeight", out double ph)
            & Num(TopText, "Print.Sheet.Top", out double top) & Num(LeftText, "Print.Sheet.Left", out double left)
            & Num(LabelWidthText, "Print.Sheet.LabelWidth", out double lw) & Num(LabelHeightText, "Print.Sheet.LabelHeight", out double lh)
            & Int(ColumnsText, "Print.Sheet.Columns", out int cols) & Int(RowsText, "Print.Sheet.Rows", out int rows)
            & Num(HorizontalPitchText, "Print.Sheet.HorizontalPitch", out double hp) & Num(VerticalPitchText, "Print.Sheet.VerticalPitch", out double vp);
        error = firstError;
        if (!ok) return false;
        layout = new SheetLayout
        {
            PageKind = SelectedPage.Value, PageWidthMm = pw, PageHeightMm = ph, TopMarginMm = top, LeftMarginMm = left,
            LabelWidthMm = lw, LabelHeightMm = lh, Columns = cols, Rows = rows, HorizontalPitchMm = hp, VerticalPitchMm = vp,
        };
        // A named paper is its real size, whatever rounding the boxes show.
        if (SelectedPage.Value != SheetPageKind.Custom) layout = layout.WithPage(SelectedPage.Value);
        return true;
    }

    private bool TryStart(SheetLayout layout, out int start, out string? error)
    {
        error = null;
        if (int.TryParse(StartPositionText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out start) && layout.ValidateStart(start) is null) return true;
        error = Loc.FormatIssue(layout.ValidateStart(0)!);
        return false;
    }

    private bool TryCopies(out int copies)
    {
        return int.TryParse(CopiesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out copies) && copies >= 1 && copies <= DataMergeViewModel.MaxSheetLabels;
    }

    /// <summary>How many labels the export makes, or null when the data decides (copies from a column).</summary>
    private long? LabelCount(int copies) => UsesRecords ? Records!.EstimateSheetLabels(copies) : copies;

    /// <summary>Checks everything and rebuilds the preview and the summary.</summary>
    public void Refresh()
    {
        var lines = new List<string>();
        bool errors = false;
        Slots.Clear();
        if (!TryBuildLayout(UseInches, out var layout, out string? readError))
        {
            ProblemsText = readError!;
            HasErrors = true;
            SummaryText = string.Empty;
            return;
        }
        foreach (var issue in layout.Validate())
        {
            lines.Add(Loc.FormatIssue(issue));
            errors |= issue.Severity == ValidationSeverity.Error;
        }
        if (errors)
        {
            ProblemsText = string.Join(Environment.NewLine, lines);
            HasErrors = true;
            SummaryText = string.Empty;
            return;
        }
        var doc = _document();
        if (layout.CheckDesignSize(Mm(doc.WidthDots, doc.Dpi), Mm(doc.HeightDots, doc.Dpi)) is { } fit) lines.Add(Loc.FormatIssue(fit));
        bool startOk = TryStart(layout, out int start, out string? startError);
        if (!startOk) { lines.Add(startError!); errors = true; start = 1; }
        bool copiesOk = TryCopies(out int copies);
        if (!copiesOk) { lines.Add(Loc.Get("Print.Sheet.Copies.Invalid", DataMergeViewModel.MaxSheetLabels)); errors = true; copies = 1; }
        if (UsesRecords && Records!.SelectedCount == 0) { lines.Add(Loc.Get("Print.Records.NoneSelected")); errors = true; }

        long? count = LabelCount(copies);
        SummaryText = count is { } n
            ? Loc.Get("Print.Sheet.Summary", n, SheetImposition.PageCount(layout, n, start), layout.SlotCount)
            : Loc.Get("Print.Sheet.Summary.FromData", layout.SlotCount);
        BuildPreview(layout, start, count, SelectedOrder.Value);
        ProblemsText = string.Join(Environment.NewLine, lines);
        HasErrors = errors;
    }

    private static double Mm(int dots, PrinterDpi dpi) => dots * SheetLayout.MmPerInch / (int)dpi;

    private void BuildPreview(SheetLayout layout, int start, long? count, SheetOrder order)
    {
        double scale = Math.Min(PreviewMaxWidth / layout.PageWidthMm, PreviewMaxHeight / layout.PageHeightMm);
        PreviewWidth = layout.PageWidthMm * scale;
        PreviewHeight = layout.PageHeightMm * scale;
        foreach (var slot in layout.Slots(order))
        {
            bool used = slot.Position < start;
            long number = slot.Position - start + 1;
            bool filled = !used && (count is null || number <= count);
            string caption = filled ? number.ToString(CultureInfo.InvariantCulture) : string.Empty;
            string tip = used ? Loc.Get("Print.Sheet.Slot.Used", slot.Position)
                : filled ? Loc.Get("Print.Sheet.Slot.Label", slot.Position, number)
                : Loc.Get("Print.Sheet.Slot.Empty", slot.Position);
            Slots.Add(new SheetSlotItem(slot.Position, slot.XMm * scale, slot.YMm * scale, slot.WidthMm * scale, slot.HeightMm * scale, used, filled, caption, tip));
        }
    }

    // ------------------------------------------------------------------------------------ export

    /// <summary>Asks where to save, then writes the PDF.</summary>
    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (IsBusy) return;
        Refresh();
        if (HasErrors || !TryBuildLayout(UseInches, out var layout, out _) || !TryStart(layout, out int start, out _) || !TryCopies(out int copies))
        {
            SetResult(ProblemsText, true);
            return;
        }
        if (Records is { IsBusy: true }) return;
        var options = new SheetExportOptions { StartPosition = start, Order = SelectedOrder.Value, Guides = SelectedGuides.Value };
        string? path = _files.PickExportFile(ExportKind.Pdf, _documentName + ".pdf");
        if (path is null) return;
        RememberLayout();
        if (UsesRecords)
        {
            var outcome = await Records!.ExportSheetsCoreAsync(RunAsync, p => Progress = p, copies, layout, options, path);
            if (outcome is { } o) SetResult(o.Text, o.IsError);
            return;
        }
        await ExportLabelAsync(layout, options, copies, path);
    }

    /// <summary>
    /// The label without records: its copies through <see cref="LabelPrintRun"/>, exactly as the Print dialog would send them, so every copy of a
    /// label with a counter carries its own number and the numbers are confirmed only when the file is complete.
    /// </summary>
    private async Task ExportLabelAsync(SheetLayout layout, SheetExportOptions options, int copies, string path)
    {
        var doc = ElementCloner.CloneDocument(_document());
        doc.Print.Quantity = copies;
        var data = _data();
        var answers = _variables.Ask(Rotula.Core.Variables.Prompts.PromptSet.Build(doc, data, _variables.PromptMemory));
        if (answers is null) return;
        var run = LabelPrintRun.Begin(doc, data, VariableServices.Options(_variables, answers));
        if (!run.CanPrint)
        {
            SetResult(Loc.Get("Print.Error.InvalidLabel") + Environment.NewLine + PrintMessages.DescribeIssues(run.Issues), true);
            return;
        }
        SheetExportResult? result = null;
        string? writeError = null;
        var closing = new List<ValidationIssue>();
        bool ok = await RunAsync(Loc.Get("Print.Sheet.Busy"), ct => Task.Run(() =>
        {
            long written = 0;
            try
            {
                IReadOnlyDictionary<string, string>? lastValues = null;
                RenderLabel? lastLabel = null;
                IEnumerable<RenderLabel> Labels()
                {
                    foreach (var values in run.LabelValues())
                    {
                        ct.ThrowIfCancellationRequested();
                        // Copies without a counter share one dictionary, so the design is converted once.
                        if (lastLabel is null || !ReferenceEquals(values, lastValues))
                        {
                            lastLabel = LabelDocumentAdapter.ToRenderLabelResolved(doc, values);
                            lastValues = values;
                        }
                        yield return lastLabel;
                    }
                }
                result = SheetExporter.SavePdf(layout, Labels(), path, options);
                if (result.Pages > 0 && !run.StoppedEarly) written = result.Labels;
                else if (run.StoppedEarly && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writeError = Loc.Get("File.Error.Write", path);
            }
            finally
            {
                // Only a complete file uses up its serial numbers; anything else gives them back.
                closing.AddRange(run.Complete(written));
            }
        }, ct));
        if (!ok) return;
        string notes = string.Join(Environment.NewLine, run.Issues.Where(i => i.Severity != ValidationSeverity.Error).Concat(closing).Select(Loc.FormatIssue));
        if (writeError is not null) { SetResult(writeError, true); return; }
        if (run.StoppedEarly)
        {
            SetResult(Loc.Get("Print.Error.InvalidLabel") + Environment.NewLine + PrintMessages.DescribeIssues(run.Issues), true);
            return;
        }
        if (result is null || result.Pages == 0) { SetResult(Loc.Get("Data.Batch.NothingProduced"), true); return; }
        string text = Loc.Get("Print.Sheet.Done", result.Labels, result.Pages, Path.GetFileName(path));
        if (result.Issues.Count > 0) text += Environment.NewLine + string.Join(Environment.NewLine, result.Issues.Select(i => i.Message).Take(3));
        SetResult(notes.Length == 0 ? text : text + Environment.NewLine + notes, false);
    }

    /// <summary>Hands a valid layout to the label, so it is saved with it. Called when the dialog closes, however it is closed.</summary>
    public void RememberLayout()
    {
        if (Settings is { } settings) _remember?.Invoke(settings);
    }

    /// <summary>Closes the dialog.</summary>
    [RelayCommand]
    private void Close()
    {
        RememberLayout();
        RequestClose();
    }

    // ------------------------------------------------------------------------------------ names

    /// <summary>The name of a template, from its dimensions: "Letter, 30 per sheet (3 x 10), 2.625 x 1 in".</summary>
    internal static string NameOf(SheetTemplate t)
    {
        var l = t.Layout;
        string size = t.Inches
            ? Loc.Get("Print.Sheet.Size.In", Inch(l.LabelWidthMm), Inch(l.LabelHeightMm))
            : Loc.Get("Print.Sheet.Size.Mm", MmText(l.LabelWidthMm), MmText(l.LabelHeightMm));
        return Loc.Get("Print.Sheet.Template.Name", Loc.Get("Enum.SheetPageKind." + l.PageKind), l.SlotCount, l.Columns, l.Rows, size);
    }

    private static string Inch(double mm) => Math.Round(mm / SheetLayout.MmPerInch, 3).ToString("0.###", CultureInfo.CurrentCulture);

    private static string MmText(double mm) => Math.Round(mm, 1).ToString("0.#", CultureInfo.CurrentCulture);
}
