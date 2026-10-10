using System.Text;
using System.Text.Json;

namespace Rotula.Mcp.Settings;

/// <summary>
/// Reads <c>rotula-mcp.settings.json</c> (comments and trailing commas allowed) and writes a commented starter file on first
/// run. The path of the Documents folder and of the settings file are parameters so tests never touch the real ones.
/// </summary>
public static class McpSettingsLoader
{
    /// <summary>File name of the settings file, kept next to the program.</summary>
    public const string FileName = "rotula-mcp.settings.json";

    /// <summary>Name of the default label folder inside the user's Documents.</summary>
    public const string DefaultFolderName = "Rotula Labels";

    private const long MaxSettingsFileBytes = 256 * 1024;

    /// <summary>
    /// Loads the settings. Never throws: a missing, unreadable or damaged file yields the safe defaults and a note that
    /// says what happened and what to do. When the file is missing a commented starter is created (if the folder is writable)
    /// and the default label folder is created.
    /// </summary>
    /// <param name="settingsPath">Full path of the settings file.</param>
    /// <param name="documentsFolder">The user's Documents folder (injected so tests stay isolated).</param>
    public static McpSettingsLoadResult Load(string settingsPath, string documentsFolder)
    {
        var notes = new List<string>();
        string defaultFolder = Path.Combine(documentsFolder, DefaultFolderName);
        McpSettings settings;

        if (!File.Exists(settingsPath))
        {
            try
            {
                File.WriteAllText(settingsPath, BuildStarter(defaultFolder), new UTF8Encoding(false));
                notes.Add($"Created the settings file {settingsPath}. Everything risky (printing, databases) is switched off; open the file to change that.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Typical when the program sits in Program Files: a normal user cannot write there.
                notes.Add($"Could not create the settings file {settingsPath} ({ex.GetType().Name}). The server runs with the safe defaults. " +
                          "To change them, create that file with write access, for example by copying the program to a folder you own.");
            }
            settings = new McpSettings();
        }
        else
        {
            settings = Parse(settingsPath, notes);
        }

        var folders = settings.AllowedFolders.Count == 0 ? new[] { defaultFolder } : settings.AllowedFolders.ToArray();
        settings = settings with { AllowedFolders = folders };
        foreach (var folder in folders)
        {
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                notes.Add($"The label folder \"{folder}\" could not be created ({ex.GetType().Name}). Create it yourself or change allowedFolders in {FileName}.");
            }
        }
        return new McpSettingsLoadResult(settings, notes, settingsPath);
    }

    private static McpSettings Parse(string path, List<string> notes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxSettingsFileBytes)
            {
                notes.Add($"The settings file {path} is unexpectedly large and was ignored. The safe defaults are in use.");
                return new McpSettings();
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 8,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The top level must be an object.");
            var root = doc.RootElement;
            var d = new McpSettings();
            return new McpSettings
            {
                AllowedFolders = Strings(root, "allowedFolders", notes),
                AllowPrinting = Bool(root, "allowPrinting", d.AllowPrinting, notes),
                AllowedPrinters = Strings(root, "allowedPrinters", notes),
                AllowDatabase = Bool(root, "allowDatabase", d.AllowDatabase, notes),
                MaxElements = Int(root, "maxElements", d.MaxElements, 1, McpSettings.ElementLimitCeiling, notes),
                MaxFileBytes = Int(root, "maxFileBytes", d.MaxFileBytes, 1024, McpSettings.FileBytesCeiling, notes),
                MaxCopies = Int(root, "maxCopies", d.MaxCopies, 1, McpSettings.CopiesCeiling, notes),
                MaxDatabaseRows = Int(root, "maxDatabaseRows", d.MaxDatabaseRows, 1, McpSettings.DatabaseRowsCeiling, notes),
                ConfirmationSeconds = Int(root, "confirmationSeconds", d.ConfirmationSeconds, 10, 600, notes),
                DebugLogging = Bool(root, "debugLogging", d.DebugLogging, notes),
                LogFolder = root.TryGetProperty("logFolder", out var lf) && lf.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(lf.GetString()) ? lf.GetString() : null,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            notes.Add($"The settings file {path} could not be read ({ex.Message}). The safe defaults are in use: printing and databases are off. Fix the file or delete it to get a fresh one.");
            return new McpSettings();
        }
    }

    private static bool Bool(JsonElement root, string name, bool fallback, List<string> notes)
    {
        if (!root.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        notes.Add($"\"{name}\" must be true or false; the default ({fallback.ToString().ToLowerInvariant()}) is used.");
        return fallback;
    }

    private static int Int(JsonElement root, string name, int fallback, int min, int max, List<string> notes)
    {
        if (!root.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
        {
            long clamped = Math.Clamp(n, min, max);
            if (clamped != n) notes.Add($"\"{name}\" must be between {min} and {max}; {clamped} is used.");
            return (int)clamped;
        }
        notes.Add($"\"{name}\" must be a whole number; the default ({fallback}) is used.");
        return fallback;
    }

    private static List<string> Strings(JsonElement root, string name, List<string> notes)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var v)) return list;
        if (v.ValueKind != JsonValueKind.Array)
        {
            notes.Add($"\"{name}\" must be a list of text values; it is ignored.");
            return list;
        }
        foreach (var item in v.EnumerateArray().Take(64))
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())) list.Add(item.GetString()!.Trim());
            else notes.Add($"An entry of \"{name}\" is not text and was ignored.");
        }
        return list;
    }

    private static string BuildStarter(string defaultFolder)
    {
        // JsonSerializer escapes the backslashes of the Windows path so the starter is valid JSON.
        string folder = JsonSerializer.Serialize(defaultFolder);
        var lines = new[]
        {
            "// Settings for the Rotula MCP server. Lines starting with // are comments.",
            "// Edit this file, save it, and restart the program that launches the server.",
            "{",
            "  // Folders the server may read and write labels, ZPL, images, CSV and Excel files in.",
            "  // Nothing outside these folders is ever touched. Network paths are not allowed.",
            $"  \"allowedFolders\": [ {folder} ],",
            "",
            "  // Printing is OFF. To allow it set allowPrinting to true AND list each printer by its exact Windows name.",
            "  // Even then, every print request is first answered with a preview of the job and a one-time confirmation code.",
            "  \"allowPrinting\": false,",
            "  \"allowedPrinters\": [],",
            "",
            "  // Database access is OFF. When on, only connections saved in the Rotula app can be used, with read-only queries.",
            "  \"allowDatabase\": false,",
            "",
            "  // Limits.",
            "  \"maxElements\": 200,",
            "  \"maxFileBytes\": 5242880,",
            "  \"maxCopies\": 10,",
            "  \"maxDatabaseRows\": 1000,",
            "  \"confirmationSeconds\": 120,",
            "",
            "  // Set to true only to diagnose a problem: the log file then also records tool arguments (label contents).",
            "  \"debugLogging\": false",
            "}",
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
