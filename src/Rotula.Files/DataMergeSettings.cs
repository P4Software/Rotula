using System.Text.Json.Serialization;
using Rotula.Core.Merge;
using Rotula.Data;

namespace Rotula.Files;

/// <summary>
/// The data-merge choices that are saved inside a .rotula file: how columns map to placeholders, the serial counters,
/// copies, the invalid-record policy and the job size. The data itself is never saved; <see cref="SourcePath"/> is only a
/// hint so the user can reload the same file with one click.
/// </summary>
public sealed class DataMergeSettings
{
    /// <summary>Path of the data file used last time (a hint; the file may have moved).</summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// True when the file carried a data-source path that was discarded because it is not a plain local drive path
    /// (network, device, URL or relative). Never saved.
    /// </summary>
    [JsonIgnore]
    public bool SourcePathIgnored { get; set; }

    /// <summary>Worksheet name for .xlsx sources.</summary>
    public string? SheetName { get; set; }

    /// <summary>CSV delimiter: "auto", "comma", "semicolon" or "tab".</summary>
    public string Delimiter { get; set; } = "auto";

    /// <summary>True when the first row of the file holds column names.</summary>
    public bool HasHeader { get; set; } = true;

    /// <summary>Explicit placeholder-to-column choices.</summary>
    public Dictionary<string, string> ColumnOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serial counters.</summary>
    public List<CounterSettings> Counters { get; set; } = new();

    /// <summary>Fixed copies per record, or null to use the label's own quantity.</summary>
    public int? CopiesPerRecord { get; set; }

    /// <summary>Column that holds the copies per record, or null.</summary>
    public string? CopiesColumn { get; set; }

    /// <summary>How copies are written.</summary>
    public CopiesMode CopiesMode { get; set; } = CopiesMode.PrintQuantity;

    /// <summary>What to do with a record that has errors.</summary>
    public InvalidRecordPolicy OnInvalidRecord { get; set; } = InvalidRecordPolicy.Skip;

    /// <summary>Records per print job (0 = one job).</summary>
    public int MaxRecordsPerJob { get; set; }

    /// <summary>True when the batch ZPL export writes one file per job.</summary>
    public bool OneFilePerJob { get; set; }

    /// <summary>Whether date variables are read once when the batch starts (default) or for every label. Saved from format version 7 on.</summary>
    public DateEvaluation DateEvaluation { get; set; } = DateEvaluation.AtBatchStart;

    /// <summary>
    /// The database query this label reads its records from, or null when the data comes from a file. Holds only the NAME of a
    /// saved connection and the query text (see <see cref="DatabaseQueryReference"/>): host, user, password and connection
    /// strings live in the per-user connection store and never in a .rotula file.
    /// </summary>
    public DatabaseMergeSettings? Database { get; set; }

    /// <summary>True when nothing was configured, so the file does not need the newer format version.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrEmpty(SourcePath) && string.IsNullOrEmpty(SheetName) && Delimiter == "auto" && HasHeader && ColumnOverrides.Count == 0
        && Counters.Count == 0 && CopiesPerRecord is null && string.IsNullOrEmpty(CopiesColumn) && CopiesMode == CopiesMode.PrintQuantity
        && OnInvalidRecord == InvalidRecordPolicy.Skip && MaxRecordsPerJob == 0 && !OneFilePerJob && Database is null && DateEvaluation == DateEvaluation.AtBatchStart;
}

/// <summary>The saved form of a database data source: a query reference, the parameter defaults and the row cap.</summary>
public sealed class DatabaseMergeSettings
{
    /// <summary>Longest saved parameter value, in characters.</summary>
    public const int MaxParameterValueLength = 1000;

    /// <summary>Most parameters a saved query may carry.</summary>
    public const int MaxParameters = 50;

    /// <summary>Connection name and query text.</summary>
    public DatabaseQueryReference Reference { get; set; } = new(string.Empty, string.Empty);

    /// <summary>Default values of the query's <c>@parameters</c>.</summary>
    public List<QueryParameterSetting> Parameters { get; set; } = new();

    /// <summary>The row cap used when the data is read.</summary>
    public int MaxRows { get; set; } = DatabaseLimits.DefaultMaxRows;
}

/// <summary>A saved default value for one query parameter.</summary>
/// <param name="Name">Parameter name without the @.</param>
/// <param name="Value">The value as typed.</param>
/// <param name="Type">"text", "number" or "date": how the value is sent to the database.</param>
public sealed record QueryParameterSetting(string Name, string Value, string Type);

/// <summary>The saved form of a <see cref="SerialCounter"/>.</summary>
public sealed class CounterSettings
{
    /// <summary>Counter name (the placeholder is <c>{{#Name}}</c>).</summary>
    public string Name { get; set; } = "Counter";

    /// <summary>Numeric or alphanumeric.</summary>
    public CounterKind Kind { get; set; } = CounterKind.Numeric;

    /// <summary>First value.</summary>
    public string Start { get; set; } = "1";

    /// <summary>Increment per record.</summary>
    public int Step { get; set; } = 1;

    /// <summary>Minimum digits (numeric).</summary>
    public int Padding { get; set; }

    /// <summary>Text before the counted part.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Text after the counted part.</summary>
    public string Suffix { get; set; } = string.Empty;

    /// <summary>Overflow behaviour.</summary>
    public CounterOverflow Overflow { get; set; } = CounterOverflow.Wrap;
}

/// <summary>File shape of <see cref="DataMergeSettings"/> ("merge" object of a .rotula file). Enums are stored by name.</summary>
public sealed class MergeDto
{
    /// <summary>Data file hint.</summary>
    public string? SourcePath { get; set; }
    /// <summary>Worksheet name.</summary>
    public string? SheetName { get; set; }
    /// <summary>"auto", "comma", "semicolon" or "tab".</summary>
    public string? Delimiter { get; set; }
    /// <summary>First row holds names.</summary>
    public bool? HasHeader { get; set; }
    /// <summary>Placeholder to column.</summary>
    public Dictionary<string, string>? ColumnOverrides { get; set; }
    /// <summary>Counters.</summary>
    public List<CounterDto>? Counters { get; set; }
    /// <summary>Fixed copies.</summary>
    public int? CopiesPerRecord { get; set; }
    /// <summary>Copies column.</summary>
    public string? CopiesColumn { get; set; }
    /// <summary>"PrintQuantity" or "RepeatBlock".</summary>
    public string? CopiesMode { get; set; }
    /// <summary>"Skip" or "Stop".</summary>
    public string? OnInvalidRecord { get; set; }
    /// <summary>Records per job.</summary>
    public int? MaxRecordsPerJob { get; set; }
    /// <summary>One ZPL file per job when exporting.</summary>
    public bool? OneFilePerJob { get; set; }
    /// <summary>"AtBatchStart" or "PerLabel" (format version 7); omitted for the default.</summary>
    public string? DateEvaluation { get; set; }
    /// <summary>Database data source (format version 4); only a connection NAME and the query, never credentials.</summary>
    public DatabaseDto? Database { get; set; }
}

/// <summary>File shape of the database data source. Deliberately has no member for host, user, password or connection string.</summary>
public sealed class DatabaseDto
{
    /// <summary>Name of a connection saved on the user's machine.</summary>
    public string? ConnectionName { get; set; }
    /// <summary>The SELECT text.</summary>
    public string? Query { get; set; }
    /// <summary>Parameter defaults.</summary>
    public List<ParameterDto>? Parameters { get; set; }
    /// <summary>Row cap.</summary>
    public int? MaxRows { get; set; }
}

/// <summary>File shape of a parameter default.</summary>
public sealed class ParameterDto
{
    /// <summary>Name without @.</summary>
    public string? Name { get; set; }
    /// <summary>Value as typed.</summary>
    public string? Value { get; set; }
    /// <summary>"text", "number" or "date".</summary>
    public string? Type { get; set; }
}

/// <summary>File shape of a counter.</summary>
public sealed class CounterDto
{
    /// <summary>Name.</summary>
    public string? Name { get; set; }
    /// <summary>"Numeric" or "Alphanumeric".</summary>
    public string? Kind { get; set; }
    /// <summary>Start value.</summary>
    public string? Start { get; set; }
    /// <summary>Step.</summary>
    public int? Step { get; set; }
    /// <summary>Padding.</summary>
    public int? Padding { get; set; }
    /// <summary>Prefix.</summary>
    public string? Prefix { get; set; }
    /// <summary>Suffix.</summary>
    public string? Suffix { get; set; }
    /// <summary>"Wrap" or "Error".</summary>
    public string? Overflow { get; set; }
}

/// <summary>Conversion between <see cref="DataMergeSettings"/> and the file DTO. Unknown or missing values fall back to defaults.</summary>
internal static class MergeSettingsMapper
{
    public static MergeDto ToDto(DataMergeSettings s) => new()
    {
        SourcePath = s.SourcePath, SheetName = s.SheetName, Delimiter = s.Delimiter, HasHeader = s.HasHeader,
        ColumnOverrides = s.ColumnOverrides.Count == 0 ? null : new Dictionary<string, string>(s.ColumnOverrides),
        Counters = s.Counters.Select(c => new CounterDto
        {
            Name = c.Name, Kind = c.Kind.ToString(), Start = c.Start, Step = c.Step, Padding = c.Padding, Prefix = c.Prefix,
            Suffix = c.Suffix, Overflow = c.Overflow.ToString(),
        }).ToList(),
        CopiesPerRecord = s.CopiesPerRecord, CopiesColumn = s.CopiesColumn, CopiesMode = s.CopiesMode.ToString(),
        OnInvalidRecord = s.OnInvalidRecord.ToString(), MaxRecordsPerJob = s.MaxRecordsPerJob, OneFilePerJob = s.OneFilePerJob,
        DateEvaluation = s.DateEvaluation == DateEvaluation.AtBatchStart ? null : s.DateEvaluation.ToString(),
        Database = s.Database is null ? null : new DatabaseDto
        {
            ConnectionName = s.Database.Reference.ConnectionName, Query = s.Database.Reference.Query, MaxRows = s.Database.MaxRows,
            Parameters = s.Database.Parameters.Select(x => new ParameterDto { Name = x.Name, Value = x.Value, Type = x.Type }).ToList(),
        },
    };

    /// <summary>
    /// Reads the database section of a file. The file is untrusted input: sizes are capped, odd values dropped, and a section
    /// without a connection name or query is ignored. Nothing here connects to anything.
    /// </summary>
    private static DatabaseMergeSettings? DatabaseFromDto(DatabaseDto? d)
    {
        if (d is null) return null;
        string name = (d.ConnectionName ?? string.Empty).Trim();
        string query = d.Query ?? string.Empty;
        if (name.Length is 0 or > 100 || name.Any(char.IsControl) || string.IsNullOrWhiteSpace(query) || query.Length > DatabaseLimits.MaxQueryLength) return null;
        var result = new DatabaseMergeSettings
        {
            Reference = new DatabaseQueryReference(name, query),
            MaxRows = Math.Clamp(d.MaxRows ?? DatabaseLimits.DefaultMaxRows, 1, DatabaseLimits.HardMaxRows),
        };
        foreach (var p in (d.Parameters ?? new List<ParameterDto>()).Take(DatabaseMergeSettings.MaxParameters))
        {
            string pn = (p.Name ?? string.Empty).Trim();
            if (pn.Length is 0 or > 128 || pn.Any(char.IsControl)) continue;
            string value = p.Value ?? string.Empty;
            if (value.Length > DatabaseMergeSettings.MaxParameterValueLength) value = value[..DatabaseMergeSettings.MaxParameterValueLength];
            string type = p.Type is "number" or "date" ? p.Type : "text";
            result.Parameters.Add(new QueryParameterSetting(pn, value, type));
        }
        return result;
    }

    public static DataMergeSettings FromDto(MergeDto d)
    {
        // The path in a file is untrusted: keep it only if it is a plain local drive path (see SafeLocalPath).
        string? safePath = SafeLocalPath.TryGetLocalPath(d.SourcePath);
        var s = new DataMergeSettings
        {
            SourcePath = safePath, SourcePathIgnored = !string.IsNullOrWhiteSpace(d.SourcePath) && safePath is null, SheetName = d.SheetName,
            Delimiter = d.Delimiter is "comma" or "semicolon" or "tab" ? d.Delimiter : "auto",
            HasHeader = d.HasHeader ?? true,
            CopiesPerRecord = d.CopiesPerRecord, CopiesColumn = d.CopiesColumn,
            CopiesMode = Enum.TryParse<CopiesMode>(d.CopiesMode, out var cm) ? cm : CopiesMode.PrintQuantity,
            OnInvalidRecord = Enum.TryParse<InvalidRecordPolicy>(d.OnInvalidRecord, out var ip) ? ip : InvalidRecordPolicy.Skip,
            MaxRecordsPerJob = Math.Max(0, d.MaxRecordsPerJob ?? 0),
            OneFilePerJob = d.OneFilePerJob ?? false,
            DateEvaluation = Enum.TryParse<DateEvaluation>(d.DateEvaluation, out var de) && Enum.IsDefined(de) ? de : DateEvaluation.AtBatchStart,
            Database = DatabaseFromDto(d.Database),
        };
        if (d.ColumnOverrides is not null)
            foreach (var kv in d.ColumnOverrides) s.ColumnOverrides[kv.Key] = kv.Value;
        foreach (var c in d.Counters ?? new List<CounterDto>())
        {
            s.Counters.Add(new CounterSettings
            {
                Name = c.Name ?? "Counter",
                Kind = Enum.TryParse<CounterKind>(c.Kind, out var k) ? k : CounterKind.Numeric,
                Start = c.Start ?? "1", Step = c.Step ?? 1, Padding = c.Padding ?? 0, Prefix = c.Prefix ?? string.Empty, Suffix = c.Suffix ?? string.Empty,
                Overflow = Enum.TryParse<CounterOverflow>(c.Overflow, out var o) ? o : CounterOverflow.Wrap,
            });
        }
        return s;
    }
}
