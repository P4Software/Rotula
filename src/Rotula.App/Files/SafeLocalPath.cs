namespace Rotula.App.Files;

/// <summary>
/// Decides whether a path that was stored in a file (a .rotula data-source hint, the recent files list in settings.json)
/// may be remembered and later offered to the user. Why: such a value is untrusted input. A crafted file could carry
/// <c>\\attacker\share\x.csv</c>, and merely checking whether that path exists makes Windows try to log on to the remote
/// machine with the user's credentials. So only plain local drive paths are accepted, and the check is purely textual:
/// it never touches the file system.
/// </summary>
public static class SafeLocalPath
{
    private static readonly char[] Forbidden = { '?', '*', '<', '>', '|', '"', '\0' };

    /// <summary>
    /// Returns the normalised full path when <paramref name="path"/> is an absolute local drive path such as
    /// <c>C:\Data\labels.csv</c>; otherwise null. Rejected: UNC paths (<c>\\host\share</c>), device paths (<c>\\?\</c>, <c>\\.\</c>),
    /// URLs and other schemes, relative paths, alternate data streams and wildcard characters.
    /// </summary>
    /// <param name="path">The stored value.</param>
    public static string? TryGetLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024) return null;
        string p = path.Trim();
        if (p.IndexOfAny(Forbidden) >= 0) return null;
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)) return null; // UNC and device paths
        // "C:\..." or "C:/...": a single ASCII drive letter. This also rejects "http://", "file:///", and "C:relative".
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || (p[2] != '\\' && p[2] != '/')) return null;
        if (p.IndexOf(':', 2) >= 0) return null; // NTFS alternate data stream or a second drive specifier
        if (!Path.IsPathFullyQualified(p)) return null;
        try
        {
            string full = Path.GetFullPath(p); // pure string normalisation (".." and "/"), no I/O for a rooted path
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the drive letter of an already validated local path is a fixed or removable disk and not a mapped network drive.
    /// Asks Windows for the drive type only (no network access). Call it when the user clicks, never while a file is being opened.
    /// </summary>
    public static bool IsFixedOrRemovableDrive(string localPath)
    {
        try
        {
            var type = new DriveInfo(localPath[..1]).DriveType;
            return type is DriveType.Fixed or DriveType.Removable;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
