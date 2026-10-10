using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using Rotula.Files;
using Rotula.Mcp.Hosting;
using Rotula.Mcp.Security;
using Rotula.Mcp.Session;

namespace Rotula.Mcp.Tools;

/// <summary>Tools that create, open, save, inspect and convert the working label.</summary>
[McpServerToolType]
public sealed class LabelTools
{
    private readonly McpContext _ctx;

    /// <summary>Creates the tool set over the shared context.</summary>
    public LabelTools(McpContext ctx) => _ctx = ctx;

    private static readonly string[] LabelExt = [".rotula"];
    private static readonly string[] ZplExt = [".zpl"];

    // ---- server and files ------------------------------------------------------------------

    /// <summary>Describes what this server may do right now.</summary>
    [McpServerTool(Name = "get_server_info", ReadOnly = true, UseStructuredContent = true)]
    [Description("Shows what this Rotula server is allowed to do: which folders it can read and write, whether printing and databases are switched on, and the limits. Call this first if a file or print request is refused.")]
    public ServerInfoResult GetServerInfo()
    {
        var s = _ctx.Settings;
        var version = typeof(LabelTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
        return new ServerInfoResult(version.Split('+')[0], _ctx.Guard.Roots, s.AllowPrinting, s.AllowedPrinters, s.AllowDatabase,
            s.MaxElements, s.MaxFileBytes, s.MaxCopies, s.MaxDatabaseRows,
            "Positions and sizes are in the label's unit (mm, in or dots) unless a tool's unit argument says otherwise. 0,0 is the top-left corner inside the margins.",
            _ctx.Session.HasLabel);
    }

    /// <summary>Lists label-related files in the allowed folders.</summary>
    [McpServerTool(Name = "list_label_files", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists the .rotula, .zpl, .png, .csv, .xlsx and .pdf files directly inside the allowed folders (and one level of sub-folders), newest first, so you can pick a path for open_label, import_zpl, add_image or merge_preview.")]
    public FileListResult ListLabelFiles()
    {
        var files = new List<FileEntry>();
        foreach (string root in _ctx.Guard.Roots)
        {
            foreach (var dir in new[] { root }.Concat(EnumDirs(root)))
            {
                foreach (string file in EnumFiles(dir))
                {
                    if (files.Count >= 500) break;
                    if (!_ctx.Guard.IsAllowed(file, PathGuard.GlobalExtensions, mustExist: false)) continue;
                    var info = new FileInfo(file);
                    files.Add(new FileEntry(file, info.Length, info.LastWriteTimeUtc));
                }
            }
        }
        var ordered = files.OrderByDescending(f => f.ModifiedUtc).Take(200).ToList();
        return new FileListResult(_ctx.Guard.Roots, ordered, files.Count > 200);
    }

    private static IEnumerable<string> EnumDirs(string root)
    {
        // A sub-folder that is a link or junction is skipped without being opened: it could lead outside the allowed folders (or to a network share).
        try { return Directory.EnumerateDirectories(root).Where(d => new DirectoryInfo(d).LinkTarget is null).Take(50).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> EnumFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Take(500).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    // ---- create / open / save --------------------------------------------------------------

    /// <summary>Starts a new empty label.</summary>
    [McpServerTool(Name = "create_label", Destructive = false, UseStructuredContent = true)]
    [Description("Creates a new empty label of the given size and makes it the current label (any label already open is replaced, but undo_last brings it back). Common sizes: 100 x 150 mm shipping label, 100 x 50 mm, 4 x 6 in. Printer resolution is 203, 300 or 600 DPI.")]
    public ChangeResult CreateLabel(
        [Description("Label width in the given unit.")] double width,
        [Description("Label height in the given unit.")] double height,
        [Description("Unit of width and height: \"mm\" (default), \"in\" or \"dots\".")] string? unit = null,
        [Description("Printer resolution in DPI: 203 (default), 300 or 600.")] int dpi = 203,
        [Description("A name for the label; it becomes the default file name when saving.")] string? name = null)
    {
        string u = Units.Normalize(unit ?? "mm", null);
        var doc = new LabelDocument
        {
            Dpi = Units.ParseDpi(dpi),
            Width = Units.ToMeasure(CheckFinite(width, "width"), u),
            Height = Units.ToMeasure(CheckFinite(height, "height"), u),
        };
        CheckLabelSize(doc);
        string label = CleanName(name) ?? "Untitled";
        _ctx.Session.Replace(doc, label, null, null, "create_label", dirty: true);
        _ctx.Gate.Forget();
        return LabelView.Change(_ctx, $"Created a {width:0.##} x {height:0.##} {u} label at {dpi} DPI ({doc.WidthDots} x {doc.HeightDots} dots).");
    }

    /// <summary>Opens a .rotula file.</summary>
    [McpServerTool(Name = "open_label", Destructive = false, UseStructuredContent = true)]
    [Description("Opens a .rotula label file from an allowed folder and makes it the current label (any label already open is replaced, but undo_last brings it back). The path is absolute or just a file name inside the first allowed folder.")]
    public ChangeResult OpenLabel([Description("Path of the .rotula file.")] string path)
    {
        string full = _ctx.Guard.Resolve(path, LabelExt, mustExist: true);
        RotulaFileContent content;
        try
        {
            content = RotulaFileSerializer.DeserializeFile(SafeFiles.ReadText(full));
        }
        catch (RotulaFileException ex)
        {
            throw new McpException(Messages.Format(ex.MessageKey, ex.MessageKey, ex.Args));
        }
        _ctx.Session.Replace(content.Document, Path.GetFileNameWithoutExtension(full), full, content.Merge, "open_label", dirty: false);
        _ctx.Gate.Forget();
        return LabelView.Change(_ctx, $"Opened {Path.GetFileName(full)} with {content.Document.Elements.Count} elements.");
    }

    /// <summary>Saves the working label.</summary>
    [McpServerTool(Name = "save_label", Destructive = true, UseStructuredContent = true)]
    [Description("Saves the current label as a .rotula file in an allowed folder. Without a path the label name is used in the first allowed folder. An existing file is only replaced when overwrite is true (or when saving back to the file the label was opened from). The file is written atomically.")]
    public SimpleResult SaveLabel(
        [Description("Where to save: absolute path or file name. Optional.")] string? path = null,
        [Description("Replace an existing file. Default false.")] bool overwrite = false)
    {
        var copy = _ctx.Session.GetCopy();
        string target = path ?? SuggestFileName(copy.Name);
        string full = _ctx.Guard.Resolve(target, LabelExt, mustExist: false);
        bool sameFile = copy.SourcePath is not null && string.Equals(copy.SourcePath, full, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(full) && !overwrite && !sameFile)
            throw new McpException($"{Path.GetFileName(full)} already exists. Choose another name, or call save_label again with overwrite set to true to replace it.");
        string json = RotulaFileSerializer.Serialize(copy.Document, copy.Merge);
        SafeFiles.WriteTextAtomic(full, json, _ctx.Settings.MaxFileBytes);
        _ctx.Session.MarkSaved(full);
        return new SimpleResult($"Saved {full} ({json.Length:N0} characters).");
    }

    // ---- inspect ---------------------------------------------------------------------------

    /// <summary>Returns the label summary and all elements.</summary>
    [McpServerTool(Name = "get_label", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns the current label: size, resolution, margins, variables, and every element in drawing order with its id, type, position and settings. Use the ids with update_element, move_element, delete_element, duplicate_element and reorder_element.")]
    public GetLabelResult GetLabel()
    {
        var s = _ctx.Session.GetCopy();
        return new GetLabelResult(LabelView.Describe(_ctx, s.Document, s.Name, s.SourcePath, s.Dirty), LabelView.Elements(s.Document));
    }

    /// <summary>Changes label-level settings.</summary>
    [McpServerTool(Name = "set_label_settings", Destructive = false, UseStructuredContent = true)]
    [Description("Changes label-level settings: size, margins, orientation, media type and tracking, default rotation and font, the number of copies printed (^PQ), or the name. Only the values you pass change. Resolution is not changed here; use convert_dpi to move a layout to another printer resolution.")]
    public ChangeResult SetLabelSettings(
        [Description("New width in `unit`.")] double? width = null,
        [Description("New height in `unit`.")] double? height = null,
        [Description("Unit for width, height and margins: \"mm\", \"in\" or \"dots\". If given on its own, the label is re-expressed in that unit.")] string? unit = null,
        [Description("Left margin; element positions start inside it.")] double? marginLeft = null,
        [Description("Top margin.")] double? marginTop = null,
        [Description("Right margin (used for off-label warnings).")] double? marginRight = null,
        [Description("Bottom margin (used for off-label warnings).")] double? marginBottom = null,
        [Description("\"Normal\" or \"Inverted\" (whole label printed rotated 180 degrees).")] string? orientation = null,
        [Description("\"ThermalTransfer\" or \"DirectThermal\".")] string? mediaType = null,
        [Description("\"Continuous\", \"Web\" (gaps) or \"Mark\" (black mark).")] string? mediaTracking = null,
        [Description("Default rotation of fields: 0, 90, 180 or 270.")] string? defaultRotation = null,
        [Description("Default font id (0 is the scalable font).")] string? defaultFontId = null,
        [Description("Default font height in `unit`.")] double? defaultFontHeight = null,
        [Description("Default font width in `unit` (0 = proportional).")] double? defaultFontWidth = null,
        [Description("Number of labels to print per job (1-99999999).")] int? printQuantity = null,
        [Description("New name of the label.")] string? name = null)
    {
        _ctx.Session.Mutate("set_label_settings", doc =>
        {
            string u = Units.Normalize(unit, doc);
            bool sizeGiven = width is not null || height is not null || unit is not null;
            if (sizeGiven)
            {
                double w = width ?? Units.Express(doc.Width, u, doc.Dpi);
                double h = height ?? Units.Express(doc.Height, u, doc.Dpi);
                doc.Width = Units.ToMeasure(CheckFinite(w, "width"), u);
                doc.Height = Units.ToMeasure(CheckFinite(h, "height"), u);
                // Keep the margins in the same unit as the size so "the label's unit" stays one thing.
                doc.Margins.Left = Restate(doc.Margins.Left, u, doc);
                doc.Margins.Top = Restate(doc.Margins.Top, u, doc);
                doc.Margins.Right = Restate(doc.Margins.Right, u, doc);
                doc.Margins.Bottom = Restate(doc.Margins.Bottom, u, doc);
            }
            string mu = Units.OfLabel(doc);
            if (marginLeft is not null) doc.Margins.Left = MarginOf(marginLeft.Value, u, mu, doc);
            if (marginTop is not null) doc.Margins.Top = MarginOf(marginTop.Value, u, mu, doc);
            if (marginRight is not null) doc.Margins.Right = MarginOf(marginRight.Value, u, mu, doc);
            if (marginBottom is not null) doc.Margins.Bottom = MarginOf(marginBottom.Value, u, mu, doc);
            if (orientation is not null) doc.Orientation = ElementModel.ParseEnum<LabelOrientation>(orientation, "orientation");
            if (mediaType is not null) doc.MediaType = ElementModel.ParseEnum<MediaType>(mediaType, "media type");
            if (mediaTracking is not null) doc.MediaTracking = ElementModel.ParseEnum<MediaTracking>(mediaTracking, "media tracking");
            if (defaultRotation is not null) doc.DefaultRotation = ElementModel.ParseRotation(defaultRotation);
            if (defaultFontId is not null || defaultFontHeight is not null || defaultFontWidth is not null)
            {
                var f = doc.DefaultFont ?? new FontSpec('0', 30, 30);
                if (defaultFontId is not null)
                    f.FontId = defaultFontId.Length == 1 && char.IsAsciiLetterOrDigit(defaultFontId[0]) ? char.ToUpperInvariant(defaultFontId[0]) : throw new McpException("The default font id must be one letter or digit.");
                if (defaultFontHeight is not null) f.Height = ElementModel.CheckRange("defaultFontHeight", Units.ToDots(defaultFontHeight.Value, u, doc.Dpi), 1, 32000);
                if (defaultFontWidth is not null) f.Width = ElementModel.CheckRange("defaultFontWidth", Units.ToDots(defaultFontWidth.Value, u, doc.Dpi), 0, 32000);
                doc.DefaultFont = f;
            }
            if (printQuantity is not null) doc.Print.Quantity = printQuantity is >= 1 and <= 99_999_999 ? printQuantity.Value : throw new McpException("printQuantity must be between 1 and 99,999,999.");
            CheckLabelSize(doc);
            return 0;
        });
        if (name is not null) _ctx.Session.Rename(CleanName(name) ?? "Untitled");
        _ctx.Gate.Forget();
        return LabelView.Change(_ctx, "Label settings updated.");
    }

    /// <summary>Declares or updates a variable.</summary>
    [McpServerTool(Name = "set_variable", Destructive = false, UseStructuredContent = true)]
    [Description("Declares a variable (or changes its sample value). Variables are used as {{name}} inside text and barcode data and are filled from data files when merging; the sample value is what shows when no data is merged.")]
    public ChangeResult SetVariable(
        [Description("Variable name: letters, digits and underscore.")] string name,
        [Description("The value shown when no data is merged.")] string sampleValue = "")
    {
        ElementModel.RequireVariableName(name);
        _ctx.Session.Mutate("set_variable", doc =>
        {
            var v = doc.Variables.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (v is null) doc.Variables.Add(new LabelVariable(name, ElementModel.Clip(sampleValue, 1000)));
            else v.SampleValue = ElementModel.Clip(sampleValue, 1000);
            return 0;
        });
        return LabelView.Change(_ctx, $"Variable {name} set.");
    }

    /// <summary>Undoes the last change.</summary>
    [McpServerTool(Name = "undo_last", Destructive = true, UseStructuredContent = true)]
    [Description("Undoes the most recent change to the label (adding, editing or deleting an element, changing settings, creating, opening, importing or converting). Up to 20 steps are remembered. Call it again to go back further.")]
    public SimpleResult UndoLast()
    {
        var outcome = _ctx.Session.Undo();
        _ctx.Gate.Forget();
        string state = outcome.HasLabel ? "" : " No label is open now.";
        return new SimpleResult($"Undid: {outcome.Undone}. {outcome.StepsLeft} earlier step(s) can still be undone.{state}");
    }

    // ---- symbologies, validate, zpl --------------------------------------------------------

    /// <summary>Lists supported barcodes.</summary>
    [McpServerTool(Name = "list_symbologies", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists all supported barcode types (28) with the ZPL command each becomes, what data each accepts, example data that is valid, the options that apply (and whether each is an argument or goes in the options object), and what the preview image cannot show. Use the Name value as the symbology argument of add_barcode.")]
    public SymbologyList ListSymbologies()
    {
        var list = BarcodeCatalog.All.Select(info =>
        {
            var details = new List<BarcodeOptionInfo>();
            foreach (var option in info.Options)
            {
                var def = BarcodeOptionTable.All.FirstOrDefault(d => d.Option == option);
                details.Add(def is not null
                    ? new BarcodeOptionInfo(def.Name, "options", def.Description)
                    : new BarcodeOptionInfo(BarcodeOptionTable.ToolName(option), "argument", DirectOptionText(option)));
            }
            string kind = info.Category switch
            {
                BarcodeCategory.Matrix2D => "2D",
                BarcodeCategory.Stacked => "stacked",
                BarcodeCategory.Postal => "postal",
                _ => "linear",
            };
            string rules = info.CharacterSet + ". " + info.LengthRule + ".";
            string preview = BarcodeCatalog.GetPreviewLimitation(new BarcodeElement { Symbology = info.Symbology }) ?? info.PreviewNote ?? "exact";
            string optionNames = string.Join(", ", details.Select(d => d.Name));
            if (info.AlwaysPrintsHumanReadable) optionNames += (optionNames.Length > 0 ? ", " : "") + "(the text line is always printed)";
            return new SymbologyInfo(info.Symbology.ToString(), info.ZplCommand, kind, rules, optionNames, info.ExampleData, preview, info.SupportsRotation, details);
        }).ToList();
        return new SymbologyList(list);
    }

    private static string DirectOptionText(BarcodeOption option) => option switch
    {
        BarcodeOption.Height => "Bar height (row height for stacked codes), in the label's unit.",
        BarcodeOption.ModuleWidth => "Narrow bar or module width in dots, 1-10.",
        BarcodeOption.WideBarRatio => "Wide to narrow bar ratio, 2.0 to 3.0.",
        BarcodeOption.HumanReadable => "Print the text line.",
        BarcodeOption.HumanReadablePosition => "Below or Above.",
        BarcodeOption.CheckDigit => "Ask the printer to add or print a check digit.",
        BarcodeOption.Code128Mode => "None, Ucc, Automatic or Ean.",
        BarcodeOption.QrErrorCorrection => "Low, Medium, Quartile or High.",
        BarcodeOption.QrModel => "QR model 1 or 2.",
        BarcodeOption.ModuleSize => "Module size in dots, 1-10.",
        BarcodeOption.Columns => "Columns (Data Matrix, PDF417) or characters per row (Codablock); 0 = automatic.",
        BarcodeOption.Rows => "Rows; 0 = automatic.",
        BarcodeOption.Pdf417SecurityLevel => "PDF417 error correction level, 1-8.",
        BarcodeOption.Pdf417Truncate => "PDF417 compact variant.",
        _ => option.ToString(),
    };

    /// <summary>Validates the label.</summary>
    [McpServerTool(Name = "validate_label", ReadOnly = true, UseStructuredContent = true)]
    [Description("Checks the current label the way the printing path does: barcode data against each symbology's rules and check digits, sizes and ranges, unknown variables, text that cannot be encoded, and elements that fall outside the label. Returns errors (cannot print correctly) and warnings (may not look as intended), each with the element id.")]
    public ValidationResult ValidateLabel()
    {
        var doc = _ctx.Session.GetCopy().Document;
        var issues = McpContext.Validate(doc);
        int errors = issues.Count(i => i.Severity == "error");
        int warnings = issues.Count - errors;
        string summary = issues.Count == 0
            ? "No problems found."
            : $"{errors} error(s) and {warnings} warning(s). Fix errors before printing; warnings are worth a look.";
        return new ValidationResult(errors == 0, errors, warnings, issues, summary);
    }

    /// <summary>Generates ZPL.</summary>
    [McpServerTool(Name = "generate_zpl", ReadOnly = false, Destructive = false, UseStructuredContent = true)]
    [Description("Generates the ZPL II program for the current label (UTF-8, ^CI28, plain ASCII output with hex escapes for accents). Elements with errors are left out and listed in issues. Optionally writes the ZPL to a .zpl file in an allowed folder. Nothing is sent to a printer.")]
    public ZplResult GenerateZpl(
        [Description("Optional .zpl file to write in an allowed folder.")] string? path = null,
        [Description("Replace the .zpl file if it exists. Default false.")] bool overwrite = false)
    {
        var doc = _ctx.Session.GetCopy().Document;
        var result = ZplGenerator.Generate(doc);
        string? saved = null;
        if (path is not null)
        {
            string full = _ctx.Guard.Resolve(path, ZplExt, mustExist: false);
            if (File.Exists(full) && !overwrite)
                throw new McpException($"{Path.GetFileName(full)} already exists. Choose another name or set overwrite to true.");
            SafeFiles.WriteTextAtomic(full, result.Zpl, _ctx.Settings.MaxFileBytes);
            saved = full;
        }
        return new ZplResult(result.Zpl, result.HasErrors, result.Issues.Select(McpContext.ToInfo).ToList(),
            result.Zpl.Count(c => c == '\n'), result.Zpl.Length, saved);
    }

    /// <summary>Imports ZPL.</summary>
    [McpServerTool(Name = "import_zpl", Destructive = false, UseStructuredContent = true)]
    [Description("Turns existing ZPL II into an editable label and makes it the current label (any label already open is replaced, but undo_last brings it back). Give the ZPL text, or the path of a .zpl file in an allowed folder. Supported fields become normal elements; anything else is kept as raw ZPL so it still prints, and is listed in warnings. ZPL does not carry the printer resolution, so pass dpi if it is not 203.")]
    public ImportResult ImportZpl(
        [Description("The ZPL text. Use this or path.")] string? zpl = null,
        [Description("A .zpl file in an allowed folder. Use this or zpl.")] string? path = null,
        [Description("Printer resolution the ZPL was written for: 203 (default), 300 or 600.")] int dpi = 203)
    {
        if ((zpl is null) == (path is null))
            throw new McpException("Give exactly one of zpl (the text) or path (a .zpl file).");
        var options = new ZplParseOptions { Dpi = Units.ParseDpi(dpi) };
        ZplParseResult parsed;
        string name = "Imported";
        if (zpl is not null)
        {
            if (zpl.Length > _ctx.Settings.MaxFileBytes)
                throw new McpException($"That ZPL is {zpl.Length:N0} characters; the limit is {_ctx.Settings.MaxFileBytes:N0}.");
            parsed = ZplParser.Parse(zpl, options);
        }
        else
        {
            string full = _ctx.Guard.Resolve(path, ZplExt, mustExist: true);
            name = Path.GetFileNameWithoutExtension(full);
            parsed = ZplParser.Parse(SafeFiles.ReadBytes(full), options);
        }
        var doc = parsed.Document;
        _ctx.Session.Replace(doc, name, null, null, "import_zpl", dirty: true);
        _ctx.Gate.Forget();
        var warnings = parsed.Warnings
            .Take(200)
            .Select(w => (w.Line > 0 ? $"line {w.Line}: " : "") + Messages.Describe(w.ToValidationIssue()))
            .ToList();
        var s = _ctx.Session.GetCopy();
        string more = parsed.Documents.Count > 1 ? $" The ZPL holds {parsed.Documents.Count} labels; only the first is open." : "";
        return new ImportResult(
            $"Imported {doc.Elements.Count} elements with {parsed.Warnings.Count} warning(s).{more}",
            LabelView.Describe(_ctx, s.Document, s.Name, s.SourcePath, s.Dirty), LabelView.Elements(s.Document), warnings, parsed.Documents.Count);
    }

    /// <summary>Converts the label to another resolution.</summary>
    [McpServerTool(Name = "convert_dpi", Destructive = false, UseStructuredContent = true)]
    [Description("Rescales the current label to another printer resolution (203, 300 or 600 DPI) so the printed size and layout stay the same. Barcode module widths are rounded to whole dots; notes list anything that changed noticeably. undo_last reverts it.")]
    public ConvertDpiResult ConvertDpi([Description("Target resolution: 203, 300 or 600.")] int targetDpi)
    {
        var target = Units.ParseDpi(targetDpi);
        var s = _ctx.Session.GetCopy();
        if (!LabelDpiConverter.NeedsConversion(s.Document, target))
            return new ConvertDpiResult($"The label is already at {targetDpi} DPI.", LabelView.Describe(_ctx, s.Document, s.Name, s.SourcePath, s.Dirty), Array.Empty<string>());
        var converted = LabelDpiConverter.Convert(s.Document, target);
        _ctx.Session.Replace(converted.Document, s.Name, s.SourcePath, s.Merge, "convert_dpi", dirty: true);
        _ctx.Gate.Forget();
        var s2 = _ctx.Session.GetCopy();
        return new ConvertDpiResult($"Converted from {(int)s.Document.Dpi} to {targetDpi} DPI.",
            LabelView.Describe(_ctx, s2.Document, s2.Name, s2.SourcePath, s2.Dirty),
            converted.Issues.Select(Messages.Describe).ToList());
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static double CheckFinite(double v, string what) =>
        double.IsFinite(v) && v > 0 && v <= Units.MaxAbsolute ? v : throw new McpException($"The {what} must be a positive number.");

    private static Measure Restate(Measure m, string unit, LabelDocument doc) =>
        Units.ToMeasure(Units.Express(m, unit, doc.Dpi), unit);

    private static Measure MarginOf(double value, string inputUnit, string labelUnit, LabelDocument doc)
    {
        if (value < 0 || !double.IsFinite(value)) throw new McpException("Margins cannot be negative.");
        int dots = Units.ToDots(value, inputUnit, doc.Dpi);
        return Units.ToMeasure(Units.FromDots(dots, labelUnit, doc.Dpi), labelUnit);
    }

    private static void CheckLabelSize(LabelDocument doc)
    {
        if (doc.WidthDots is < 1 or > 9999)
            throw new McpException($"The width becomes {doc.WidthDots} dots; labels can be 1 to 9999 dots wide ({9999 * 25.4 / (int)doc.Dpi:0} mm at {(int)doc.Dpi} DPI).");
        if (doc.HeightDots is < 1 or > 32000)
            throw new McpException($"The height becomes {doc.HeightDots} dots; labels can be 1 to 32000 dots tall.");
    }

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = new(name.Trim().Where(c => !char.IsControl(c)).ToArray());
        return n.Length > 100 ? n[..100] : n;
    }

    private static string SuggestFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        string clean = new(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        clean = clean.Trim().TrimEnd('.');
        return (clean.Length == 0 ? "label" : clean) + ".rotula";
    }
}
