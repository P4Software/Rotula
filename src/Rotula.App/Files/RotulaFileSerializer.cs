using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Zpl;

namespace Rotula.App.Files;

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

    /// <summary>The newest format version this build can read. Files are written with the lowest version that can hold their content.</summary>
    public const int CurrentFormatVersion = RawZplFormatVersion;

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
        }
        // Older builds do not know the raw/recall element types, so only labels that contain them are marked version 3.
        if (document.Elements.Any(e => e is RawZplElement or RecallPlaceholderElement))
            dto.FormatVersion = RawZplFormatVersion;
        return JsonSerializer.Serialize(dto, Options);
    }

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
            int v = version.GetInt32();
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
                Variables = d.Variables.Select(v => new VariableDto { Name = v.Name, SampleValue = v.SampleValue }).ToList(),
            },
        };
        foreach (var e in d.Elements) dto.Elements.Add(ToDto(e));
        return dto;
    }

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
            },
            BoxElement x => new BoxElementDto { WidthDots = x.WidthDots, HeightDots = x.HeightDots, ThicknessDots = x.ThicknessDots, Color = x.Color.ToString(), CornerRounding = x.CornerRounding },
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
            td.Block = t.Block is null ? null : new TextBlockDto
            {
                WidthDots = t.Block.WidthDots, MaxLines = t.Block.MaxLines, LineSpacing = t.Block.LineSpacing,
                Alignment = t.Block.Alignment.ToString(), HangingIndent = t.Block.HangingIndent,
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
        foreach (var v in l.Variables) doc.Variables.Add(new LabelVariable(v.Name, v.SampleValue));
        foreach (var e in dto.Elements) doc.Elements.Add(FromDto(e));
        return doc;
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
                e = new BarcodeElement
                {
                    Symbology = Parse<BarcodeSymbology>(b.Symbology), Data = b.Data, Height = b.Height, ModuleWidth = b.ModuleWidth, WideBarRatio = b.WideBarRatio,
                    HumanReadable = b.HumanReadable, HumanReadablePosition = Parse<HumanReadablePosition>(b.HumanReadablePosition), CheckDigit = b.CheckDigit,
                    Code128Mode = Parse<Code128Mode>(b.Code128Mode), QrErrorCorrection = Parse<QrErrorCorrection>(b.QrErrorCorrection), QrModel = b.QrModel,
                    ModuleSize = b.ModuleSize, Columns = b.Columns, Rows = b.Rows, Pdf417SecurityLevel = b.Pdf417SecurityLevel, Pdf417Truncate = b.Pdf417Truncate,
                };
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
        if (!string.IsNullOrEmpty(dto.Id)) e.Id = dto.Id;
        e.Name = dto.Name;
        e.X = dto.X;
        e.Y = dto.Y;
        e.OriginKind = Parse<FieldOriginKind>(dto.OriginKind);
        e.Rotation = dto.Rotation is null ? null : Parse<FieldRotation>(dto.Rotation);
        e.ReverseField = dto.ReverseField;
        return e;
    }

    private static TextElement FillText(TextElement t, TextElementDto d)
    {
        t.Text = d.Text;
        t.FontId = d.FontId is { Length: > 0 } f ? f[0] : null;
        t.FontHeight = d.FontHeight;
        t.FontWidth = d.FontWidth;
        t.Block = d.Block is null ? null : new TextBlockOptions
        {
            WidthDots = d.Block.WidthDots, MaxLines = d.Block.MaxLines, LineSpacing = d.Block.LineSpacing,
            Alignment = Parse<TextAlignment>(d.Block.Alignment), HangingIndent = d.Block.HangingIndent,
        };
        return t;
    }

    private static char ParseChar(string s) => s.Length == 1 ? s[0] : throw new ArgumentException("Invalid font id.");

    // Enum.TryParse also accepts numbers ("7"), which could silently produce an undefined member; reject those.
    private static T Parse<T>(string value) where T : struct, Enum =>
        !string.IsNullOrEmpty(value) && !char.IsDigit(value[0]) && Enum.TryParse<T>(value, false, out var r) && Enum.IsDefined(r)
            ? r
            : throw new ArgumentException($"Unknown value '{value}' for {typeof(T).Name}.");
}
