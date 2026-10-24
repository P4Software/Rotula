using Rotula.Core.Validation;

namespace Rotula.Files.Station;

/// <summary>
/// The message keys of the print station configuration. The app shows them as <c>Issue.&lt;key&gt;</c>; the arguments are described on each
/// constant. Errors stop the station from opening; warnings are shown on the station screen and the station still opens.
/// </summary>
public static class StationKeys
{
    /// <summary>Error: the configuration file or folder does not exist. Args: the path.</summary>
    public const string NotFound = "StationConfig.NotFound";

    /// <summary>Error: the configuration file could not be read (locked, no permission). Args: the path.</summary>
    public const string ReadFailed = "StationConfig.ReadFailed";

    /// <summary>Error: the configuration file is not valid JSON or has a setting of the wrong type or an unknown name. Args: the path, the line number, the setting's JSON path.</summary>
    public const string InvalidJson = "StationConfig.InvalidJson";

    /// <summary>Error: the file was written for a newer version of Rotula. Args: the version in the file.</summary>
    public const string NewerVersion = "StationConfig.NewerVersion";

    /// <summary>Error: no label can be offered (none listed, none found in the folder, or none of the listed ones exists).</summary>
    public const string NoLabels = "StationConfig.NoLabels";

    /// <summary>Warning: a listed label file does not exist and is left out. Args: the path as written.</summary>
    public const string LabelMissing = "StationConfig.LabelMissing";

    /// <summary>Error: a listed file is not a .rotula label. Args: the path as written.</summary>
    public const string LabelNotRotula = "StationConfig.LabelNotRotula";

    /// <summary>Error: a label entry has no file. Args: the position of the entry (1-based).</summary>
    public const string LabelNoFile = "StationConfig.LabelNoFile";

    /// <summary>Error: two labels share one scan key. Args: the key.</summary>
    public const string DuplicateKey = "StationConfig.DuplicateKey";

    /// <summary>Error: a label (or the default) names a printer the file does not define. Args: the printer name.</summary>
    public const string UnknownPrinter = "StationConfig.UnknownPrinter";

    /// <summary>Error: a printer has neither a Windows printer nor a host, or both. Args: the printer name.</summary>
    public const string PrinterTarget = "StationConfig.PrinterTarget";

    /// <summary>Error: a printer's port is outside 1-65535. Args: the printer name.</summary>
    public const string PrinterPort = "StationConfig.PrinterPort";

    /// <summary>Error: a printer's resolution is not 203, 300 or 600. Args: the printer name.</summary>
    public const string PrinterDpi = "StationConfig.PrinterDpi";

    /// <summary>Error: a printer's darkness is outside 0-30 or its speed outside 1-14. Args: the printer name.</summary>
    public const string PrinterProfile = "StationConfig.PrinterProfile";

    /// <summary>Error: the station's copies settings are outside 1 to <see cref="StationConfig.CopiesLimit"/>, or the default is above the maximum.</summary>
    public const string Copies = "StationConfig.Copies";

    /// <summary>Error: the copies settings of one label are outside 1 to <see cref="StationConfig.CopiesLimit"/>, or its default is above its maximum. Args: the label name.</summary>
    public const string LabelCopies = "StationConfig.LabelCopies";

    /// <summary>Error: the exit PIN is not a hash made by Rotula (a PIN must never be stored as plain text).</summary>
    public const string PinHash = "StationConfig.PinHash";
}

/// <summary>A printer the station sends to. Exactly one of <see cref="WindowsPrinter"/> and <see cref="Host"/> is set.</summary>
/// <param name="Name">The name used in the configuration file.</param>
/// <param name="WindowsPrinter">An installed Windows printer queue (USB, shared, network queue), or null.</param>
/// <param name="Host">Host name or IP address of a network printer reached directly on <paramref name="Port"/>, or null.</param>
/// <param name="Port">TCP port of a network printer.</param>
/// <param name="Dpi">Printhead resolution (203, 300 or 600); null prints at the label's own resolution.</param>
/// <param name="Darkness">Darkness 0-30 sent before the label, or null to leave the printer as it is.</param>
/// <param name="Speed">Print speed 1-14, or null to leave the printer as it is.</param>
/// <param name="CheckStatus">Ask the printer for its status before and after every job (on by default at a station).</param>
public sealed record StationPrinter(string Name, string? WindowsPrinter, string? Host, int Port, int? Dpi, int? Darkness, int? Speed, bool CheckStatus);

/// <summary>One label offered at the station.</summary>
/// <param name="Path">Full path of the .rotula file.</param>
/// <param name="Name">Name shown on the tile (the file name when the configuration gives none).</param>
/// <param name="Key">Code that selects this label when it is scanned on the label list, or null.</param>
/// <param name="Printer">Where it prints; null means the printer last used in the designer.</param>
/// <param name="DefaultCopies">Copies shown when the label opens; null uses the label's own quantity (within the maximum).</param>
/// <param name="MaxCopies">The most copies one print may have.</param>
/// <param name="AllowDataMerge">The operator may print the records of the data the label was saved with.</param>
/// <param name="KeepAllValues">After printing, every answer stays for the next label instead of going back to its default.</param>
/// <param name="KeepFields">Answers (by prompt name) that stay after printing even when <paramref name="KeepAllValues"/> is false.</param>
public sealed record StationLabel(string Path, string Name, string? Key, StationPrinter? Printer, int? DefaultCopies, int MaxCopies, bool AllowDataMerge,
    bool KeepAllValues, IReadOnlyList<string> KeepFields);

/// <summary>A checked print station configuration, ready to open.</summary>
public sealed class StationConfig
{
    /// <summary>The configuration format this version reads and writes.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Largest number of copies any setting may allow (the Print dialog's limit).</summary>
    public const int CopiesLimit = 99_999_999;

    /// <summary>Maximum copies per print when the configuration does not say.</summary>
    public const int DefaultMaxCopies = 100;

    /// <summary>The file name the station looks for in a folder, and in the shared policy folder.</summary>
    public const string FileName = "station.json";

    /// <summary>The configuration file that was read, or null when the station runs on defaults (a folder of labels without a station.json).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>The folder the labels are offered from.</summary>
    public string LabelFolder { get; init; } = string.Empty;

    /// <summary>Title shown at the top of the station; empty uses the product's own title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The labels, in the order of the file (or by name for a folder).</summary>
    public IReadOnlyList<StationLabel> Labels { get; init; } = Array.Empty<StationLabel>();

    /// <summary>Hash of the exit PIN (<see cref="StationPin"/>), or null when the station can be closed without one.</summary>
    public string? ExitPinHash { get; init; }

    /// <summary>The scanner's terminator on the last question prints at once.</summary>
    public bool PrintOnLastScan { get; init; } = true;

    /// <summary>Scanning a label's key on the label list opens that label.</summary>
    public bool ScanToSelect { get; init; } = true;

    /// <summary>The station fills the whole screen without a title bar.</summary>
    public bool FullScreen { get; init; } = true;
}

/// <summary>Outcome of reading a station configuration: the configuration (null when an error stops it) and every problem found.</summary>
/// <param name="Config">The configuration, or null when <paramref name="Issues"/> holds an error.</param>
/// <param name="Issues">Errors and warnings with <see cref="StationKeys"/> message keys.</param>
public sealed record StationLoadResult(StationConfig? Config, IReadOnlyList<ValidationIssue> Issues)
{
    /// <summary>True when the station can open.</summary>
    public bool CanOpen => Config is not null;

    /// <summary>The warnings to show on the station screen.</summary>
    public IReadOnlyList<ValidationIssue> Warnings => Issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList();
}
