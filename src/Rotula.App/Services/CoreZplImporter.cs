using Rotula.App.Localization;
using Rotula.Core.Zpl.Import;

namespace Rotula.App.Services;

/// <summary>
/// The real ZPL importer: runs the parser from Rotula.Core and turns its structured warnings into sentences in the
/// current language (the parser never carries UI text).
/// </summary>
public sealed class CoreZplImporter : IZplImporter
{
    /// <inheritdoc />
    public ZplImportResult Import(byte[] zpl)
    {
        // ZPL does not say which printhead it was written for; 203 DPI is the most common and can be changed in the label
        // properties afterwards (positions are in dots, so they stay as they are).
        var result = ZplParser.Parse(zpl, new ZplParseOptions());
        var warnings = result.Warnings.Select(w =>
        {
            string text = Loc.FormatIssue(w.ToValidationIssue());
            return w.Line > 0 ? Loc.Get("Import.Warning.Line", w.Line, text) : text;
        }).ToList();
        return ZplImportResult.Success(result.Document, warnings);
    }
}
