using System.Text.Json;
using System.Text.Json.Serialization;
using Rotula.Core.Validation;

namespace Rotula.Files.Station;

/// <summary>
/// Reads and checks a print station configuration (<c>station.json</c>). The format is described in docs/print-station.md. Every problem is a
/// <see cref="ValidationIssue"/> with a <see cref="StationKeys"/> key, never an exception, so the app can say in plain words what to fix.
/// </summary>
/// <remarks>
/// What the station is started with decides where the configuration comes from:
/// a <c>.json</c> file is the configuration; a folder uses its <c>station.json</c> or, without one, offers every label in the folder with default
/// settings; a <c>.rotula</c> file offers that label only, with the settings of a <c>station.json</c> next to it when there is one; nothing at all
/// uses the shared policy file <c>%ProgramData%\Rotula\station.json</c>. The configuration is written by whoever sets up the station, not
/// opened from an untrusted label, so its paths are used as written (relative ones from the configuration's folder).
/// </remarks>
public static class StationConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A misspelt setting ("maxCopy") must not be ignored silently: the station would run without the limit the administrator meant.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The shared policy file used when the station is started without a path.</summary>
    public static string PolicyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rotula", StationConfig.FileName);

    /// <summary>Reads the configuration for what the station was started with (see the remarks of the class); null or empty uses <see cref="PolicyPath"/>.</summary>
    public static StationLoadResult Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return LoadFile(PolicyPath, null);
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(StationKeys.NotFound, path);
        }
        if (Directory.Exists(full))
        {
            string json = Path.Combine(full, StationConfig.FileName);
            return File.Exists(json) ? LoadFile(json, null) : FromFolder(full);
        }
        if (!File.Exists(full)) return Fail(StationKeys.NotFound, full);
        if (IsLabelFile(full))
        {
            string json = Path.Combine(Path.GetDirectoryName(full)!, StationConfig.FileName);
            return File.Exists(json) ? LoadFile(json, full) : FromFolder(Path.GetDirectoryName(full)!, full);
        }
        return LoadFile(full, null);
    }

    /// <summary>Checks configuration text as if it were the file <paramref name="configPath"/> (relative label paths start at its folder).</summary>
    /// <param name="json">The file content.</param>
    /// <param name="configPath">Where the file is (or would be).</param>
    /// <param name="onlyLabel">When set, the station offers only this label file.</param>
    public static StationLoadResult Parse(string json, string configPath, string? onlyLabel = null)
    {
        StationFileDto? dto;
        try { dto = JsonSerializer.Deserialize<StationFileDto>(json, Options); }
        catch (JsonException ex)
        {
            // The line number and the setting's place in the file let the administrator find the mistake.
            return Fail(StationKeys.InvalidJson, configPath, ex.LineNumber is { } line ? (line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "?",
                ex.Path ?? string.Empty);
        }
        if (dto is null) return Fail(StationKeys.InvalidJson, configPath, "1", string.Empty);
        return Build(dto, configPath, onlyLabel);
    }

    private static StationLoadResult LoadFile(string configPath, string? onlyLabel)
    {
        if (!File.Exists(configPath)) return Fail(StationKeys.NotFound, configPath);
        string json;
        try { json = File.ReadAllText(configPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(StationKeys.ReadFailed, configPath);
        }
        return Parse(json, configPath, onlyLabel);
    }

    /// <summary>A folder without a configuration: every label in it, default settings, the designer's printer.</summary>
    private static StationLoadResult FromFolder(string folder, string? onlyLabel = null) =>
        Build(new StationFileDto(), Path.Combine(folder, StationConfig.FileName), onlyLabel, fileExists: false);

    private static StationLoadResult Build(StationFileDto dto, string configPath, string? onlyLabel, bool fileExists = true)
    {
        var issues = new List<ValidationIssue>();
        void Error(string key, params string[] args) => issues.Add(new ValidationIssue(ValidationSeverity.Error, key, null, args));
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

        if (dto.FormatVersion > StationConfig.CurrentFormatVersion)
            return Fail(StationKeys.NewerVersion, dto.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // ---- printers
        var printers = new Dictionary<string, StationPrinter>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, p) in dto.Printers ?? new Dictionary<string, StationPrinterDto>())
        {
            if (p is null) { Error(StationKeys.PrinterTarget, name); continue; }
            bool hasQueue = !string.IsNullOrWhiteSpace(p.WindowsPrinter), hasHost = !string.IsNullOrWhiteSpace(p.Host);
            if (hasQueue == hasHost) Error(StationKeys.PrinterTarget, name);
            if (p.Port is < 1 or > 65535) Error(StationKeys.PrinterPort, name);
            if (p.Dpi is not null and not (203 or 300 or 600)) Error(StationKeys.PrinterDpi, name);
            if (p.Darkness is < 0 or > 30 || p.Speed is < 1 or > 14) Error(StationKeys.PrinterProfile, name);
            printers[name] = new StationPrinter(name, hasQueue ? p.WindowsPrinter!.Trim() : null, hasHost ? p.Host!.Trim() : null,
                p.Port ?? 9100, p.Dpi, p.Darkness, p.Speed, p.CheckStatus ?? true);
        }
        StationPrinter? Printer(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (printers.TryGetValue(name.Trim(), out var found)) return found;
            Error(StationKeys.UnknownPrinter, name.Trim());
            return null;
        }
        var defaultPrinter = Printer(dto.DefaultPrinter);
        // With one printer and no default, that printer is the default: the common single-printer station needs no extra line.
        if (defaultPrinter is null && string.IsNullOrWhiteSpace(dto.DefaultPrinter) && printers.Count == 1) defaultPrinter = printers.Values.First();

        // ---- copies
        int maxCopies = dto.MaxCopies ?? StationConfig.DefaultMaxCopies;
        if (!CopiesOk(dto.DefaultCopies, maxCopies)) Error(StationKeys.Copies);

        // ---- PIN
        string? pin = string.IsNullOrWhiteSpace(dto.ExitPinHash) ? null : dto.ExitPinHash.Trim();
        if (pin is not null && !StationPin.IsHash(pin)) Error(StationKeys.PinHash);

        // ---- labels
        string folder = string.IsNullOrWhiteSpace(dto.LabelFolder) ? baseDir : Resolve(baseDir, dto.LabelFolder);
        var entries = new List<StationLabelDto>();
        if (dto.Labels is { Count: > 0 } listed) entries.AddRange(listed);
        else if (Directory.Exists(folder))
        {
            // Without a list, every label in the folder (not its sub-folders), by name, so the order is the same on every PC.
            foreach (var file in Directory.EnumerateFiles(folder, "*.rotula").OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                entries.Add(new StationLabelDto { File = file });
        }
        else if (dto.LabelFolder is not null) Error(StationKeys.NotFound, folder);

        var labels = new List<StationLabel>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e is null || string.IsNullOrWhiteSpace(e.File)) { Error(StationKeys.LabelNoFile, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)); continue; }
            string path = Resolve(folder, e.File);
            if (!IsLabelFile(path)) { Error(StationKeys.LabelNotRotula, e.File); continue; }
            if (onlyLabel is not null && !string.Equals(path, Path.GetFullPath(onlyLabel), StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(path)) { issues.Add(new ValidationIssue(ValidationSeverity.Warning, StationKeys.LabelMissing, null, e.File)); continue; }
            string name = string.IsNullOrWhiteSpace(e.Name) ? Path.GetFileNameWithoutExtension(path) : e.Name.Trim();
            string? key = string.IsNullOrWhiteSpace(e.Key) ? null : e.Key.Trim();
            if (key is not null && !keys.Add(key)) Error(StationKeys.DuplicateKey, key);
            int max = e.MaxCopies ?? maxCopies;
            int? def = e.DefaultCopies ?? dto.DefaultCopies;
            if ((e.MaxCopies is not null || e.DefaultCopies is not null) && !CopiesOk(def, max)) Error(StationKeys.LabelCopies, name);
            labels.Add(new StationLabel(path, name, key, string.IsNullOrWhiteSpace(e.Printer) ? defaultPrinter : Printer(e.Printer), def, max,
                e.AllowDataMerge ?? dto.AllowDataMerge ?? false, e.KeepValues ?? dto.KeepValues ?? false,
                (e.KeepFields ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList()));
        }
        // The label the station was started with is offered even when the configuration next to it does not list it.
        if (onlyLabel is not null && labels.Count == 0 && File.Exists(onlyLabel) && !issues.Any(x => x.Severity == ValidationSeverity.Error))
        {
            string full = Path.GetFullPath(onlyLabel);
            labels.Add(new StationLabel(full, Path.GetFileNameWithoutExtension(full), null, defaultPrinter, dto.DefaultCopies, maxCopies,
                dto.AllowDataMerge ?? false, dto.KeepValues ?? false, Array.Empty<string>()));
        }
        if (labels.Count == 0 && !issues.Any(x => x.Severity == ValidationSeverity.Error)) Error(StationKeys.NoLabels);

        if (issues.Any(x => x.Severity == ValidationSeverity.Error)) return new StationLoadResult(null, issues);
        var config = new StationConfig
        {
            ConfigPath = fileExists ? Path.GetFullPath(configPath) : null,
            LabelFolder = folder,
            Title = dto.Title?.Trim() ?? string.Empty,
            Labels = labels,
            ExitPinHash = pin,
            PrintOnLastScan = dto.PrintOnLastScan ?? true,
            ScanToSelect = dto.ScanToSelect ?? true,
            FullScreen = dto.FullScreen ?? true,
        };
        return new StationLoadResult(config, issues);
    }

    private static bool CopiesOk(int? defaultCopies, int max) =>
        max is >= 1 and <= StationConfig.CopiesLimit && (defaultCopies is null || (defaultCopies >= 1 && defaultCopies <= max));

    private static bool IsLabelFile(string path) => path.EndsWith(".rotula", StringComparison.OrdinalIgnoreCase);

    private static string Resolve(string baseDir, string path)
    {
        string p = path.Trim();
        try { return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.Combine(baseDir, p);
        }
    }

    private static StationLoadResult Fail(string key, params string[] args) =>
        new(null, new[] { new ValidationIssue(ValidationSeverity.Error, key, null, args) });

    // ------------------------------------------------------------------------------------ file shape

    private sealed class StationFileDto
    {
        public int FormatVersion { get; set; } = 1;
        public string? Title { get; set; }
        public string? LabelFolder { get; set; }
        public List<StationLabelDto>? Labels { get; set; }
        public Dictionary<string, StationPrinterDto>? Printers { get; set; }
        public string? DefaultPrinter { get; set; }
        public int? DefaultCopies { get; set; }
        public int? MaxCopies { get; set; }
        public bool? AllowDataMerge { get; set; }
        public bool? KeepValues { get; set; }
        public string? ExitPinHash { get; set; }
        public bool? PrintOnLastScan { get; set; }
        public bool? ScanToSelect { get; set; }
        public bool? FullScreen { get; set; }
        [JsonPropertyName("$schema")]
        public string? Schema { get; set; }
    }

    private sealed class StationLabelDto
    {
        public string? File { get; set; }
        public string? Name { get; set; }
        public string? Key { get; set; }
        public string? Printer { get; set; }
        public int? DefaultCopies { get; set; }
        public int? MaxCopies { get; set; }
        public bool? AllowDataMerge { get; set; }
        public bool? KeepValues { get; set; }
        public List<string>? KeepFields { get; set; }
    }

    private sealed class StationPrinterDto
    {
        public string? WindowsPrinter { get; set; }
        public string? Host { get; set; }
        public int? Port { get; set; }
        public int? Dpi { get; set; }
        public int? Darkness { get; set; }
        public int? Speed { get; set; }
        public bool? CheckStatus { get; set; }
    }
}
