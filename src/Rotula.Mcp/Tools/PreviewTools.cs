using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Data;
using Rotula.Mcp.Hosting;
using Rotula.Mcp.Security;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Mcp.Tools;

/// <summary>Tools that show the label as an image and preview data merges.</summary>
[McpServerToolType]
public sealed class PreviewTools
{
    /// <summary>Said in every preview so nobody mistakes it for the printed result.</summary>
    public const string ApproximateNote =
        "This preview is approximate: text is drawn with stand-in fonts, so line widths differ slightly from the printer's own fonts. " +
        "Bars, QR codes, boxes, lines and images follow the same data as the ZPL. Only a printed test label proves the final result.";

    private const int DefaultPreviewSide = 1000;
    private const int MaxPreviewSide = 2000;

    private readonly McpContext _ctx;

    /// <summary>Creates the tool set over the shared context.</summary>
    public PreviewTools(McpContext ctx) => _ctx = ctx;

    /// <summary>Renders the label to a PNG.</summary>
    [McpServerTool(Name = "render_preview", ReadOnly = false, Destructive = false)]
    [Description("Draws the current label as a PNG image (returned as an image, with its size). The preview is APPROXIMATE: text uses stand-in fonts, so exact text widths differ from the printer. Optionally also saves the PNG in an allowed folder. Sample values are used for variables.")]
    public CallToolResult RenderPreview(
        [Description("Longest side of the image in pixels (100 to 2000). Default 1000; large labels are scaled down to fit.")] int maxPixels = DefaultPreviewSide,
        [Description("Optional .png file to write in an allowed folder.")] string? path = null,
        [Description("Replace the .png file if it exists. Default false.")] bool overwrite = false)
    {
        var doc = _ctx.Session.GetCopy().Document;
        return Render(doc, null, maxPixels, path, overwrite, extra: null);
    }

    /// <summary>Previews one record of a data source on the label.</summary>
    [McpServerTool(Name = "merge_preview", ReadOnly = false, Destructive = false)]
    [Description("Fills the label's {{variables}} from one record of a data source and shows the result: the record values, which variables found no matching column, the merged ZPL and an approximate PNG. The source is a .csv or .xlsx file in an allowed folder, or a saved database connection plus a read-only SELECT (only when databases are enabled). Column names match variable names; the header row must come first. Counts records from 1.")]
    public CallToolResult MergePreview(
        [Description("A .csv or .xlsx file in an allowed folder. Use this, or connection and query.")] string? source = null,
        [Description("Record number to preview, counting from 1. Default 1.")] int record = 1,
        [Description("Worksheet name for .xlsx files. Default: the first sheet.")] string? sheet = null,
        [Description("CSV delimiter: \"comma\", \"semicolon\" or \"tab\". Default: detected.")] string? delimiter = null,
        [Description("Whether the first row holds column names. Default true.")] bool hasHeader = true,
        [Description("Name of a saved database connection (databases must be enabled).")] string? connection = null,
        [Description("A read-only SELECT for the connection.")] string? query = null,
        [Description("Longest side of the image in pixels (100 to 2000). Default 1000.")] int maxPixels = DefaultPreviewSide)
    {
        var doc = _ctx.Session.GetCopy().Document;
        MergeTable table;
        string label;
        if (source is not null && connection is null)
        {
            string ext = Path.GetExtension(source).ToLowerInvariant();
            string full = _ctx.Guard.Resolve(source, [".csv", ".xlsx"], mustExist: true);
            label = Path.GetFileName(full);
            try
            {
                if (ext == ".csv")
                {
                    var opts = new CsvOptions { HasHeader = hasHeader, Delimiter = ParseDelimiter(delimiter) };
                    table = CsvReader.Parse(SafeFiles.ReadText(full), opts);
                }
                else
                {
                    SafeFiles.CheckWorkbook(full);
                    table = XlsxReader.ReadFile(full, new XlsxOptions { SheetName = sheet, HasHeader = hasHeader });
                }
            }
            catch (Exception ex) when (ex is not McpException && ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                throw new McpException($"The data file could not be read ({ex.GetType().Name}). Check that it is a valid {ext} file.");
            }
        }
        else if (connection is not null && query is not null && source is null)
        {
            var q = DataTools.RunQuery(_ctx, connection, query, _ctx.Settings.MaxDatabaseRows, CancellationToken.None);
            table = new MergeTable(q.Columns, q.Rows.Select(r => r.ToArray()));
            label = $"database connection \"{connection}\"";
        }
        else
        {
            throw new McpException("Give either source (a .csv or .xlsx file) or both connection and query.");
        }

        if (table.RowCount == 0) throw new McpException("The data source has no records.");
        if (record < 1 || record > table.RowCount)
            throw new McpException($"Record {record} does not exist; the source has {table.RowCount} record(s). Counting starts at 1.");

        var set = new MergeDataSet(doc, table);
        var data = set.GetRecordData(record - 1);
        var unmapped = set.Placeholders.Where(p => !p.StartsWith('#') && !set.Mapping.ContainsKey(p)).ToList();
        var result = ZplGenerator.Generate(doc, data);
        var issues = result.Issues.Select(McpContext.ToInfo).ToList();
        var recordValues = data.ToDictionary(kv => kv.Key, kv => Clip(kv.Value), StringComparer.OrdinalIgnoreCase);
        var info = new MergePreviewResult(label, record, table.RowCount, table.Columns, recordValues, unmapped, issues, result.Zpl,
            unmapped.Count == 0 ? "All variables found a column." : "Variables without a matching column keep their sample values. Rename the columns or the variables so they match.");
        return Render(doc, data, maxPixels, null, false, info);
    }

    // ---- rendering -------------------------------------------------------------------------

    private CallToolResult Render(LabelDocument doc, IReadOnlyDictionary<string, string>? data, int maxPixels, string? path, bool overwrite, object? extra)
    {
        if (maxPixels < 100 || maxPixels > MaxPreviewSide)
            throw new McpException($"maxPixels must be between 100 and {MaxPreviewSide}.");
        var label = LabelDocumentAdapter.ToRenderLabel(doc, data);
        if (label.WidthDots < 1 || label.HeightDots < 1)
            throw new McpException("The label has no size to draw.");
        // Large labels (up to 9999 x 32000 dots) are scaled down so the preview stays small and cheap to produce.
        double scale = Math.Min(1.0, maxPixels / (double)Math.Max(label.WidthDots, label.HeightDots));

        byte[] png;
        int widthPx, heightPx;
        IReadOnlyList<RenderIssue> renderIssues;
        using (var rendered = LabelRenderer.RenderBitmap(label, new RenderOptions { Scale = scale }))
        {
            using var image = SKImage.FromBitmap(rendered.Bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new McpException("The preview image could not be created.");
            png = encoded.ToArray();
            widthPx = rendered.Bitmap.Width;
            heightPx = rendered.Bitmap.Height;
            renderIssues = rendered.Issues;
        }

        string? saved = null;
        if (path is not null)
        {
            string full = _ctx.Guard.Resolve(path, [".png"], mustExist: false);
            if (File.Exists(full) && !overwrite)
                throw new McpException($"{Path.GetFileName(full)} already exists. Choose another name or set overwrite to true.");
            SafeFiles.WriteAtomic(full, png, _ctx.Settings.MaxFileBytes);
            saved = full;
        }

        var summary = new Dictionary<string, object?>
        {
            ["approximate"] = true,
            ["note"] = ApproximateNote,
            ["widthPixels"] = widthPx,
            ["heightPixels"] = heightPx,
            ["labelWidthDots"] = label.WidthDots,
            ["labelHeightDots"] = label.HeightDots,
            ["dpi"] = label.Dpi,
            ["scale"] = Math.Round(scale, 4),
            ["savedTo"] = saved,
            ["drawingProblems"] = renderIssues.Select(i => i.Message).Take(20).ToList(),
        };
        if (extra is not null) summary["merge"] = extra;

        var text = $"Preview {widthPx} x {heightPx} px of a {label.WidthDots} x {label.HeightDots} dot label ({label.Dpi} DPI). {ApproximateNote}"
                   + (saved is null ? "" : $" Saved to {saved}.");
        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(png, "image/png"),
                new TextContentBlock { Text = text },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        };
    }

    private static char? ParseDelimiter(string? delimiter) => delimiter?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => null,
        "comma" or "," => ',',
        "semicolon" or ";" => ';',
        "tab" or "\\t" => '\t',
        _ => throw new McpException("The delimiter must be \"comma\", \"semicolon\" or \"tab\"."),
    };

    private static string Clip(string s) => s.Length > 500 ? s[..500] + "..." : s;
}
