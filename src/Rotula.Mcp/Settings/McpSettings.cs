namespace Rotula.Mcp.Settings;

/// <summary>
/// What the person who installed the server allows. Everything dangerous is off by default. The values come from
/// <c>rotula-mcp.settings.json</c> next to the program and are clamped by <see cref="McpSettingsLoader"/>, so a hand-edited
/// file can lower or raise a limit only within sane bounds.
/// </summary>
public sealed record McpSettings
{
    /// <summary>Hard ceiling for <see cref="MaxElements"/>.</summary>
    public const int ElementLimitCeiling = 2000;

    /// <summary>Hard ceiling for <see cref="MaxFileBytes"/> (64 MiB).</summary>
    public const int FileBytesCeiling = 64 * 1024 * 1024;

    /// <summary>Hard ceiling for <see cref="MaxCopies"/>.</summary>
    public const int CopiesCeiling = 1000;

    /// <summary>Hard ceiling for <see cref="MaxDatabaseRows"/>.</summary>
    public const int DatabaseRowsCeiling = 10_000;

    /// <summary>Folders the server may read from and write to. Never empty after loading.</summary>
    public IReadOnlyList<string> AllowedFolders { get; init; } = Array.Empty<string>();

    /// <summary>When false (the default) <c>print_label</c> refuses every request.</summary>
    public bool AllowPrinting { get; init; }

    /// <summary>Windows printer names that may be printed to. An empty list means none.</summary>
    public IReadOnlyList<string> AllowedPrinters { get; init; } = Array.Empty<string>();

    /// <summary>When false (the default) <c>run_query</c> and the database record source are refused.</summary>
    public bool AllowDatabase { get; init; }

    /// <summary>Largest number of elements a label may hold.</summary>
    public int MaxElements { get; init; } = 200;

    /// <summary>Largest file the server reads or writes, in bytes.</summary>
    public int MaxFileBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Most copies one print request may ask for.</summary>
    public int MaxCopies { get; init; } = 10;

    /// <summary>Most rows a database answer may return to the assistant.</summary>
    public int MaxDatabaseRows { get; init; } = 1000;

    /// <summary>How long a print confirmation token stays valid, in seconds.</summary>
    public int ConfirmationSeconds { get; init; } = 120;

    /// <summary>Folder for the log files, or null for <c>%AppData%\Rotula\mcp</c>.</summary>
    public string? LogFolder { get; init; }

    /// <summary>When true the log also records tool arguments; label contents then reach the log file. Off by default.</summary>
    public bool DebugLogging { get; init; }
}

/// <summary>The outcome of loading the settings: the values plus plain-language notes for the person running the server.</summary>
/// <param name="Settings">The effective settings (safe defaults when the file is missing or damaged).</param>
/// <param name="Notes">Messages worth showing in the log: file created, file damaged, value out of range.</param>
/// <param name="Path">Where the settings file is (or would be).</param>
public sealed record McpSettingsLoadResult(McpSettings Settings, IReadOnlyList<string> Notes, string Path);
