using Rotula.Files;

namespace Rotula.Engine;

/// <summary>The outcome of finding and reading a label: the content, or a message key with its arguments.</summary>
/// <param name="Content">The loaded label, or null.</param>
/// <param name="ErrorKey">Why it could not be loaded (an <c>Engine.Error.*</c> or <c>File.Error.*</c> key), or null.</param>
/// <param name="Args">Arguments of <paramref name="ErrorKey"/>.</param>
public sealed record LabelLoad(RotulaFileContent? Content, string? ErrorKey, object[] Args)
{
    /// <summary>A loaded label.</summary>
    public static LabelLoad Ok(RotulaFileContent content) => new(content, null, []);

    /// <summary>A label that could not be loaded.</summary>
    public static LabelLoad Error(string key, params object[] args) => new(null, key, args);
}

/// <summary>Finds and reads .rotula labels for the engine, with the path rules of the REST server.</summary>
public static class LabelFiles
{
    /// <summary>The label file extension.</summary>
    public const string Extension = ".rotula";

    /// <summary>Reads a label from any path (the command line runs with the rights of the person or service that starts it).</summary>
    /// <param name="path">The file.</param>
    /// <param name="maxBytes">Largest file accepted.</param>
    public static LabelLoad Load(string path, long maxBytes = long.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return LabelLoad.Error("Engine.Error.LabelNotFound", path);
            if (info.Length > maxBytes) return LabelLoad.Error("Engine.Error.LabelTooLarge", path, maxBytes / 1024);
            return Parse(File.ReadAllText(info.FullName), Path.GetFileName(info.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return LabelLoad.Error("File.Error.Read", Path.GetFileName(path));
        }
    }

    /// <summary>Reads a label sent as text (the content of a .rotula file).</summary>
    /// <param name="content">The JSON of the file.</param>
    /// <param name="name">How messages name it (the file name, or "labelContent").</param>
    public static LabelLoad Parse(string content, string name = "labelContent")
    {
        try { return LabelLoad.Ok(RotulaFileSerializer.DeserializeFile(content)); }
        // The File.Error texts name the file first, then their own arguments (the format version of a newer file).
        catch (RotulaFileException ex) { return LabelLoad.Error(ex.MessageKey, [name, .. ex.Args]); }
    }

    /// <summary>
    /// Finds a label named relative to one of <paramref name="folders"/>, for the REST server. Only a plain relative path to a .rotula file is
    /// accepted: no drive, no UNC or device path, no "..", no stream name; and the resolved file must lie inside the folder. Folders are tried in
    /// order and the first that holds the file wins. Returns the full path, or null with the key of the problem in <paramref name="errorKey"/>.
    /// </summary>
    public static string? Resolve(string? requested, IReadOnlyList<string> folders, out string? errorKey)
    {
        errorKey = "Engine.Error.LabelOutsideFolders";
        if (string.IsNullOrWhiteSpace(requested) || requested.Length > 512) return null;
        string rel = requested.Trim().Replace('\\', '/');
        if (rel.StartsWith('/') || rel.Contains(':') || rel.IndexOfAny(['?', '*', '<', '>', '|', '"', '\0']) >= 0) return null;
        var parts = rel.Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == "..")) return null;
        if (!rel.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var folder in folders)
        {
            string root;
            try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            string full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            if (File.Exists(full))
            {
                errorKey = null;
                return full;
            }
        }
        errorKey = "Engine.Error.LabelNotFound";
        return null;
    }
}
