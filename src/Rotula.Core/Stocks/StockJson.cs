using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rotula.Core.Validation;

namespace Rotula.Core.Stocks;

/// <summary>Hard limits applied to every catalogue file, because a stocks file is untrusted input.</summary>
public static class StockLimits
{
    /// <summary>Highest schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>Largest catalogue file that is read, in bytes.</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;
    /// <summary>Most entries read from one file.</summary>
    public const int MaxEntriesPerFile = 20000;
    /// <summary>Most files read from the user stocks folder.</summary>
    public const int MaxUserFiles = 100;
    /// <summary>Smallest plausible label dimension in millimetres.</summary>
    public const double MinSizeMm = 3;
    /// <summary>Largest plausible label width in millimetres.</summary>
    public const double MaxWidthMm = 600;
    /// <summary>Largest plausible label height in millimetres.</summary>
    public const double MaxHeightMm = 2000;
    /// <summary>Largest allowed disagreement between a millimetre and an inch figure of one size, in millimetres.</summary>
    public const double UnitToleranceMm = 0.6;
    /// <summary>Longest text value kept (names, notes).</summary>
    public const int MaxTextLength = 1000;
    /// <summary>Longest id.</summary>
    public const int MaxIdLength = 64;

    internal static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True for a JSON property name that looks like pricing data (price, cost, msrp, currency, discount and similar).
    /// Such fields are never stored by Rotula.
    /// </summary>
    public static bool IsPriceLikeName(string name)
    {
        string n = name.ToLowerInvariant();
        foreach (var bad in PriceWords)
            if (n.Contains(bad, StringComparison.Ordinal)) return true;
        return n == "map";
    }

    private static readonly string[] PriceWords = { "price", "pricing", "msrp", "cost", "currency", "discount", "retail", "wholesale", "precio", "costo" };
}

/// <summary>
/// Reads and writes the catalogue JSON schema. Reading is hand-written on top of <see cref="JsonDocument"/> so that one
/// damaged entry costs only that entry, and nothing is ever deserialized into arbitrary types.
/// </summary>
internal static class StockJson
{
    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Parses a whole catalogue document. Never throws; failures become issues and an empty entry list.</summary>
    public static (StockCatalogMetadata Metadata, List<StockEntry> Entries) ParseDocument(ReadOnlyMemory<byte> utf8, string fileName, StockOrigin origin, List<ValidationIssue> issues)
    {
        var entries = new List<StockEntry>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8, ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileInvalidJson, fileName));
            return (StockCatalogMetadata.Empty, entries);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileInvalidJson, fileName));
                return (StockCatalogMetadata.Empty, entries);
            }

            var metadata = ParseMetadata(root, fileName, issues, out bool supported);
            if (!supported) return (metadata, entries);

            if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileNoEntries, fileName));
                return (metadata, entries);
            }

            int index = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list.EnumerateArray())
            {
                index++;
                if (index > StockLimits.MaxEntriesPerFile)
                {
                    issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileTooManyEntries, fileName, StockLimits.MaxEntriesPerFile.ToString(CultureInfo.InvariantCulture)));
                    break;
                }
                var entry = ParseEntry(item, index, fileName, origin, issues);
                if (entry is null) continue;
                if (!seen.Add(entry.Id))
                {
                    issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryDuplicateId, fileName, entry.Id));
                    continue;
                }
                entries.Add(entry);
            }
            return (metadata, entries);
        }
    }

    private static StockCatalogMetadata ParseMetadata(JsonElement root, string fileName, List<ValidationIssue> issues, out bool supported)
    {
        supported = false;
        if (!root.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("schemaVersion", out var ver))
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileUnsupportedSchema, fileName, string.Empty));
            return StockCatalogMetadata.Empty;
        }
        if (ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out int version) || version < 1 || version > StockLimits.CurrentSchemaVersion)
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileUnsupportedSchema, fileName, Trunc(ver.ToString(), 20)));
            return StockCatalogMetadata.Empty;
        }
        supported = true;

        string? generatedOn = meta.TryGetProperty("generatedOn", out var g) && g.ValueKind == JsonValueKind.String ? Trunc(g.GetString(), 40) : null;
        string? notes = meta.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? Trunc(n.GetString(), 4000) : null;
        var sources = new List<StockSourceInfo>();
        if (meta.TryGetProperty("sources", out var src) && src.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in src.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                string? id = s.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? Trunc(i.GetString(), 80) : null;
                string? name = s.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? Trunc(nm.GetString(), 400) : null;
                if (!string.IsNullOrWhiteSpace(id)) sources.Add(new StockSourceInfo(id!, name ?? id!));
                if (sources.Count >= 50) break;
            }
        }
        return new StockCatalogMetadata(version, generatedOn, notes, sources);
    }

    /// <summary>Parses one entry. Returns null (with issues) when it must be skipped.</summary>
    public static StockEntry? ParseEntry(JsonElement el, int position, string fileName, StockOrigin origin, List<ValidationIssue> issues)
    {
        string pos = position.ToString(CultureInfo.InvariantCulture);
        if (el.ValueKind != JsonValueKind.Object)
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryNotAnObject, fileName, pos));
            return null;
        }

        string? id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryMissingId, fileName, pos));
            return null;
        }
        if (id.Length > StockLimits.MaxIdLength || !StockLimits.IdPattern.IsMatch(id))
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryBadId, fileName, pos, Trunc(id, 40)));
            return null;
        }

        var r = new Reader(el, id, issues);
        string? name = r.Text("name", 300);
        string? brand = r.Text("brand", 80);
        string? partNumber = r.Text("partNumber", 80);
        string? series = r.Text("series", 200);
        string? family = r.Text("stockFamily", 200);
        string? finish = r.Text("finish", StockLimits.MaxTextLength);
        string? shape = r.Text("shape", 40);
        string? category = r.Text("category", 60);
        string? verification = r.Text("verification", 200);
        string? notes = r.Text("notes", StockLimits.MaxTextLength);
        string? sourceUrl = r.Text("sourceUrl", 500);
        string? mediaText = r.Text("media", 40);
        string? formatText = r.Text("format", 40);

        double? widthMm = r.Number("widthMm", 0, StockLimits.MaxWidthMm * 4, positive: true);
        double? heightMm = r.Number("heightMm", 0, StockLimits.MaxHeightMm * 2, positive: true);
        double? widthIn = r.Number("widthIn", 0, StockLimits.MaxWidthMm * 4 / 25.4, positive: true);
        double? heightIn = r.Number("heightIn", 0, StockLimits.MaxHeightMm * 2 / 25.4, positive: true);
        double? vGap = r.Number("vGapMm", 0, 50);
        double? hGap = r.Number("hGapMm", 0, 50);
        double? radius = r.Number("cornerRadiusMm", 0, 50);
        double? core = r.Number("coreMm", 5, 500);
        double? rollLength = r.Number("rollLengthM", 0.1, 10000);
        int? labelsPerRoll = r.Integer("labelsPerRoll", 1, 1_000_000);
        int? rollsPerCarton = r.Integer("rollsPerCarton", 1, 100_000);
        int? columns = r.Integer("columns", 1, 50);
        int? rows = r.Integer("rows", 1, 50);
        bool? verified = r.Boolean("verified");

        // gapMm is a number, or one of the words "mark" / "gap" / "web" / "continuous" when the source only states the sensing mode.
        double? gap = null;
        var sensing = StockSensing.Unknown;
        if (el.TryGetProperty("gapMm", out var gapEl) && gapEl.ValueKind != JsonValueKind.Null)
        {
            if (gapEl.ValueKind == JsonValueKind.Number)
                gap = r.Number("gapMm", 0, 50);
            else if (gapEl.ValueKind == JsonValueKind.String)
            {
                sensing = (gapEl.GetString() ?? string.Empty).Trim().ToLowerInvariant() switch
                {
                    "mark" => StockSensing.Mark,
                    "gap" or "web" => StockSensing.Gap,
                    "continuous" => StockSensing.Continuous,
                    _ => StockSensing.Unknown,
                };
                if (sensing == StockSensing.Unknown) r.Fail("gapMm");
            }
            else r.Fail("gapMm");
        }
        if (gap is not null) sensing = StockSensing.Gap;

        StockMargins? margins = null;
        if (el.TryGetProperty("marginsMm", out var mEl) && mEl.ValueKind != JsonValueKind.Null)
        {
            var mr = new Reader(mEl, id, issues);
            if (mEl.ValueKind != JsonValueKind.Object) r.Fail("marginsMm");
            else
            {
                double? l = mr.Number("left", 0, 200), t = mr.Number("top", 0, 200), rt = mr.Number("right", 0, 200), b = mr.Number("bottom", 0, 200);
                if (mr.Failed) r.Fail("marginsMm");
                else if (l is { } l1 && t is { } t1 && rt is { } r1 && b is { } b1) margins = new StockMargins(l1, t1, r1, b1);
            }
        }

        var regions = r.TextList("regions", 20, 40);
        var sources = r.TextList("sources", 20, 80);

        // Price-like fields are never kept; tell the user once per field.
        foreach (var prop in el.EnumerateObject())
            if (StockLimits.IsPriceLikeName(prop.Name))
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryPriceFieldIgnored, id, Trunc(prop.Name, 40)));

        if (r.Failed) return null;
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryMissingName, id));
            return null;
        }
        if (sourceUrl is not null && !IsHttpsUrl(sourceUrl))
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryBadUrl, id));
            return null;
        }

        var media = ParseMedia(mediaText);
        var format = ParseFormat(formatText);

        // Fill the missing unit from the other one, then check that both agree.
        if (widthMm is null && widthIn is not null) widthMm = Math.Round(widthIn.Value * 25.4, 2);
        if (widthIn is null && widthMm is not null) widthIn = Math.Round(widthMm.Value / 25.4, 3);
        if (heightMm is null && heightIn is not null) heightMm = Math.Round(heightIn.Value * 25.4, 2);
        if (heightIn is null && heightMm is not null) heightIn = Math.Round(heightMm.Value / 25.4, 3);

        if (widthMm is not { } w || widthIn is not { } wi)
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryMissingSize, id));
            return null;
        }
        if (w < StockLimits.MinSizeMm || w > StockLimits.MaxWidthMm)
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntrySizeOutOfRange, id, "widthMm", Num(w)));
            return null;
        }
        if (Math.Abs(w - wi * 25.4) > StockLimits.UnitToleranceMm)
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryUnitMismatch, id, "width", Num(w), Num(wi)));
            return null;
        }
        if (heightMm is { } h)
        {
            if (h < StockLimits.MinSizeMm || h > StockLimits.MaxHeightMm)
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntrySizeOutOfRange, id, "heightMm", Num(h)));
                return null;
            }
            if (heightIn is not { } hi || Math.Abs(h - hi * 25.4) > StockLimits.UnitToleranceMm)
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryUnitMismatch, id, "height", Num(h), Num(heightIn ?? 0)));
                return null;
            }
        }
        else
        {
            // No height is only meaningful for a continuous roll; anything else is a half-filled entry.
            if (format is not (StockFormat.Continuous or StockFormat.Linerless))
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryMissingSize, id));
                return null;
            }
            heightIn = null;
        }

        return new StockEntry
        {
            Id = id,
            Brand = string.IsNullOrWhiteSpace(brand) ? "Generic" : brand!,
            PartNumber = Blank(partNumber),
            Name = name!,
            Series = Blank(series),
            StockFamily = Blank(family),
            WidthMm = w,
            HeightMm = heightMm,
            WidthIn = wi,
            HeightIn = heightIn,
            GapMm = gap,
            Sensing = sensing,
            VGapMm = vGap,
            HGapMm = hGap,
            Margins = margins,
            CornerRadiusMm = radius,
            Media = media,
            Format = format,
            Finish = Blank(finish),
            Shape = Blank(shape),
            Category = Blank(category),
            CoreMm = core,
            LabelsPerRoll = labelsPerRoll,
            RollsPerCarton = rollsPerCarton,
            RollLengthM = rollLength,
            Columns = columns ?? 1,
            Rows = rows ?? 1,
            Regions = regions,
            SourceUrl = sourceUrl,
            Sources = sources,
            Verified = verified ?? false,
            Verification = Blank(verification),
            Notes = Blank(notes),
            Origin = origin,
        };
    }

    internal static bool IsHttpsUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host);

    private static StockMedia ParseMedia(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "direct thermal" or "direct" or "dt" => StockMedia.DirectThermal,
        "thermal transfer" or "transfer" or "tt" => StockMedia.ThermalTransfer,
        "either" or "both" => StockMedia.Either,
        _ => StockMedia.Unknown,
    };

    private static StockFormat ParseFormat(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "roll" => StockFormat.Roll,
        "continuous" => StockFormat.Continuous,
        "linerless" => StockFormat.Linerless,
        "fanfold" => StockFormat.Fanfold,
        "sheet" => StockFormat.Sheet,
        _ => StockFormat.Unknown,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string Trunc(string? s, int max) => s is null ? string.Empty : s.Length <= max ? s : s[..max];

    /// <summary>Typed property reader that records one issue per bad field and remembers that the entry failed.</summary>
    private sealed class Reader
    {
        private readonly JsonElement _el;
        private readonly string _id;
        private readonly List<ValidationIssue> _issues;

        public Reader(JsonElement el, string id, List<ValidationIssue> issues)
        {
            _el = el;
            _id = id;
            _issues = issues;
        }

        public bool Failed { get; private set; }

        public void Fail(string field)
        {
            Failed = true;
            _issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryBadField, _id, field));
        }

        private bool TryGet(string field, out JsonElement value)
        {
            value = default;
            if (_el.ValueKind != JsonValueKind.Object) return false;
            return _el.TryGetProperty(field, out value) && value.ValueKind != JsonValueKind.Null;
        }

        public string? Text(string field, int max)
        {
            if (!TryGet(field, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) { Fail(field); return null; }
            string s = v.GetString() ?? string.Empty;
            if (s.Length > max) { Fail(field); return null; }
            return Sanitize(s).Trim();
        }

        public double? Number(string field, double min, double max, bool positive = false)
        {
            if (!TryGet(field, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || double.IsNaN(d) || double.IsInfinity(d)) { Fail(field); return null; }
            // "positive" is for sizes, where zero is as meaningless as a negative value; gaps and radii may be zero.
            if (d < min || d > max || (positive && d <= 0))
            {
                Failed = true;
                _issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryValueOutOfRange, _id, field, Num(d)));
                return null;
            }
            return d;
        }

        public int? Integer(string field, int min, int max)
        {
            if (!TryGet(field, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) { Fail(field); return null; }
            if (i < min || i > max)
            {
                Failed = true;
                _issues.Add(StockMessageKeys.Warn(StockMessageKeys.EntryValueOutOfRange, _id, field, i.ToString(CultureInfo.InvariantCulture)));
                return null;
            }
            return i;
        }

        public bool? Boolean(string field)
        {
            if (!TryGet(field, out var v)) return null;
            if (v.ValueKind is JsonValueKind.True) return true;
            if (v.ValueKind is JsonValueKind.False) return false;
            Fail(field);
            return null;
        }

        public IReadOnlyList<string> TextList(string field, int maxItems, int maxLength)
        {
            if (!TryGet(field, out var v)) return Array.Empty<string>();
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > maxItems) { Fail(field); return Array.Empty<string>(); }
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) { Fail(field); return Array.Empty<string>(); }
                string s = (item.GetString() ?? string.Empty).Trim();
                if (s.Length == 0) continue;
                if (s.Length > maxLength) { Fail(field); return Array.Empty<string>(); }
                list.Add(Sanitize(s).Trim());
            }
            return list;
        }

        /// <summary>Drops control characters so a hostile file cannot smuggle line breaks into the UI or into ZPL notes.</summary>
        private static string Sanitize(string s)
        {
            bool clean = true;
            foreach (char c in s)
                if (char.IsControl(c)) { clean = false; break; }
            if (clean) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsControl(c) ? ' ' : c);
            return sb.ToString();
        }
    }

    // ---- Writing --------------------------------------------------------------------------------

    /// <summary>Serializes metadata and entries in the catalogue schema (null fields are left out), indented for readable diffs.</summary>
    public static string Write(StockCatalogMetadata metadata, IEnumerable<StockEntry> entries)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartObject("metadata");
            w.WriteNumber("schemaVersion", metadata.SchemaVersion);
            if (metadata.GeneratedOn is not null) w.WriteString("generatedOn", metadata.GeneratedOn);
            w.WriteStartArray("sources");
            foreach (var s in metadata.Sources)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Name);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (metadata.Notes is not null) w.WriteString("notes", metadata.Notes);
            w.WriteEndObject();

            w.WriteStartArray("entries");
            foreach (var e in entries) WriteEntry(w, e);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteEntry(Utf8JsonWriter w, StockEntry e)
    {
        w.WriteStartObject();
        w.WriteString("id", e.Id);
        w.WriteString("brand", e.Brand);
        Str(w, "partNumber", e.PartNumber);
        w.WriteString("name", e.Name);
        Str(w, "series", e.Series);
        Str(w, "stockFamily", e.StockFamily);
        w.WriteNumber("widthMm", e.WidthMm);
        if (e.HeightMm is { } h) w.WriteNumber("heightMm", h);
        w.WriteNumber("widthIn", e.WidthIn);
        if (e.HeightIn is { } hi) w.WriteNumber("heightIn", hi);
        if (e.GapMm is { } g) w.WriteNumber("gapMm", g);
        else if (e.Sensing == StockSensing.Mark) w.WriteString("gapMm", "mark");
        else if (e.Sensing == StockSensing.Continuous) w.WriteString("gapMm", "continuous");
        Num(w, "vGapMm", e.VGapMm);
        Num(w, "hGapMm", e.HGapMm);
        if (e.Margins is { } m)
        {
            w.WriteStartObject("marginsMm");
            w.WriteNumber("left", m.Left);
            w.WriteNumber("top", m.Top);
            w.WriteNumber("right", m.Right);
            w.WriteNumber("bottom", m.Bottom);
            w.WriteEndObject();
        }
        Num(w, "cornerRadiusMm", e.CornerRadiusMm);
        string? media = e.Media switch { StockMedia.DirectThermal => "direct thermal", StockMedia.ThermalTransfer => "thermal transfer", StockMedia.Either => "either", _ => null };
        Str(w, "media", media);
        string? format = e.Format switch { StockFormat.Roll => "roll", StockFormat.Continuous => "continuous", StockFormat.Linerless => "linerless", StockFormat.Fanfold => "fanfold", StockFormat.Sheet => "sheet", _ => null };
        Str(w, "format", format);
        Str(w, "finish", e.Finish);
        Str(w, "shape", e.Shape);
        Str(w, "category", e.Category);
        Num(w, "coreMm", e.CoreMm);
        if (e.LabelsPerRoll is { } lpr) w.WriteNumber("labelsPerRoll", lpr);
        if (e.RollsPerCarton is { } rpc) w.WriteNumber("rollsPerCarton", rpc);
        Num(w, "rollLengthM", e.RollLengthM);
        w.WriteNumber("columns", e.Columns);
        w.WriteNumber("rows", e.Rows);
        if (e.Regions.Count > 0) StrList(w, "regions", e.Regions);
        Str(w, "sourceUrl", e.SourceUrl);
        StrList(w, "sources", e.Sources);
        w.WriteBoolean("verified", e.Verified);
        Str(w, "verification", e.Verification);
        Str(w, "notes", e.Notes);
        w.WriteEndObject();
    }

    private static void Str(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null) w.WriteString(name, value);
    }

    private static void Num(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
    }

    private static void StrList(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }
}
