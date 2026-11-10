using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Rotula.Files.Station;

/// <summary>
/// The file <c>station.acknowledged.json</c>, kept next to station.json by the "Set up print station" tool: per label, the fingerprint of the
/// Windows-driver (picture mode) findings the person who set up the station accepted (see <see cref="StationConfig.AcknowledgedFindings"/>).
/// <code>{ "version": 1, "labels": { "pallet.rotula": "3f2a..." } }</code>
/// It is a file of its own, not a setting in station.json, for two reasons: station.json refuses settings it does not know, so an older Rotula
/// would refuse a station.json that held it; and station.json is written by hand, so Rotula never has to rewrite (and risk) the administrator's
/// file and comments. Reading never fails: a file that is missing, unreadable, damaged or of a newer version counts as "nothing accepted",
/// which makes the station refuse those labels with a plain message, never print them unchecked.
/// </summary>
public static class StationAcknowledgements
{
    /// <summary>The file name, in the folder of the station configuration.</summary>
    public const string FileName = "station.acknowledged.json";

    /// <summary>The format this version reads and writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Hand edits (comments, a trailing comma) are tolerated, as in station.json; the file is written by Rotula, though.</summary>
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Paths and hexadecimal text, no HTML context: there is no reason to escape letters such as "é" in a folder name, which would only make the
    /// file harder to read.
    /// </summary>
    private static readonly JsonWriterOptions WriteOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The acknowledgements file that belongs to a station configuration file: the same folder.</summary>
    /// <param name="configPath">The station configuration (station.json, or any configuration file name).</param>
    public static string PathFor(string configPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? string.Empty, FileName);

    /// <summary>
    /// Reads the file. The keys are resolved against <paramref name="labelFolder"/> exactly like a label's <c>file</c> in station.json, so a key
    /// finds its label by full path (without regard to letter case). Never throws.
    /// </summary>
    /// <param name="path">The acknowledgements file.</param>
    /// <param name="labelFolder">The station's label folder.</param>
    /// <returns>Full label path to fingerprint; empty when the file is missing, cannot be read, is damaged, or is of a version this Rotula does
    /// not know. An entry whose fingerprint is not a non-empty string is left out.</returns>
    public static Dictionary<string, string> Read(string path, string labelFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string text;
        try
        {
            if (!File.Exists(path)) return result;
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return result;
        }
        try
        {
            using var doc = JsonDocument.Parse(text, ReadOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;
            // A newer format may mean something else by the same names: accepting it could let a label through that was never accepted.
            if (root.TryGetProperty("version", out var version) && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v != CurrentVersion))
                return result;
            if (!root.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Object) return result;
            foreach (var entry in labels.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) continue;
                string? fingerprint = entry.Value.GetString();
                if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(fingerprint)) continue;
                result[StationConfigLoader.Resolve(labelFolder, entry.Name)] = fingerprint.Trim();
            }
            return result;
        }
        catch (JsonException)
        {
            // Damaged: nothing counts as accepted. The set-up tool writes a whole new file the next time it saves.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Writes the file, or deletes it when <paramref name="acknowledged"/> is empty (nothing accepted means no file, so a station that prints in
    /// printer language never gets one). The new content goes to a temporary file in the same folder first, which then replaces the old file in
    /// one step, so a station starting at that moment reads either the old file or the new one, never half of it.
    /// </summary>
    /// <param name="path">The acknowledgements file.</param>
    /// <param name="labelFolder">The station's label folder: keys are written relative to it, so the acknowledgements follow the folder when it is
    /// moved. A label outside it keeps its full path.</param>
    /// <param name="acknowledged">Full label path to fingerprint.</param>
    /// <exception cref="IOException">The file could not be written or deleted (the caller says so in plain words).</exception>
    /// <exception cref="UnauthorizedAccessException">The folder may not be changed by this account.</exception>
    public static void Write(string path, string labelFolder, IReadOnlyDictionary<string, string> acknowledged)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(labelFolder);
        ArgumentNullException.ThrowIfNull(acknowledged);
        if (acknowledged.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, Serialize(labelFolder, acknowledged));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            // Only still there when the replace failed: never leave a stray file next to station.json.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The key written for a label: its path relative to the label folder, or its full path when it lies outside that folder.</summary>
    /// <param name="labelFolder">The station's label folder.</param>
    /// <param name="labelPath">The label's full path.</param>
    public static string KeyFor(string labelFolder, string labelPath)
    {
        string relative = Path.GetRelativePath(labelFolder, labelPath);
        bool outside = Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("../", StringComparison.Ordinal);
        return outside ? labelPath : relative;
    }

    /// <summary>The file content: the version, then one label per line in name order, so the file reads (and compares) the same every time.</summary>
    private static byte[] Serialize(string labelFolder, IReadOnlyDictionary<string, string> acknowledged)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriteOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteStartObject("labels");
            foreach (var (key, fingerprint) in acknowledged.Select(kv => (KeyFor(labelFolder, kv.Key), kv.Value)).OrderBy(e => e.Item1, StringComparer.OrdinalIgnoreCase))
                writer.WriteString(key, fingerprint);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        stream.Write(Encoding.UTF8.GetBytes(Environment.NewLine));
        return stream.ToArray();
    }
}
