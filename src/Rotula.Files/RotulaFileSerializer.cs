using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Variables;
using Rotula.Core.Zpl;

namespace Rotula.Files;

/// <summary>A problem reading a .rotula file. <see cref="MessageKey"/> is a UI resource key; the UI shows it in the user's language.</summary>
public sealed class RotulaFileException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RotulaFileException(string messageKey, Exception? inner = null, params string[] args)
        : base(messageKey, inner)
    {
        MessageKey = messageKey;
        Args = args;
    }

    /// <summary>Resource key of the plain-language message.</summary>
    public string MessageKey { get; }

    /// <summary>Format arguments of the message.</summary>
    public string[] Args { get; }
}

/// <summary>A loaded .rotula file: the label plus the saved data-merge settings (null when the file has none).</summary>
/// <param name="Document">The label.</param>
/// <param name="Merge">Data-merge settings, or null.</param>
public sealed record RotulaFileContent(LabelDocument Document, DataMergeSettings? Merge);

/// <summary>
/// Reads and writes the versioned .rotula JSON format and converts between the Core model and the file DTOs.
/// The same conversion also backs the clipboard, so copy/paste between two running copies of Rotula works.
/// </summary>
public static class RotulaFileSerializer
{
    /// <summary>Version 1: label and elements only.</summary>
    public const int BaseFormatVersion = 1;

    /// <summary>Version 2: adds the optional "merge" object (data-merge settings, never the data).</summary>
    public const int DataMergeFormatVersion = 2;

    /// <summary>Version 3: adds the "rawZpl" and "recall" element types that an imported ZPL file can contain.</summary>
    public const int RawZplFormatVersion = 3;

    /// <summary>Version 4: adds the "database" object inside "merge" (connection NAME, query text, parameter defaults; never credentials).</summary>
    public const int DatabaseFormatVersion = 4;

    /// <summary>Version 5: adds the extra barcode symbologies (anything after PDF417) and the "extraOptions" object of a barcode element.</summary>
    public const int BarcodeBreadthFormatVersion = 5;

    /// <summary>
    /// Version 6: adds the ZPL command catalogue features: the "diagonalLine", "ellipse" and "symbol" element types, field options
    /// (field number, embedding, clock, printer serialization), text boxes and direction, and the printer settings of the label
    /// (shift, top, print mode, mirror, speeds, darkness, font aliases, stored graphics) plus the stock id.
    /// </summary>
    public const int ZplCatalogFormatVersion = 6;

    /// <summary>
    /// Version 7: adds variable definitions (date, counter, prompt and computed kinds with their options) and the label "id" that keys counters
    /// remembered between sessions. Plain text variables and labels without an id stay at the lower versions.
    /// </summary>
    public const int VariablesFormatVersion = 7;

    /// <summary>
    /// Version 8: adds text formatting: the Windows font choice of a text ("font": family, bold, italic, underline, size in points), which is printed as a
    /// picture, and the path of a font stored on the printer ("storedFontPath", printed with ^A@). Labels that use neither stay at the lower versions.
    /// </summary>
    public const int TextFormattingFormatVersion = 8;

    /// <summary>The newest format version this build can read. Files are written with the lowest version that can hold their content.</summary>
    public const int CurrentFormatVersion = TextFormattingFormatVersion;

    // Limits for untrusted values of the version 6 fields.
    private const int MaxStoredGraphics = 256;
    private const int MaxFontAliases = 64;
    private const int MaxTotalStoredGraphicBytes = 16 * 1024 * 1024; // the same memory rule the ZPL importer uses
    private const int MaxFieldTextLength = 255;
    private const int MaxSerialTextLength = 64;
    private const int MaxPathLength = 64;
    private const int MaxStockIdLength = 64;

    // Limits for the untrusted variable definitions of version 7. Anything beyond them is dropped or clipped, never trusted.
    private const int MaxVariables = 500;
    private const int MaxLabelIdLength = 64;

    // Limits for the untrusted "extraOptions" object. The known option names are far shorter than this; anything longer
    // cannot be a valid value and is dropped instead of being handed to the model.
    private const int MaxExtraOptionCount = 64;
    private const int MaxExtraOptionNameLength = 48;
    private const int MaxExtraOptionValueLength = 64;
    private const int MaxQrContentSpecLength = 20000;

    private const string ClipboardMarker = "rotulaElements";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Accept a discriminator that is not the first property so hand-edited files still load.
        AllowOutOfOrderMetadataProperties = true,
    };

    /// <summary>
    /// Serializes a document to .rotula JSON text. Why the version depends on the content: a label without data-merge
    /// settings stays at version 1, so older copies of Rotula can still open it.
    /// </summary>
    /// <param name="document">The label.</param>
    /// <param name="merge">Data-merge settings to store, or null/empty for none.</param>
    public static string Serialize(LabelDocument document, DataMergeSettings? merge = null)
    {
        var dto = ToDto(document);
        if (merge is { IsEmpty: false })
        {
            dto.Merge = MergeSettingsMapper.ToDto(merge);
            dto.FormatVersion = DataMergeFormatVersion;
            // A file is stamped with the highest version of any feature it uses, never lower: older builds then refuse it cleanly.
            if (merge.Database is not null) dto.FormatVersion = DatabaseFormatVersion;
        }
        // Older builds do not know the raw/recall element types, so only labels that contain them are marked version 3.
        if (document.Elements.Any(e => e is RawZplElement or RecallPlaceholderElement))
            dto.FormatVersion = Math.Max(dto.FormatVersion, RawZplFormatVersion);
        // Older builds cannot parse the new symbology names, so a label that uses one is stamped version 5. Extra options
        // alone would be ignored by an old build, but then it would silently lose them, so they also require version 5.
        if (document.Elements.Any(RequiresBarcodeBreadth))
            dto.FormatVersion = Math.Max(dto.FormatVersion, BarcodeBreadthFormatVersion);
        // Version 6 features are only written when used, so ToDto leaves them null; any one of them raises the stamp.
        if (UsesCatalogFeatures(dto))
            dto.FormatVersion = Math.Max(dto.FormatVersion, ZplCatalogFormatVersion);
        // Variable definitions and the label id only exist from version 7; an older build would drop them silently, so it must refuse the file instead.
        if (dto.Label.Id is not null || dto.Label.Variables.Any(v => v.Kind is not null) || dto.Merge?.DateEvaluation is not null)
            dto.FormatVersion = Math.Max(dto.FormatVersion, VariablesFormatVersion);
        // An older build would show a Windows-font text with a plain printer font and lose the stored font, so it must refuse the file instead.
        if (dto.Elements.Any(e => e is TextElementDto { Font: not null } or TextElementDto { StoredFontPath: not null }))
            dto.FormatVersion = Math.Max(dto.FormatVersion, TextFormattingFormatVersion);
        return JsonSerializer.Serialize(dto, Options);
    }

    private static bool UsesCatalogFeatures(RotulaFileDto dto)
    {
        var l = dto.Label;
        if (l.StockId is not null || l.LabelShift is not null || l.LabelTop is not null || l.PrintMode is not null || l.PrePeel is not null
            || l.MirrorImage is not null || l.PrintRate is not null || l.MediaDarkness is not null || l.FontAliases is not null || l.StoredGraphics is not null)
            return true;
        return dto.Elements.Any(e => e switch
        {
            DiagonalLineElementDto or EllipseElementDto or SymbolElementDto => true,
            FieldDataElementDto f when f.FieldNumber is not null || f.FieldPrompt is not null || f.EmbedCharacter is not null || f.Clock is not null || f.Serial is not null => true,
            TextElementDto t when t.TextBox is not null || t.Direction is not null || t.InterCharacterGap is not null => true,
            _ => false,
        });
    }

    private static bool RequiresBarcodeBreadth(LabelElement e) =>
        e is BarcodeElement b && (b.Symbology > BarcodeSymbology.Pdf417 || b.ExportExtraOptions().Count > 0);

    /// <summary>Parses .rotula JSON text. Throws <see cref="RotulaFileException"/> with a localizable key when it cannot be used.</summary>
    public static LabelDocument Deserialize(string json) => DeserializeFile(json).Document;

    /// <summary>Parses .rotula JSON text into the label and its data-merge settings (null for version 1 files).</summary>
    public static RotulaFileContent DeserializeFile(string json)
    {
        RotulaFileDto? dto;
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("formatVersion", out var version)
                || version.ValueKind != JsonValueKind.Number)
                throw new RotulaFileException("File.Error.NotRotula");
            // A huge or fractional number (1e99) is not a version; treat it like any other unreadable header.
            if (!version.TryGetInt32(out int v))
                throw new RotulaFileException("File.Error.NotRotula");
            if (v > CurrentFormatVersion)
                throw new RotulaFileException("File.Error.NewerVersion", null, v.ToString(CultureInfo.InvariantCulture));
            if (v < 1)
                throw new RotulaFileException("File.Error.NotRotula");
            dto = parsed.RootElement.Deserialize<RotulaFileDto>(Options);
        }
        catch (JsonException ex)
        {
            throw new RotulaFileException("File.Error.Corrupt", ex);
        }
        if (dto is null) throw new RotulaFileException("File.Error.Corrupt");
        try
        {
            var merge = dto.Merge is null ? null : MergeSettingsMapper.FromDto(dto.Merge);
            return new RotulaFileContent(FromDto(dto), merge);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or OverflowException or NullReferenceException)
        {
            throw new RotulaFileException("File.Error.Corrupt", ex);
        }
    }

    /// <summary>Writes a document to a file (UTF-8 without BOM). A temp file is written first so a failed save never destroys the previous version.</summary>
    public static void Save(LabelDocument document, string path, DataMergeSettings? merge = null)
    {
        string json = Serialize(document, merge);
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads a document from a file.</summary>
    public static LabelDocument Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Reads a document and its data-merge settings from a file.</summary>
    public static RotulaFileContent LoadFile(string path) => DeserializeFile(File.ReadAllText(path));

    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// A compact string describing everything about an element except its id and position. Two elements with the same
    /// fingerprint have the same size and look, which is what the bounds cache needs.
    /// </summary>
    public static string Fingerprint(LabelElement e)
    {
        var dto = ToDto(e);
        dto.Id = string.Empty;
        dto.X = 0;
        dto.Y = 0;
        return JsonSerializer.Serialize(dto, FingerprintOptions);
    }

    /// <summary>A compact string describing a variable and its definition, for caches that must notice when the definition is edited.</summary>
    public static string Fingerprint(LabelVariable v) => JsonSerializer.Serialize(VariableToDto(v), FingerprintOptions);

    // ---------------------------------------------------------------------------------- clipboard

    /// <summary>Serializes elements for the clipboard.</summary>
    public static string SerializeElements(IEnumerable<LabelElement> elements)
    {
        var payload = new ClipboardDto { RotulaElements = BaseFormatVersion, Elements = elements.Select(ToDto).ToList() };
        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Parses clipboard text produced by <see cref="SerializeElements"/>; returns an empty list when the text is something else.</summary>
    public static List<LabelElement> DeserializeElements(string? text)
    {
        var result = new List<LabelElement>();
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(ClipboardMarker, StringComparison.Ordinal)) return result;
        try
        {
            var dto = JsonSerializer.Deserialize<ClipboardDto>(text, Options);
            if (dto is null || dto.RotulaElements < 1) return result;
            foreach (var item in dto.Elements) result.Add(FromDto(item));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            result.Clear();
        }
        return result;
    }

    private sealed class ClipboardDto
    {
        public int RotulaElements { get; set; }
        public List<ElementDto> Elements { get; set; } = new();
    }

    // ---------------------------------------------------------------------------------- model -> dto

    /// <summary>Converts a document to its DTO.</summary>
    public static RotulaFileDto ToDto(LabelDocument d)
    {
        var dto = new RotulaFileDto
        {
            FormatVersion = BaseFormatVersion,
            Generator = "Rotula",
            Label = new LabelDto
            {
                Width = ToDto(d.Width),
                Height = ToDto(d.Height),
                Dpi = (int)d.Dpi,
                Orientation = d.Orientation.ToString(),
                Margins = new MarginsDto { Left = ToDto(d.Margins.Left), Top = ToDto(d.Margins.Top), Right = ToDto(d.Margins.Right), Bottom = ToDto(d.Margins.Bottom) },
                MediaType = d.MediaType.ToString(),
                MediaTracking = d.MediaTracking.ToString(),
                DefaultRotation = d.DefaultRotation.ToString(),
                DefaultFont = d.DefaultFont is null ? null : new FontDto { FontId = d.DefaultFont.FontId.ToString(), Height = d.DefaultFont.Height, Width = d.DefaultFont.Width },
                Encoding = new EncodingDto { CiCode = d.Encoding.CiCode, CodePage = d.Encoding.DotNetCodePage },
                Print = new PrintOptionsDto { Quantity = d.Print.Quantity, PauseEvery = d.Print.PauseEvery, Replicates = d.Print.Replicates, OverridePause = d.Print.OverridePause },
                Variables = d.Variables.Select(VariableToDto).ToList(),
            },
        };
        var lab = dto.Label;
        lab.Id = d.Id;
        lab.StockId = d.StockId;
        lab.LabelShift = d.LabelShift != 0 ? d.LabelShift : null;
        lab.LabelTop = d.LabelTop != 0 ? d.LabelTop : null;
        lab.PrintMode = d.PrintMode?.ToString();
        lab.PrePeel = d.PrePeel ? true : null;
        lab.MirrorImage = d.MirrorImage ? true : null;
        lab.PrintRate = d.PrintRate is { IsEmpty: false } r ? new PrintRateDto { Speed = r.Speed, SlewSpeed = r.SlewSpeed, BackfeedSpeed = r.BackfeedSpeed } : null;
        lab.MediaDarkness = d.MediaDarkness;
        lab.FontAliases = d.FontAliases.Count == 0 ? null : d.FontAliases.Select(a => new FontAliasDto { Letter = a.Letter.ToString(), Path = a.Path }).ToList();
        lab.StoredGraphics = d.StoredGraphics.Count == 0 ? null
            : d.StoredGraphics.Select(g => new StoredGraphicDto { Path = g.Path, BytesPerRow = g.BytesPerRow, Data = Convert.ToBase64String(g.Data) }).ToList();
        foreach (var e in d.Elements) dto.Elements.Add(ToDto(e));
        return dto;
    }

    // ---------------------------------------------------------------------------------- variables

    private static VariableDto VariableToDto(LabelVariable v)
    {
        var dto = new VariableDto { Name = v.Name, SampleValue = v.SampleValue };
        // A plain variable is written exactly as before, so a label that only uses those stays readable by older builds.
        if (v.Kind == VariableKind.Text) return dto;
        dto.Kind = v.Kind.ToString();
        switch (v.Kind)
        {
            case VariableKind.Date:
                var d = v.Date ?? new DateVariableOptions();
                dto.Date = new DateVariableDto
                {
                    Source = d.Source.ToString(),
                    FixedValue = d.FixedValue?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    Offsets = d.Offsets.Select(o => new DateOffsetDto { Amount = o.Amount, Unit = o.Unit.ToString() }).ToList(),
                    Format = d.Format, Culture = d.Culture, Utc = d.Utc, Rounding = d.Rounding.ToString(),
                    Holidays = d.Holidays.Select(h => h.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList(),
                };
                break;
            case VariableKind.Counter:
                var c = v.Counter ?? new CounterVariableOptions();
                dto.Counter = new CounterVariableDto
                {
                    Kind = c.Kind.ToString(), Start = c.Start, Step = c.Step, Padding = c.Padding, Prefix = c.Prefix, Suffix = c.Suffix,
                    Overflow = c.Overflow.ToString(), Remember = c.Remember,
                };
                break;
            case VariableKind.Prompt:
                var p = v.Prompt ?? new PromptVariableOptions();
                dto.Prompt = new PromptVariableDto
                {
                    Label = p.Label, InputType = p.InputType.ToString(), Default = p.Default, Required = p.Required, Pattern = p.Pattern,
                    MinLength = p.MinLength, MaxLength = p.MaxLength, Choices = p.Choices.ToList(), Uppercase = p.Uppercase, RememberLast = p.RememberLast,
                    HelpText = p.HelpText, OverrideRecord = p.OverrideRecord, DateFormat = p.DateFormat,
                };
                break;
            case VariableKind.Computed:
                var x = v.Computed ?? new ComputedVariableOptions();
                dto.Computed = new ComputedVariableDto { Expression = x.Expression };
                break;
        }
        return dto;
    }

    /// <summary>
    /// Reads a variable. Definitions are untrusted: lists are capped, text is clipped, unknown names fall back to defaults, and a kind whose options
    /// are missing or unreadable opens as a plain text variable (keeping its sample value) instead of failing the whole file.
    /// </summary>
    private static LabelVariable VariableFromDto(VariableDto v)
    {
        string name = Clip(v.Name ?? string.Empty, VariableLimits.MaxNameLength);
        var variable = new LabelVariable(name, Clip(v.SampleValue ?? string.Empty, VariableLimits.MaxValueLength));
        if (!TryParse<VariableKind>(v.Kind, out var kind) || kind == VariableKind.Text) return variable;
        switch (kind)
        {
            case VariableKind.Date when v.Date is { } d:
            {
                var o = new DateVariableOptions
                {
                    Source = TryParse<DateSource>(d.Source, out var src) ? src : DateSource.ComputerClock,
                    FixedValue = d.FixedValue is { Length: <= 32 } fx && DateTime.TryParseExact(fx, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fixedDate) ? fixedDate : null,
                    Format = Clip(string.IsNullOrEmpty(d.Format) ? "iso" : d.Format, VariableLimits.MaxFormatLength),
                    Culture = Clip(d.Culture ?? string.Empty, 40),
                    Utc = d.Utc,
                    Rounding = TryParse<DateRounding>(d.Rounding, out var rounding) ? rounding : DateRounding.None,
                };
                foreach (var off in (d.Offsets ?? new()).Take(VariableLimits.MaxOffsets))
                    if (off is not null && TryParse<DateUnit>(off.Unit, out var unit))
                        o.Offsets.Add(new DateOffset(Math.Clamp(off.Amount, -VariableLimits.MaxOffsetAmount, VariableLimits.MaxOffsetAmount), unit));
                foreach (var h in (d.Holidays ?? new()).Take(VariableLimits.MaxHolidays))
                    if (h is { Length: <= 10 } && DateOnly.TryParseExact(h, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var holiday))
                        o.Holidays.Add(holiday);
                variable.Kind = kind;
                variable.Date = o;
                break;
            }
            case VariableKind.Counter when v.Counter is { } c:
                variable.Kind = kind;
                variable.Counter = new CounterVariableOptions
                {
                    Kind = TryParse<CounterKind>(c.Kind, out var ck) ? ck : CounterKind.Numeric,
                    Start = Clip(c.Start ?? "1", 40),
                    Step = c.Step,
                    Padding = c.Padding,
                    Prefix = Clip(c.Prefix ?? string.Empty, VariableLimits.MaxAffixLength),
                    Suffix = Clip(c.Suffix ?? string.Empty, VariableLimits.MaxAffixLength),
                    Overflow = TryParse<CounterOverflow>(c.Overflow, out var ov) ? ov : CounterOverflow.Wrap,
                    Remember = c.Remember,
                };
                break;
            case VariableKind.Prompt when v.Prompt is { } p:
            {
                var o = new PromptVariableOptions
                {
                    Label = Clip(p.Label ?? string.Empty, VariableLimits.MaxPromptTextLength),
                    InputType = TryParse<PromptInputType>(p.InputType, out var it) ? it : PromptInputType.Text,
                    Default = Clip(p.Default ?? string.Empty, VariableLimits.MaxValueLength),
                    Required = p.Required,
                    Pattern = Clip(p.Pattern ?? string.Empty, VariableLimits.MaxPatternLength),
                    MinLength = Math.Clamp(p.MinLength, 0, VariableLimits.MaxValueLength),
                    MaxLength = Math.Clamp(p.MaxLength, 0, VariableLimits.MaxValueLength),
                    Uppercase = p.Uppercase,
                    RememberLast = p.RememberLast,
                    HelpText = Clip(p.HelpText ?? string.Empty, VariableLimits.MaxPromptTextLength),
                    OverrideRecord = p.OverrideRecord,
                    DateFormat = Clip(string.IsNullOrEmpty(p.DateFormat) ? "yyyy-MM-dd" : p.DateFormat, VariableLimits.MaxFormatLength),
                };
                foreach (var choice in (p.Choices ?? new()).Take(VariableLimits.MaxChoices))
                    if (choice is not null) o.Choices.Add(Clip(choice, VariableLimits.MaxPromptTextLength));
                variable.Kind = kind;
                variable.Prompt = o;
                break;
            }
            case VariableKind.Computed when v.Computed is { } x:
                variable.Kind = kind;
                variable.Computed = new ComputedVariableOptions { Expression = Clip(x.Expression ?? string.Empty, VariableLimits.MaxExpressionLength) };
                break;
        }
        return variable;
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    private static MeasureDto ToDto(Measure m) => new() { Value = m.Value, Unit = m.Unit.ToString() };

    /// <summary>Converts one element to its DTO.</summary>
    public static ElementDto ToDto(LabelElement e)
    {
        ElementDto dto = e switch
        {
            // DataFieldElement derives from TextElement, so it must be tested first.
            DataFieldElement f => new DataFieldElementDto { VariableName = f.VariableName },
            TextElement => new TextElementDto(),
            BarcodeElement b => new BarcodeElementDto
            {
                Symbology = b.Symbology.ToString(), Data = b.Data, Height = b.Height, ModuleWidth = b.ModuleWidth, WideBarRatio = b.WideBarRatio,
                HumanReadable = b.HumanReadable, HumanReadablePosition = b.HumanReadablePosition.ToString(), CheckDigit = b.CheckDigit,
                Code128Mode = b.Code128Mode.ToString(), QrErrorCorrection = b.QrErrorCorrection.ToString(), QrModel = b.QrModel,
                ModuleSize = b.ModuleSize, Columns = b.Columns, Rows = b.Rows, Pdf417SecurityLevel = b.Pdf417SecurityLevel, Pdf417Truncate = b.Pdf417Truncate,
                ExtraOptions = ExtrasToDto(b),
            },
            BoxElement x => new BoxElementDto { WidthDots = x.WidthDots, HeightDots = x.HeightDots, ThicknessDots = x.ThicknessDots, Color = x.Color.ToString(), CornerRounding = x.CornerRounding },
            DiagonalLineElement g => new DiagonalLineElementDto
            {
                WidthDots = g.WidthDots, HeightDots = g.HeightDots, ThicknessDots = g.ThicknessDots, Color = g.Color.ToString(), Lean = g.Lean.ToString(),
            },
            EllipseElement el => new EllipseElementDto { WidthDots = el.WidthDots, HeightDots = el.HeightDots, ThicknessDots = el.ThicknessDots, Color = el.Color.ToString() },
            SymbolElement sy => new SymbolElementDto { Symbol = sy.Symbol.ToString(), HeightDots = sy.HeightDots, WidthDots = sy.WidthDots },
            LineElement l => new LineElementDto { LengthDots = l.LengthDots, ThicknessDots = l.ThicknessDots, Direction = l.Direction.ToString(), Color = l.Color.ToString() },
            CircleElement c => new CircleElementDto { DiameterDots = c.DiameterDots, ThicknessDots = c.ThicknessDots, Color = c.Color.ToString() },
            ImageElement i => ImageToDto(i),
            RawZplElement r => new RawZplElementDto { Zpl = r.Zpl, IsVisual = r.IsVisual },
            RecallPlaceholderElement p => new RecallPlaceholderElementDto
            {
                Command = p.Command, Reference = p.Reference, Zpl = p.Zpl, EmitAsField = p.EmitAsField, WidthDots = p.WidthDots, HeightDots = p.HeightDots,
            },
            _ => throw new NotSupportedException($"Element type {e.GetType().Name} cannot be saved."),
        };
        if (e is TextElement t && dto is TextElementDto td)
        {
            td.Text = t.Text;
            td.FontId = t.FontId?.ToString();
            td.FontHeight = t.FontHeight;
            td.FontWidth = t.FontWidth;
            td.Font = t.Font is null ? null : new FontChoiceDto
            {
                Source = t.Font.Source.ToString(), PrinterFontId = t.Font.IsWindows ? null : t.Font.PrinterFontId.ToString(), FamilyName = t.Font.IsWindows ? t.Font.FamilyName : null,
                Bold = t.Font.Bold, Italic = t.Font.Italic, Underline = t.Font.Underline, PointSize = t.Font.PointSize,
            };
            td.StoredFontPath = t.StoredFontPath;
            td.Block = t.Block is null ? null : new TextBlockDto
            {
                WidthDots = t.Block.WidthDots, MaxLines = t.Block.MaxLines, LineSpacing = t.Block.LineSpacing,
                Alignment = t.Block.Alignment.ToString(), HangingIndent = t.Block.HangingIndent,
            };
            td.TextBox = t.TextBox is null ? null : new TextBoxDto { WidthDots = t.TextBox.WidthDots, HeightDots = t.TextBox.HeightDots };
            td.Direction = t.Direction == FieldDirection.Horizontal ? null : t.Direction.ToString();
            td.InterCharacterGap = t.InterCharacterGap != 0 ? t.InterCharacterGap : null;
        }
        if (e is FieldDataElement fe && dto is FieldDataElementDto fd)
        {
            fd.FieldNumber = fe.FieldNumber;
            fd.FieldPrompt = fe.FieldPrompt;
            fd.EmbedCharacter = fe.EmbedCharacter?.ToString();
            fd.Clock = fe.Clock is null ? null : new FieldClockDto { Primary = fe.Clock.Primary.ToString(), Secondary = fe.Clock.Secondary?.ToString(), Third = fe.Clock.Third?.ToString() };
            fd.Serial = fe.Serial is null ? null : new PrinterSerialDto
            {
                Kind = fe.Serial.Kind.ToString(), Increment = fe.Serial.Increment, LeadingZeros = fe.Serial.LeadingZeros,
                Mask = fe.Serial.Mask, IncrementText = fe.Serial.IncrementText,
            };
        }
        dto.Id = e.Id;
        dto.Name = e.Name;
        dto.X = e.X;
        dto.Y = e.Y;
        dto.OriginKind = e.OriginKind.ToString();
        dto.Rotation = e.Rotation?.ToString();
        dto.ReverseField = e.ReverseField;
        return dto;
    }

    private static Dictionary<string, JsonElement>? ExtrasToDto(BarcodeElement b)
    {
        var extras = b.ExportExtraOptions();
        if (extras.Count == 0) return null; // null is omitted from the file, so a plain barcode looks exactly as before
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in extras)
            result[name] = JsonSerializer.SerializeToElement(value);
        return result;
    }

    /// <summary>
    /// Turns the untrusted "extraOptions" object into text pairs for the model. Why filtered here: the model ignores unknown
    /// names and unreadable values, but it would still copy a huge string into the document, so size is capped first.
    /// </summary>
    private static Dictionary<string, string>? ExtrasFromDto(Dictionary<string, JsonElement>? raw)
    {
        if (raw is null) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in raw)
        {
            // Every option the model understands is the name of a BarcodeOption member, so junk names are dropped before they can use up the count.
            if (string.IsNullOrEmpty(name) || name.Length > MaxExtraOptionNameLength || char.IsDigit(name[0])
                || !Enum.TryParse<Rotula.Core.Barcodes.BarcodeOption>(name, false, out _)) continue;
            if (result.Count >= MaxExtraOptionCount) break;
            string? text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null, // objects, arrays and null are never valid option values
            };
            // The QR content builder's definition is a JSON text of several hundred characters (kind plus every field), so it gets
            // its own, larger cap; every other option is a short word or number.
            int limit = name == nameof(Rotula.Core.Barcodes.BarcodeOption.QrContentSpec) ? MaxQrContentSpecLength : MaxExtraOptionValueLength;
            if (text is null || text.Length > limit) continue;
            result[name] = text;
        }
        return result;
    }

    private static ImageElementDto ImageToDto(ImageElement i)
    {
        var dto = new ImageElementDto { WidthDots = i.WidthDots, HeightDots = i.HeightDots, Dither = i.Dither.ToString(), Threshold = i.Threshold, Invert = i.Invert };
        if (i.Source is { } s)
        {
            var bytes = new byte[s.Width * s.Height];
            for (int y = 0; y < s.Height; y++)
                for (int x = 0; x < s.Width; x++)
                    bytes[y * s.Width + x] = s.GetPixel(x, y);
            dto.SourceWidth = s.Width;
            dto.SourceHeight = s.Height;
            dto.SourcePixels = Convert.ToBase64String(bytes);
        }
        return dto;
    }

    // ---------------------------------------------------------------------------------- dto -> model

    /// <summary>Converts a DTO to a document. Throws <see cref="ArgumentException"/> on invalid enum names or values.</summary>
    public static LabelDocument FromDto(RotulaFileDto dto)
    {
        var l = dto.Label ?? throw new ArgumentException("Missing label.");
        if (!Enum.IsDefined(typeof(PrinterDpi), l.Dpi)) throw new ArgumentException("Unsupported DPI.");
        var doc = new LabelDocument
        {
            Width = FromDto(l.Width),
            Height = FromDto(l.Height),
            Dpi = (PrinterDpi)l.Dpi,
            Orientation = Parse<LabelOrientation>(l.Orientation),
            Margins = new LabelMargins { Left = FromDto(l.Margins.Left), Top = FromDto(l.Margins.Top), Right = FromDto(l.Margins.Right), Bottom = FromDto(l.Margins.Bottom) },
            MediaType = Parse<MediaType>(l.MediaType),
            MediaTracking = Parse<MediaTracking>(l.MediaTracking),
            DefaultRotation = Parse<FieldRotation>(l.DefaultRotation),
            DefaultFont = l.DefaultFont is null ? null : new FontSpec(ParseChar(l.DefaultFont.FontId), l.DefaultFont.Height, l.DefaultFont.Width),
            Encoding = l.Encoding.CodePage is { } cp ? ZplEncoding.Legacy(l.Encoding.CiCode, cp)
                : l.Encoding.CiCode == 28 ? ZplEncoding.Utf8
                : throw new ArgumentException("Unsupported encoding."),
            Print = new PrintOptions { Quantity = l.Print.Quantity, PauseEvery = l.Print.PauseEvery, Replicates = l.Print.Replicates, OverridePause = l.Print.OverridePause },
        };
        doc.Id = l.Id is { Length: > 0 and <= MaxLabelIdLength } labelId && labelId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? labelId : null;
        foreach (var v in l.Variables.Take(MaxVariables)) doc.Variables.Add(VariableFromDto(v));
        ReadCatalogSettings(l, doc);
        foreach (var e in dto.Elements) doc.Elements.Add(FromDto(e));
        return doc;
    }

    /// <summary>
    /// Reads the version 6 label settings. They are optional and untrusted, so a value that is not usable (an unknown name, a bad
    /// path, too much data) is skipped instead of failing the whole file; the label then simply opens without that setting.
    /// </summary>
    private static void ReadCatalogSettings(LabelDto l, LabelDocument doc)
    {
        doc.StockId = l.StockId is { Length: > 0 and <= MaxStockIdLength } id && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? id : null;
        doc.LabelShift = l.LabelShift ?? 0;
        doc.LabelTop = l.LabelTop ?? 0;
        doc.PrintMode = TryParse<PrintMode>(l.PrintMode, out var pm) ? pm : null;
        doc.PrePeel = l.PrePeel ?? false;
        doc.MirrorImage = l.MirrorImage ?? false;
        doc.PrintRate = l.PrintRate is null ? null : new PrintRateOptions { Speed = l.PrintRate.Speed, SlewSpeed = l.PrintRate.SlewSpeed, BackfeedSpeed = l.PrintRate.BackfeedSpeed };
        doc.MediaDarkness = l.MediaDarkness;
        if (l.FontAliases is not null)
            foreach (var a in l.FontAliases.Take(MaxFontAliases))
                if (a?.Letter is { Length: 1 } letter && char.IsAsciiLetterOrDigit(letter[0]) && a.Path is { Length: > 0 and <= MaxPathLength } path
                    && path.All(c => c >= ' ' && c <= '~'))
                    doc.FontAliases.Add(new FontAlias(letter[0], path));
        if (l.StoredGraphics is not null)
        {
            long budget = MaxTotalStoredGraphicBytes;
            foreach (var g in l.StoredGraphics.Take(MaxStoredGraphics))
            {
                if (g?.Data is null || g.Path is null || g.Path.Length > MaxPathLength || g.BytesPerRow < 1 || g.BytesPerRow > 4096) continue;
                // The decoded size is known from the text length, so an oversized graphic is refused before any memory is allocated for it.
                long decoded = (long)g.Data.Length / 4 * 3;
                if (decoded > budget) continue;
                string path = StoredGraphic.NormalizePath(g.Path);
                if (!StoredGraphic.IsValidPath(path)) continue;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(g.Data); }
                catch (FormatException) { continue; }
                if (bytes.Length == 0 || bytes.Length % g.BytesPerRow != 0 || bytes.Length > budget) continue;
                budget -= bytes.Length;
                doc.StoredGraphics.Add(new StoredGraphic(path, g.BytesPerRow, bytes));
            }
        }
    }

    private static Measure FromDto(MeasureDto m)
    {
        if (double.IsNaN(m.Value) || double.IsInfinity(m.Value)) throw new ArgumentException("Invalid length.");
        return new Measure(m.Value, Parse<LengthUnit>(m.Unit));
    }

    /// <summary>Converts one element DTO to a Core element.</summary>
    public static LabelElement FromDto(ElementDto dto)
    {
        LabelElement e;
        switch (dto)
        {
            case DataFieldElementDto f:
                e = FillText(new DataFieldElement { VariableName = f.VariableName }, f);
                break;
            case TextElementDto t:
                e = FillText(new TextElement(), t);
                break;
            case BarcodeElementDto b:
                var barcode = new BarcodeElement
                {
                    Symbology = Parse<BarcodeSymbology>(b.Symbology), Data = b.Data, Height = b.Height, ModuleWidth = b.ModuleWidth, WideBarRatio = b.WideBarRatio,
                    HumanReadable = b.HumanReadable, HumanReadablePosition = Parse<HumanReadablePosition>(b.HumanReadablePosition), CheckDigit = b.CheckDigit,
                    Code128Mode = Parse<Code128Mode>(b.Code128Mode), QrErrorCorrection = Parse<QrErrorCorrection>(b.QrErrorCorrection), QrModel = b.QrModel,
                    ModuleSize = b.ModuleSize, Columns = b.Columns, Rows = b.Rows, Pdf417SecurityLevel = b.Pdf417SecurityLevel, Pdf417Truncate = b.Pdf417Truncate,
                };
                barcode.ImportExtraOptions(ExtrasFromDto(b.ExtraOptions));
                e = barcode;
                break;
            case DiagonalLineElementDto g:
                e = new DiagonalLineElement { WidthDots = g.WidthDots, HeightDots = g.HeightDots, ThicknessDots = g.ThicknessDots, Color = Parse<GraphicColor>(g.Color), Lean = Parse<DiagonalLean>(g.Lean) };
                break;
            case EllipseElementDto el:
                e = new EllipseElement { WidthDots = el.WidthDots, HeightDots = el.HeightDots, ThicknessDots = el.ThicknessDots, Color = Parse<GraphicColor>(el.Color) };
                break;
            case SymbolElementDto sy:
                e = new SymbolElement { Symbol = Parse<GraphicSymbol>(sy.Symbol), HeightDots = sy.HeightDots, WidthDots = sy.WidthDots };
                break;
            case BoxElementDto x:
                e = new BoxElement { WidthDots = x.WidthDots, HeightDots = x.HeightDots, ThicknessDots = x.ThicknessDots, Color = Parse<GraphicColor>(x.Color), CornerRounding = x.CornerRounding };
                break;
            case LineElementDto l:
                e = new LineElement { LengthDots = l.LengthDots, ThicknessDots = l.ThicknessDots, Direction = Parse<LineDirection>(l.Direction), Color = Parse<GraphicColor>(l.Color) };
                break;
            case CircleElementDto c:
                e = new CircleElement { DiameterDots = c.DiameterDots, ThicknessDots = c.ThicknessDots, Color = Parse<GraphicColor>(c.Color) };
                break;
            case ImageElementDto i:
                e = new ImageElement
                {
                    WidthDots = i.WidthDots, HeightDots = i.HeightDots, Dither = Parse<ImageDitherMode>(i.Dither), Threshold = i.Threshold, Invert = i.Invert,
                    Source = i.SourcePixels is null ? null : new GrayscaleBitmap(i.SourceWidth, i.SourceHeight, Convert.FromBase64String(i.SourcePixels)),
                };
                break;
            case RawZplElementDto raw:
                e = new RawZplElement { Zpl = raw.Zpl, IsVisual = raw.IsVisual };
                break;
            case RecallPlaceholderElementDto rp:
                e = new RecallPlaceholderElement { Command = rp.Command, Reference = rp.Reference, Zpl = rp.Zpl, EmitAsField = rp.EmitAsField, WidthDots = rp.WidthDots, HeightDots = rp.HeightDots };
                break;
            default:
                throw new ArgumentException("Unknown element type.");
        }
        if (e is FieldDataElement fe && dto is FieldDataElementDto fd) ReadFieldData(fe, fd);
        if (!string.IsNullOrEmpty(dto.Id)) e.Id = dto.Id;
        e.Name = dto.Name;
        e.X = dto.X;
        e.Y = dto.Y;
        e.OriginKind = Parse<FieldOriginKind>(dto.OriginKind);
        e.Rotation = dto.Rotation is null ? null : Parse<FieldRotation>(dto.Rotation);
        e.ReverseField = dto.ReverseField;
        return e;
    }

    /// <summary>Reads the optional field settings; unusable values are skipped (see <see cref="ReadCatalogSettings"/>).</summary>
    private static void ReadFieldData(FieldDataElement e, FieldDataElementDto d)
    {
        e.FieldNumber = d.FieldNumber;
        e.FieldPrompt = d.FieldPrompt is { Length: > 0 and <= MaxFieldTextLength } prompt ? prompt : null;
        e.EmbedCharacter = d.EmbedCharacter is { Length: 1 } embed ? embed[0] : null;
        if (d.Clock?.Primary is { Length: 1 } primary)
            e.Clock = new FieldClockOptions
            {
                Primary = primary[0],
                Secondary = d.Clock.Secondary is { Length: 1 } second ? second[0] : null,
                Third = d.Clock.Third is { Length: 1 } third ? third[0] : null,
            };
        if (d.Serial is { } ser && TryParse<PrinterSerialKind>(ser.Kind, out var kind))
            e.Serial = new PrinterSerialOptions
            {
                Kind = kind, Increment = ser.Increment, LeadingZeros = ser.LeadingZeros,
                Mask = (ser.Mask ?? string.Empty).Length <= MaxSerialTextLength ? ser.Mask ?? string.Empty : string.Empty,
                IncrementText = (ser.IncrementText ?? string.Empty).Length <= MaxSerialTextLength ? ser.IncrementText ?? string.Empty : string.Empty,
            };
        if (e is TextElement t)
        {
            var td = (TextElementDto)d;
            t.TextBox = td.TextBox is null ? null : new TextBoxOptions { WidthDots = td.TextBox.WidthDots, HeightDots = td.TextBox.HeightDots };
            t.Direction = TryParse<FieldDirection>(td.Direction, out var dir) ? dir : FieldDirection.Horizontal;
            t.InterCharacterGap = td.InterCharacterGap ?? 0;
        }
    }

    private static bool TryParse<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        return !string.IsNullOrEmpty(value) && !char.IsDigit(value[0]) && Enum.TryParse(value, false, out result) && Enum.IsDefined(result);
    }

    private static TextElement FillText(TextElement t, TextElementDto d)
    {
        t.Text = d.Text;
        t.FontId = d.FontId is { Length: > 0 } f ? f[0] : null;
        t.FontHeight = d.FontHeight;
        t.FontWidth = d.FontWidth;
        t.Font = FontFromDto(d.Font);
        // A path that is not a printer font file name is dropped rather than trusted: it would end up in the ZPL.
        t.StoredFontPath = Rotula.Core.Text.StoredFonts.IsValidPath(d.StoredFontPath) ? d.StoredFontPath : null;
        t.Block = d.Block is null ? null : new TextBlockOptions
        {
            WidthDots = d.Block.WidthDots, MaxLines = d.Block.MaxLines, LineSpacing = d.Block.LineSpacing,
            Alignment = Parse<TextAlignment>(d.Block.Alignment), HangingIndent = d.Block.HangingIndent,
        };
        return t;
    }

    /// <summary>Reads the font choice of a file. It is untrusted, so the family is clipped and cleaned, the size is bounded and an unusable choice becomes "no Windows font".</summary>
    private static Rotula.Core.Text.FontChoice? FontFromDto(FontChoiceDto? d)
    {
        if (d is null) return null;
        var source = TryParse<Rotula.Core.Text.FontSource>(d.Source, out var s) ? s : Rotula.Core.Text.FontSource.Windows;
        if (source != Rotula.Core.Text.FontSource.Windows) return null; // only Windows fonts are stored on an element; a printer font lives in FontId
        string family = new string((d.FamilyName ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (family.Length > Rotula.Core.Text.FontChoice.MaxFamilyLength) family = family[..Rotula.Core.Text.FontChoice.MaxFamilyLength];
        double points = double.IsFinite(d.PointSize) ? Math.Clamp(d.PointSize, 1, Rotula.Core.Text.FontChoice.MaxPointSize) : Rotula.Core.Text.FontChoice.DefaultPointSize;
        var font = Rotula.Core.Text.FontChoice.ForWindows(family, points, d.Bold, d.Italic, d.Underline);
        return font.Validate() is null ? font : null;
    }

    private static char ParseChar(string s) => s.Length == 1 ? s[0] : throw new ArgumentException("Invalid font id.");

    // Enum.TryParse also accepts numbers ("7"), which could silently produce an undefined member; reject those.
    private static T Parse<T>(string value) where T : struct, Enum =>
        !string.IsNullOrEmpty(value) && !char.IsDigit(value[0]) && Enum.TryParse<T>(value, false, out var r) && Enum.IsDefined(r)
            ? r
            : throw new ArgumentException($"Unknown value '{value}' for {typeof(T).Name}.");
}
