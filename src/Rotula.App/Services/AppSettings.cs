using System.Text.Json;

namespace Rotula.App.Services;

/// <summary>User preferences stored in %AppData%\Rotula\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>"auto" (follow Windows), "en" or "es".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Ruler unit: "Millimeters", "Inches" or "Dots".</summary>
    public string Unit { get; set; } = "Millimeters";

    /// <summary>Show the grid.</summary>
    public bool GridVisible { get; set; } = true;

    /// <summary>Snap to the grid while dragging.</summary>
    public bool SnapToGrid { get; set; } = true;

    /// <summary>Snap to the edges and centres of other elements while dragging.</summary>
    public bool SnapToGuides { get; set; } = true;

    /// <summary>Grid spacing in millimetres.</summary>
    public double GridSpacingMm { get; set; } = 2.0;

    /// <summary>Most recently used files, newest first.</summary>
    public List<string> RecentFiles { get; set; } = new();
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
        "// language: \"auto\" follows Windows, or use \"en\" / \"es\".\n" +
        "// unit: \"Millimeters\", \"Inches\" or \"Dots\".\n";

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
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, Header + JsonSerializer.Serialize(settings, Options), new System.Text.UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
