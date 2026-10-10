using System.Text.Json.Serialization;
using Rotula.Core.Merge;

namespace Rotula.App.Files;

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

    /// <summary>True when nothing was configured, so the file does not need the newer format version.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrEmpty(SourcePath) && string.IsNullOrEmpty(SheetName) && Delimiter == "auto" && HasHeader && ColumnOverrides.Count == 0
        && Counters.Count == 0 && CopiesPerRecord is null && string.IsNullOrEmpty(CopiesColumn) && CopiesMode == CopiesMode.PrintQuantity
        && OnInvalidRecord == InvalidRecordPolicy.Skip && MaxRecordsPerJob == 0 && !OneFilePerJob;
}

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
    };

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
