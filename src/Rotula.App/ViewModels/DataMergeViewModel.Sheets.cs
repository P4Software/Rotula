using System.Globalization;
using Rotula.App.Localization;
using Rotula.Core.Merge;
using Rotula.Core.Sheets;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Rendering;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;

namespace Rotula.App.ViewModels;

/// <summary>The ticked records written onto label sheets as a PDF (the "Label sheets" dialog uses this when data is loaded).</summary>
public sealed partial class DataMergeViewModel
{
    /// <summary>Most labels one sheet export writes; beyond it the PDF would be too large to print or even open.</summary>
    internal const long MaxSheetLabels = 100_000;

    /// <summary>
    /// How many labels the ticked records make with <paramref name="labelCopies"/> copies each, or null when a column of the data sets the copies (then
    /// the number is only known once the records are read).
    /// </summary>
    internal long? EstimateSheetLabels(int labelCopies) => CopiesSource switch
    {
        CopiesSource.Column => null,
        CopiesSource.Fixed => int.TryParse(FixedCopiesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1
            ? (long)SelectedCount * n
            : SelectedCount,
        _ => (long)SelectedCount * Math.Max(1, labelCopies),
    };

    /// <summary>
    /// Writes the ticked records onto sheets as a PDF, through the same record iteration as printing (<see cref="MergeBatch.Labels"/>): the same
    /// selection, copies, skipped invalid records and print-time questions, and the serial numbers of the batch. A PDF will be printed, so its serial
    /// numbers count as used once the file is complete; a failed or cancelled export gives them back. Every copy of a record carries that record's
    /// values, as the copies of a printed record do.
    /// </summary>
    /// <param name="run">The host window's single-operation runner.</param>
    /// <param name="progress">Receives 0-100 while records are read.</param>
    /// <param name="labelCopies">Copies per record when the copies setting is the label's own quantity; null keeps the label's own.</param>
    /// <param name="layout">The sheet (already checked).</param>
    /// <param name="options">Start position, order and guides.</param>
    /// <param name="path">The PDF file to write.</param>
    internal async Task<BatchOutcome?> ExportSheetsCoreAsync(Func<string, Func<CancellationToken, Task>, Task<bool>> run, Action<double> progress,
        int? labelCopies, SheetLayout layout, SheetExportOptions options, string path)
    {
        // The PDF keeps the design's own resolution: it is drawn, not sent to a label printer.
        string? error = Snapshot(out var doc, out var ds, convertToPrinter: false, quantity: labelCopies);
        if (error is not null) return new BatchOutcome(error, true);
        if (!AskBatchPrompts(ds)) return null;

        VariableJob? serials = null;
        if (VariableEngine.HasDefinitions(doc))
        {
            serials = VariableJob.Begin(doc, ds.CreateBatchContext(), ds.BatchCount);
            ds.Options.Job = serials;
        }
        var report = new MergeReport();
        var closing = new List<ValidationIssue>();
        SheetExportResult? result = null;
        string? writeError = null;
        bool tooMany = false;
        var progressReporter = new Progress<double>(progress);
        bool ok = await run(Loc.Get("Print.Sheet.Busy"), ct => Task.Run(() =>
        {
            int lastItem = 0, writtenThrough = 0;
            long count = 0;
            IEnumerable<RenderLabel> Labels()
            {
                foreach (var merged in MergeBatch.Labels(doc, ds, report))
                {
                    ct.ThrowIfCancellationRequested();
                    // Converted once per record: the copies are the same picture.
                    var label = LabelDocumentAdapter.ToRenderLabelResolved(doc, merged.Values);
                    for (int c = 0; c < merged.Copies; c++)
                    {
                        if (++count > MaxSheetLabels) { tooMany = true; yield break; }
                        yield return label;
                    }
                    lastItem = merged.ItemNumber;
                    if (report.Processed % 64 == 0) ((IProgress<double>)progressReporter).Report(100.0 * report.Processed / ds.BatchCount);
                }
            }
            try
            {
                result = SheetExporter.SavePdf(layout, Labels(), path, options);
                if (tooMany)
                {
                    // Cut short: the file is not what the operator asked for, so it is removed and every number goes back.
                    if (File.Exists(path)) File.Delete(path);
                    result = null;
                }
                else if (result.Pages > 0) writtenThrough = lastItem;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writeError = Loc.Get("File.Error.Write", path);
            }
            finally
            {
                if (serials is not null) closing.AddRange(serials.Complete(writtenThrough));
            }
        }, ct));
        if (!ok) return null;
        ShowReport(report);
        string notes = string.Join(" ", closing.Select(Loc.FormatIssue));
        if (writeError is not null) return new BatchOutcome(writeError, true);
        if (tooMany) return new BatchOutcome(Loc.Get("Print.Sheet.TooMany", MaxSheetLabels), true);
        if (result is null || result.Pages == 0) return new BatchOutcome(Loc.Get("Data.Batch.NothingProduced"), true);
        string text = Loc.Get("Print.Sheet.Done", result.Labels, result.Pages, Path.GetFileName(path));
        if (report.SkippedRecords.Count > 0) text += " " + Loc.Get("Data.Batch.Skipped", report.SkippedRecords.Count);
        if (report.Stopped && report.SkippedRecords.Count > 0) text += " " + Loc.Get("Data.Batch.Stopped", report.SkippedRecords[^1]);
        if (result.Issues.Count > 0) text += " " + string.Join(" ", result.Issues.Select(i => i.Message).Take(3));
        return new BatchOutcome(text + (notes.Length > 0 ? " " + notes : string.Empty), report.HasErrors);
    }
}
