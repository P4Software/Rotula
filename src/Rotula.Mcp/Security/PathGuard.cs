using System.Text.RegularExpressions;
using ModelContextProtocol;
using Rotula.Files;
using Rotula.Mcp.Settings;

namespace Rotula.Mcp.Security;

/// <summary>
/// The single gate every file path coming from an assistant passes before the server touches the disk. A path is untrusted
/// text: it may try traversal (<c>..</c>), network shares, device names, alternate data streams or a link that leads out of an
/// allowed folder. The guard accepts a path only when, after canonicalisation, it lies inside an allowed folder, has a
/// whitelisted extension, contains no link on the way, and (for reads) is small enough.
/// </summary>
public sealed class PathGuard
{
    /// <summary>Extensions the server ever reads or writes, whatever the tool asks for.</summary>
    public static readonly IReadOnlySet<string> GlobalExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".rotula", ".zpl", ".png", ".csv", ".xlsx", ".pdf" };

    private const int MaxPathLength = 1024;

    // Windows treats these names as devices in every folder, with or without an extension ("nul.txt" is still NUL).
    private static readonly Regex DeviceName = new(
        @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[0-9\u00B9\u00B2\u00B3]|LPT[0-9\u00B9\u00B2\u00B3])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string[] _roots;
    private readonly int _maxFileBytes;

    /// <summary>Creates the guard from the settings. Allowed folders that are not plain local drive paths are dropped.</summary>
    public PathGuard(McpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _maxFileBytes = settings.MaxFileBytes;
        _roots = settings.AllowedFolders
            .Select(f => SafeLocalPath.TryGetLocalPath(f))
            .Where(f => f is not null)
            .Select(f => f!.TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The canonical allowed folders (without a trailing separator).</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>
    /// Validates <paramref name="input"/> and returns its canonical full path, or throws an <see cref="McpException"/> whose
    /// message says in plain language what is wrong and what to do.
    /// </summary>
    /// <param name="input">The path as the assistant wrote it: absolute, or relative to the first allowed folder.</param>
    /// <param name="extensions">Extensions this call accepts (each must also be in <see cref="GlobalExtensions"/>).</param>
    /// <param name="mustExist">True for reads: the file must exist and not exceed the size limit.</param>
    public string Resolve(string? input, IEnumerable<string> extensions, bool mustExist)
    {
        if (_roots.Length == 0)
            throw new McpException("No usable label folder is configured. Set allowedFolders in rotula-mcp.settings.json to a folder on a local drive.");
        if (string.IsNullOrWhiteSpace(input))
            throw new McpException("A file path is required.");
        if (input.Length > MaxPathLength)
            throw new McpException($"That path is too long (more than {MaxPathLength} characters).");
        string p = input.Trim();
        if (p.Any(char.IsControl))
            throw new McpException("The path contains control characters, which are not allowed.");
        if (p.Split('\\', '/').Any(s => s == ".."))
            throw new McpException("Paths with \"..\" are not allowed. Use a plain file name or a path inside an allowed folder.");
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal))
            throw new McpException("Network paths and device paths are not allowed. Use a folder on a local drive.");

        string full;
        if (IsRelative(p))
        {
            // A bare name such as "shipping.rotula" lives in the first allowed folder; this keeps conversations short.
            string combined = Path.Combine(_roots[0], p.Replace('/', '\\'));
            full = SafeLocalPath.TryGetLocalPath(combined)
                ?? throw new McpException("That is not a valid file name.");
        }
        else
        {
            full = SafeLocalPath.TryGetLocalPath(p)
                ?? throw new McpException("Only plain local paths such as C:\\Folder\\file.rotula are allowed (no network shares, device paths, URLs or alternate streams).");
        }

        string root = _roots
            .Where(r => full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault()
            ?? throw new McpException("That file is outside the allowed folders. Allowed: " + string.Join("; ", _roots) +
                                      ". The person who installed the server can change this in rotula-mcp.settings.json.");

        CheckSegments(full[(root.Length + 1)..]);

        string ext = Path.GetExtension(full);
        var accepted = extensions.Where(e => GlobalExtensions.Contains(e)).Select(e => e.ToLowerInvariant()).ToArray();
        if (!accepted.Contains(ext.ToLowerInvariant()))
            throw new McpException($"This operation works with {string.Join(", ", accepted)} files; \"{ext}\" is not accepted.");

        if (!SafeLocalPath.IsFixedOrRemovableDrive(full))
            throw new McpException("That drive is a network drive. Only local disks are allowed.");

        RejectLinks(root, full);

        if (mustExist)
        {
            var info = new FileInfo(full);
            if (!info.Exists)
                throw new McpException("That file does not exist. Use list_label_files to see what is in the allowed folders.");
            if (info.Length > _maxFileBytes)
                throw new McpException($"That file is {info.Length:N0} bytes; the limit is {_maxFileBytes:N0} bytes (maxFileBytes in rotula-mcp.settings.json).");
        }
        return full;
    }

    /// <summary>Returns true when the path would be accepted; never throws. Handy for tests and listings.</summary>
    public bool IsAllowed(string? input, IEnumerable<string> extensions, bool mustExist)
    {
        try { Resolve(input, extensions, mustExist); return true; }
        catch (McpException) { return false; }
    }

    private static bool IsRelative(string p) =>
        !p.Contains(':') && !p.StartsWith('\\') && !p.StartsWith('/');

    private static void CheckSegments(string relative)
    {
        foreach (string segment in relative.Split('\\', '/'))
        {
            if (segment.Length == 0) continue;
            // Windows silently drops trailing dots and spaces, so "CON." and "evil.rotula " alias other names.
            if (segment.EndsWith('.') || segment.EndsWith(' '))
                throw new McpException($"The name \"{segment}\" ends with a dot or a space, which Windows treats specially. Rename it.");
            string stem = segment;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem[..dot];
            stem = stem.TrimEnd(' ');
            if (DeviceName.IsMatch(stem))
                throw new McpException($"\"{segment}\" is a reserved Windows device name and cannot be used as a file or folder name.");
        }
    }

    /// <summary>
    /// Rejects the path when any existing component below the allowed folder is a symbolic link or junction. Why: a link
    /// inside an allowed folder could point anywhere, and the string comparison above would never notice.
    /// </summary>
    private static void RejectLinks(string root, string full)
    {
        string current = root;
        foreach (string segment in full[(root.Length + 1)..].Split('\\'))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) return; // nothing further down exists yet, so nothing can be a link
            if (info.LinkTarget is not null)
                throw new McpException("That path goes through a shortcut (symbolic link or junction), which is not allowed. Use the real folder.");
        }
    }
}
