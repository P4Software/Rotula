using System.Text.Json;
using Rotula.Files.Policy;

namespace Rotula.App.Services;

/// <summary>User preferences stored in %AppData%\Rotula\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>"auto" (follow Windows), "en", "es", "pt-BR" or "fr-CA".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Ruler unit: "Millimeters", "Inches" or "Dots".</summary>
    public string Unit { get; set; } = "Millimeters";

    /// <summary>Show the grid.</summary>
    public bool GridVisible { get; set; } = true;

    /// <summary>Snap to the grid while dragging.</summary>
    public bool SnapToGrid { get; set; } = true;

    /// <summary>Snap to the edges and centres of other elements while dragging.</summary>
    public bool SnapToGuides { get; set; } = true;

    /// <summary>Show the ZPL code panel under the canvas. Hidden by default; the choice is remembered.</summary>
    public bool ZplPanelVisible { get; set; }

    /// <summary>Share of the right-hand column's height given to the Properties panel (the Objects list gets the rest). 0.2 to 0.8.</summary>
    public double PropertiesPanelShare { get; set; } = 0.55;

    /// <summary>The Objects list under the Properties panel is open. Closed by default: the fields show their values, so the list is rarely needed.</summary>
    public bool ObjectsPanelExpanded { get; set; }

    /// <summary>The user has dismissed the one-time notice that a text in a Windows font is printed as a picture.</summary>
    public bool RasterNoticeDismissed { get; set; }

    /// <summary>Grid spacing in millimetres.</summary>
    public double GridSpacingMm { get; set; } = 2.0;

    /// <summary>Most recently used files, newest first.</summary>
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Ids of the label stocks the user starred in the New label dialog.</summary>
    public List<string> StockFavorites { get; set; } = new();

    /// <summary>Ids of the label stocks used last, most recent first.</summary>
    public List<string> StockRecent { get; set; } = new();

    /// <summary>The printer and printer profile used last time, so the Print dialog opens ready to go.</summary>
    public PrintSettings Print { get; set; } = new();

    /// <summary>
    /// The user switched on the daily update check (Help menu). Off by default: without it Rotula makes no network request
    /// of its own. An organization policy overrides it (see <see cref="UpdateCheckIn"/>).
    /// </summary>
    public bool UpdateCheckEnabled { get; set; }

    /// <summary>When the release page was last asked (UTC); null before the first check.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// How often, in seconds, a recovery copy of the open label is written while it has unsaved changes (see docs/recovery.md).
    /// 0 switches the recovery copy off. Values between 1 and 4 count as 5 and values above 3600 as 3600.
    /// </summary>
    public int AutosaveSeconds { get; set; } = 30;
    /// <summary>The newer version whose notice the user closed, so that notice is not shown again for the same version.</summary>
    public string? UpdateDismissedVersion { get; set; }

    /// <summary>The language in use: the organization's choice when a policy sets one, otherwise the user's own.</summary>
    public string LanguageIn(RotulaPolicy policy) => policy.Language ?? Language;

    /// <summary>Whether the update check runs: a policy that switches it on or off wins over the user's choice.</summary>
    public bool UpdateCheckIn(RotulaPolicy policy) => policy.UpdateCheck ?? UpdateCheckEnabled;
}

/// <summary>The last printer and printer profile, stored in settings.json. Enum values are stored by name.</summary>
public sealed class PrintSettings
{
    /// <summary>False until the user has printed or saved once; the dialog then starts from the open label's size.</summary>
    public bool Configured { get; set; }

    /// <summary>"Spooler" (an installed Windows printer) or "Tcp" (host and port).</summary>
    public string Transport { get; set; } = "Spooler";

    /// <summary>Windows printer queue name.</summary>
    public string PrinterName { get; set; } = string.Empty;

    /// <summary>Host name or IP address of a network printer.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>TCP port of a network printer (9100 on most label printers).</summary>
    public int Port { get; set; } = 9100;

    /// <summary>Printhead resolution: "Dpi203", "Dpi300" or "Dpi600".</summary>
    public string Dpi { get; set; } = "Dpi203";

    /// <summary>Label width in millimetres (sent as ^PW).</summary>
    public double WidthMm { get; set; } = 100;

    /// <summary>Label height in millimetres (used by the test label).</summary>
    public double HeightMm { get; set; } = 50;

    /// <summary>Media tracking: "Web", "Continuous" or "Mark".</summary>
    public string MediaTracking { get; set; } = "Web";

    /// <summary>Darkness 0-30 (~SD) or null to leave the printer as it is.</summary>
    public int? Darkness { get; set; }

    /// <summary>Print speed 1-14 (^PR) or null to leave the printer as it is.</summary>
    public int? SpeedIps { get; set; }

    /// <summary>Ask a network printer for its status (~HS) before and after printing. On by default: a TCP printer answers on the same port.</summary>
    public bool StatusCheckNetwork { get; set; } = true;

    /// <summary>
    /// Ask a Windows printer for its status before and after printing. Off by default: many USB drivers cannot answer, and a queue that is
    /// not a ZPL compatible printer would print the query as text.
    /// </summary>
    public bool StatusCheckWindows { get; set; }

    /// <summary>
    /// The map as read from settings.json. Null means the file had no map (or an explicit null), which only a version before driver printing
    /// writes: this version always writes the map, even empty, because the getter below creates it before the serializer reads it.
    /// </summary>
    private Dictionary<string, string>? _printModes;

    /// <summary>
    /// How each Windows printer understands labels, by queue name: <c>"PrinterLanguage"</c> (ZPL) or <c>"WindowsDriver"</c> (a picture drawn
    /// through the driver). A map rather than one value, so choosing a mode for one printer never changes the choice made for another. A
    /// printer that is missing, or whose value is not one of the two names, has no saved mode and is asked about once. Never null when read; a
    /// settings file saved by this version always contains it (an empty map is written as <c>{}</c>, not left out), which is how
    /// <see cref="NormalizePrintModes"/> tells an older file from a new one.
    /// </summary>
    public Dictionary<string, string> PrintModes
    {
        get => _printModes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        set => _printModes = value;
    }

    /// <summary>
    /// Makes <see cref="PrintModes"/> safe to use and brings a file from an older version up to date. Call it right after loading, before
    /// anything reads the map or saves the file (the store does; the printer view-model does it again for settings that never came from a
    /// file). Idempotent.
    /// <list type="bullet">
    /// <item>The map is rebuilt to match queue names without regard to case: Windows treats them that way, but System.Text.Json loads an ordinary
    /// case-sensitive dictionary. The indexer is used, not the copying constructor, so a hand-edited file that spells one queue twice in
    /// different case cannot throw.</item>
    /// <item>Only when the map was absent (a file written before driver printing) is the configured printer recorded as
    /// <c>"PrinterLanguage"</c>: it already printed ZPL, so its user is never asked. A map that is present but empty was written by this
    /// version for a printer nobody has answered for yet, so it is never seeded; otherwise a person who printed once from the Data window on a
    /// non-ZPL printer would never be asked.</item>
    /// </list>
    /// </summary>
    public void NormalizePrintModes()
    {
        bool writtenBeforePrintModes = _printModes is null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_printModes is not null)
            foreach (var (name, mode) in _printModes) map[name] = mode;
        _printModes = map;
        if (writtenBeforePrintModes && Configured && !string.IsNullOrWhiteSpace(PrinterName))
            map[PrinterName] = nameof(Rotula.Printing.PrinterOutputMode.PrinterLanguage);
    }

    /// <summary>
    /// A separate copy of these settings, for a window that must never change the designer's own printer (the print station, the station set-up
    /// tool). The print-mode map is copied as it is, without reading it through <see cref="PrintModes"/>: reading would create an empty map on
    /// this object, and a map that is absent (an older settings file) must stay absent so that the copy goes through
    /// <see cref="NormalizePrintModes"/> exactly as the file itself would, with the same old-file-versus-new-file rule.
    /// </summary>
    public PrintSettings Copy()
    {
        var copy = (PrintSettings)MemberwiseClone();
        // Every other field is a value or an immutable string; the map is the only part the copy could share and change.
        copy._printModes = _printModes is null ? null : new Dictionary<string, string>(_printModes, _printModes.Comparer);
        return copy;
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>Reads the settings; never throws (a damaged file yields the defaults and <see cref="LoadProblem"/> is set).</summary>
    AppSettings Load();

    /// <summary>Writes the settings; never throws (returns false when the file could not be written).</summary>
    bool Save(AppSettings settings);

    /// <summary>True when the settings file was damaged and replaced by defaults.</summary>
    bool LoadProblem { get; }
}

/// <summary>JSON file store. The first run writes a commented starter file so people can find and edit the settings.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private const string Header =
        "// Rotula settings. Edit with the program closed, or change them from the menus.\n" +
        "// language: \"auto\" follows Windows, or use \"en\" / \"es\" / \"pt-BR\" / \"fr-CA\".\n" +
        "// unit: \"Millimeters\", \"Inches\" or \"Dots\".\n" +
        "// updateCheckEnabled: true asks GitHub once a day whether a newer version exists (off by default; nothing is sent about you).\n" +
        "// autosaveSeconds: how often (in seconds) a recovery copy of a label with unsaved changes is kept, so a crash or power cut does not lose your work.\n" +
        "//   30 is the default, 0 switches it off. Rotula offers the copy the next time it starts. Your label file is never changed by it.\n" +
        "// print.printModes: per Windows printer, \"PrinterLanguage\" (ZPL) or \"WindowsDriver\" (picture); a printer not listed is asked about once.\n";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    /// <summary>Creates a store; the default location is %AppData%\Rotula\settings.json.</summary>
    public JsonSettingsStore(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rotula", "settings.json");

    /// <summary>Full path of the settings file.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public bool LoadProblem { get; private set; }

    /// <inheritdoc />
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var fresh = new AppSettings();
                Save(fresh);
                return fresh;
            }
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
            // Decide here whether the file predates print modes, before any part of Rotula can save it: the first save writes the map, and
            // the difference between "older file" and "nobody answered yet" would then be lost.
            (loaded.Print ??= new PrintSettings()).NormalizePrintModes();
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LoadProblem = true;
            return new AppSettings();
        }
    }

    /// <inheritdoc />
    public bool Save(AppSettings settings)
    {
        // Why a temp file: File.WriteAllText truncates settings.json before it writes, so a crash or power loss in between would leave an
        // empty file and the user's settings (recent files, printer, language) would be gone. The new text is written beside the real
        // file first and swapped in with one operation, so the real file is always either the old version or the new one.
        // The name is unique because two Rotula windows (or the designer and the print engine) can save at the same moment.
        string temp = _path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, Header + JsonSerializer.Serialize(settings, Options), new System.Text.UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never leave a half-written copy next to the settings.
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* nothing more can be done */ }
            return false;
        }
    }
}
