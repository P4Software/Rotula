using System.Globalization;
using System.Text.Json;
using Rotula.Core.Validation;

namespace Rotula.Core.Stocks;

/// <summary>Result of <see cref="StockCatalogValidator"/>. A contribution passes when <see cref="IsValid"/> is true.</summary>
public sealed class StockValidationReport
{
    internal StockValidationReport(List<ValidationIssue> errors, List<ValidationIssue> warnings, int entryCount, int verifiedCount)
    {
        Errors = errors;
        Warnings = warnings;
        EntryCount = entryCount;
        VerifiedCount = verifiedCount;
    }

    /// <summary>Problems that must be fixed before the file can be merged (message keys with arguments).</summary>
    public IReadOnlyList<ValidationIssue> Errors { get; }
    /// <summary>Things worth a look that do not block a merge.</summary>
    public IReadOnlyList<ValidationIssue> Warnings { get; }
    /// <summary>Entries found in the file.</summary>
    public int EntryCount { get; }
    /// <summary>Entries with <c>verified: true</c>.</summary>
    public int VerifiedCount { get; }
    /// <summary>True when there are no errors.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Strict check of a catalogue file, meant for CI on community contributions (no command line: call it from a test or a
/// small tool). Where the loader skips a bad entry and carries on, the validator reports every problem as an error.
/// Rules: supported schema version and metadata; ids unique (ignoring case); sizes in range and millimetres agree with
/// inches within 0.6 mm; every entry cites a source id or an https source URL; no price-like fields anywhere; verified
/// entries state width and height in both units (or width plus roll length for a continuous roll).
/// </summary>
public static class StockCatalogValidator
{
    /// <summary>Smallest width/height accepted in a contribution, in millimetres (the loader itself allows 3).</summary>
    public const double MinPlausibleMm = 5;

    /// <summary>Validates a file on disk. A file that cannot be read gives one error.</summary>
    public static StockValidationReport ValidateFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > StockLimits.MaxFileBytes)
                return Fail(info.Exists ? StockMessageKeys.FileTooLarge : StockMessageKeys.FileUnreadable, Path.GetFileName(path));
            return Validate(File.ReadAllBytes(path), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(StockMessageKeys.FileUnreadable, Path.GetFileName(path));
        }
    }

    /// <summary>Validates catalogue JSON (UTF-8 bytes, an optional byte order mark is accepted).</summary>
    public static StockValidationReport Validate(ReadOnlyMemory<byte> utf8, string fileName = "catalogue.json")
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        if (utf8.Length > StockLimits.MaxFileBytes) return Fail(StockMessageKeys.FileTooLarge, fileName);
        var span = utf8.Span;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) utf8 = utf8[3..];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return Fail(StockMessageKeys.FileInvalidJson, fileName);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail(StockMessageKeys.FileInvalidJson, fileName);

            // Price-like property names are forbidden anywhere in the file, not just in entries.
            ScanForPriceFields(root, "", errors, 0);

            var knownSources = new HashSet<string>(StringComparer.Ordinal);
            int statedEntries = -1, statedVerified = -1;
            if (!root.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object)
            {
                errors.Add(StockMessageKeys.Error(StockMessageKeys.ValidateBadMetadata, "metadata"));
            }
            else
            {
                if (!meta.TryGetProperty("schemaVersion", out var ver) || ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out int v) || v < 1 || v > StockLimits.CurrentSchemaVersion)
                    errors.Add(StockMessageKeys.Error(StockMessageKeys.FileUnsupportedSchema, fileName, meta.TryGetProperty("schemaVersion", out var bad) ? bad.ToString() : string.Empty));
                if (!meta.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(notes.GetString()))
                    errors.Add(StockMessageKeys.Error(StockMessageKeys.ValidateBadMetadata, "notes"));
                if (!meta.TryGetProperty("sources", out var srcs) || srcs.ValueKind != JsonValueKind.Array || srcs.GetArrayLength() == 0)
                    errors.Add(StockMessageKeys.Error(StockMessageKeys.ValidateBadMetadata, "sources"));
                else
                    foreach (var s in srcs.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("id", out var sid) && sid.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sid.GetString())
                            && s.TryGetProperty("name", out var sn) && sn.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sn.GetString()))
                            knownSources.Add(sid.GetString()!);
                        else
                            errors.Add(StockMessageKeys.Error(StockMessageKeys.ValidateBadMetadata, "sources[].id/name"));
                    }
                if (meta.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Object)
                {
                    if (counts.TryGetProperty("catalogEntries", out var ce) && ce.TryGetInt32(out int ceV)) statedEntries = ceV;
                    if (counts.TryGetProperty("verified", out var cv) && cv.TryGetInt32(out int cvV)) statedVerified = cvV;
                }
            }

            if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                errors.Add(StockMessageKeys.Error(StockMessageKeys.FileNoEntries, fileName));
                return new StockValidationReport(errors, warnings, 0, 0);
            }

            int count = 0, verifiedCount = 0, position = 0;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list.EnumerateArray())
            {
                position++;
                count++;
                if (position > StockLimits.MaxEntriesPerFile)
                {
                    errors.Add(StockMessageKeys.Error(StockMessageKeys.FileTooManyEntries, fileName, StockLimits.MaxEntriesPerFile.ToString(CultureInfo.InvariantCulture)));
                    break;
                }

                // The loader's rules are the schema; whatever it would skip or ignore is an error here.
                var loaderIssues = new List<ValidationIssue>();
                var entry = StockJson.ParseEntry(item, position, fileName, StockOrigin.User, loaderIssues);
                foreach (var li in loaderIssues)
                {
                    // Price fields are reported once by the whole-file scan.
                    if (li.MessageKey == StockMessageKeys.EntryPriceFieldIgnored) continue;
                    errors.Add(new ValidationIssue(ValidationSeverity.Error, li.MessageKey, null, li.Args.ToArray()));
                }
                if (entry is null) continue;

                if (!ids.Add(entry.Id)) errors.Add(StockMessageKeys.Error(StockMessageKeys.EntryDuplicateId, fileName, entry.Id));
                if (entry.Verified) verifiedCount++;

                if (entry.WidthMm < MinPlausibleMm)
                    errors.Add(StockMessageKeys.Error(StockMessageKeys.EntrySizeOutOfRange, entry.Id, "widthMm", StockJson.Num(entry.WidthMm)));
                if (entry.HeightMm is { } hmm && hmm < MinPlausibleMm)
                    errors.Add(StockMessageKeys.Error(StockMessageKeys.EntrySizeOutOfRange, entry.Id, "heightMm", StockJson.Num(hmm)));

                bool hasUrl = entry.SourceUrl is not null && StockJson.IsHttpsUrl(entry.SourceUrl);
                if (entry.Sources.Count == 0 && !hasUrl)
                    errors.Add(StockMessageKeys.Error(StockMessageKeys.ValidateNoProvenance, entry.Id));
                foreach (string s in entry.Sources)
                    if (knownSources.Count > 0 && !knownSources.Contains(s))
                        warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ValidateUnknownSource, entry.Id, s));

                if (entry.Verified)
                {
                    // Both units must be written in the file, not derived by the loader, so a reviewer sees two statements.
                    var missing = new List<string>();
                    if (!item.TryGetProperty("widthMm", out var a) || a.ValueKind != JsonValueKind.Number) missing.Add("widthMm");
                    if (!item.TryGetProperty("widthIn", out var b) || b.ValueKind != JsonValueKind.Number) missing.Add("widthIn");
                    if (entry.HeightMm is not null)
                    {
                        if (!item.TryGetProperty("heightMm", out var c) || c.ValueKind != JsonValueKind.Number) missing.Add("heightMm");
                        if (!item.TryGetProperty("heightIn", out var d) || d.ValueKind != JsonValueKind.Number) missing.Add("heightIn");
                    }
                    else if (entry.RollLengthM is null) missing.Add("heightMm or rollLengthM");
                    if (missing.Count > 0)
                        errors.Add(StockMessageKeys.Error(StockMessageKeys.ValidateIncompleteVerified, entry.Id, string.Join(", ", missing)));
                }
            }

            if (statedEntries >= 0 && statedEntries != count)
                warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ValidateCountMismatch, "entries " + statedEntries.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture)));
            if (statedVerified >= 0 && statedVerified != verifiedCount)
                warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ValidateCountMismatch, "verified " + statedVerified.ToString(CultureInfo.InvariantCulture), verifiedCount.ToString(CultureInfo.InvariantCulture)));
            return new StockValidationReport(errors, warnings, count, verifiedCount);
        }
    }

    /// <summary>Validates a catalogue that was already loaded, by serializing it with the file schema first.</summary>
    public static StockValidationReport Validate(StockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string json = StockJson.Write(catalog.Metadata, catalog.Entries);
        return Validate(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static void ScanForPriceFields(JsonElement el, string path, List<ValidationIssue> errors, int depth)
    {
        if (depth > 16) return;
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (StockLimits.IsPriceLikeName(p.Name))
                    errors.Add(StockMessageKeys.Error(StockMessageKeys.EntryPriceFieldIgnored, path.Length == 0 ? "(file)" : path, StockJson.Trunc(p.Name, 40)));
                string childPath = path.Length == 0 ? p.Name : path;
                // For entries, report the entry id rather than the array position.
                if (p.Name == "entries") childPath = "entries";
                ScanForPriceFields(p.Value, p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? childPath : childPath, errors, depth + 1);
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                ScanForPriceFields(item, item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? path : path, errors, depth + 1);
        }
    }

    private static StockValidationReport Fail(string key, string fileName) =>
        new(new List<ValidationIssue> { StockMessageKeys.Error(key, fileName) }, new List<ValidationIssue>(), 0, 0);
}
