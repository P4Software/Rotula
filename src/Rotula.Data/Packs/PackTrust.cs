using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rotula.Data.Packs;

/// <summary>Hashes a whole pack folder so that swapping ANY file (a dependency, a native DLL, the manifest) changes the fingerprint.</summary>
public static class PackFingerprint
{
    /// <summary>Most files hashed per pack.</summary>
    public const int MaxFiles = 2000;
    /// <summary>Most bytes hashed per pack.</summary>
    public const long MaxBytes = 1L << 30;

    /// <summary>
    /// SHA-256 (lower-case hex) over the sorted relative paths and the SHA-256 of each file's content. Returns null when the folder
    /// contains a link (junction or symlink), too many files or too many bytes: such a folder is never trusted.
    /// </summary>
    public static string? Compute(string folder)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists || IsLink(root)) return null;
        var files = new List<FileInfo>();
        long total = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (IsLink(entry)) return null;
                if (entry is DirectoryInfo d) stack.Push(d);
                else if (entry is FileInfo f)
                {
                    files.Add(f);
                    total += f.Length;
                    if (files.Count > MaxFiles || total > MaxBytes) return null;
                }
            }
        }

        using var outer = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in files.OrderBy(f => Path.GetRelativePath(root.FullName, f.FullName).Replace('\\', '/').ToLowerInvariant(), StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root.FullName, f.FullName).Replace('\\', '/').ToLowerInvariant();
            outer.AppendData(Encoding.UTF8.GetBytes(rel));
            outer.AppendData(new byte[] { 0 });
            using var stream = f.OpenRead();
            outer.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(outer.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>True for junctions and symbolic links, which can redirect a trusted-looking folder somewhere else.</summary>
    public static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}

/// <summary>An approval the user gave to one pack at one fingerprint.</summary>
/// <param name="PackId">Pack id.</param>
/// <param name="Sha256">Fingerprint of the folder at the time of approval.</param>
/// <param name="TrustedAtUtc">When the user approved.</param>
public sealed record TrustedPack(string PackId, string Sha256, DateTime TrustedAtUtc);

/// <summary>
/// Remembers which packs from the user's own folder the user approved. Loading a pack runs its code with the user's rights, so a pack
/// is only loaded when its id AND the SHA-256 of every file match an approval; a changed pack asks again. Stored in
/// <c>%AppData%\Rotula\trusted-packs.json</c> (version 1).
/// </summary>
public sealed class PackTrustStore
{
    private const int CurrentVersion = 1;
    private const long MaxFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8 };
    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Default location.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rotula", "trusted-packs.json");

    /// <summary>Creates a store over <paramref name="path"/> (null for the default).</summary>
    public PackTrustStore(string? path = null) => _path = path ?? DefaultPath;

    private sealed class FileModel
    {
        public int Version { get; set; } = CurrentVersion;
        public List<TrustedPack> Packs { get; set; } = new();
    }

    /// <summary>All approvals. A damaged or oversized file reads as "nothing approved" (never as "everything approved").</summary>
    public IReadOnlyList<TrustedPack> Load()
    {
        lock (_gate) return Read().Packs.ToList();
    }

    /// <summary>True when this exact pack content was approved.</summary>
    public bool IsTrusted(string packId, string sha256)
        => Load().Any(t => t.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase) && t.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the pack was approved at some earlier fingerprint (so a mismatch means "changed since you approved it").</summary>
    public bool HasEarlierApproval(string packId)
        => Load().Any(t => t.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records the user's approval, replacing an earlier one for the same pack id. Call only after the user confirmed the warning.</summary>
    public void Trust(string packId, string sha256)
    {
        if (!ProviderRegistry.IsValidId(packId) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new ArgumentException("Not a pack id or fingerprint.");
        lock (_gate)
        {
            var model = Read();
            model.Packs.RemoveAll(t => t.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase));
            model.Packs.Add(new TrustedPack(packId, sha256.ToLowerInvariant(), DateTime.UtcNow));
            Write(model);
        }
    }

    /// <summary>Withdraws an approval.</summary>
    public void Revoke(string packId)
    {
        lock (_gate)
        {
            var model = Read();
            if (model.Packs.RemoveAll(t => t.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase)) > 0) Write(model);
        }
    }

    private FileModel Read()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length > MaxFileBytes) return new FileModel();
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllBytes(_path), Json);
            if (m is null || m.Version != CurrentVersion) return new FileModel();
            m.Packs = m.Packs.Where(t => t is not null && ProviderRegistry.IsValidId(t.PackId) && t.Sha256?.Length == 64).ToList();
            return m;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new FileModel();
        }
    }

    private void Write(FileModel model)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(model, Json));
            if (File.Exists(_path)) File.Replace(temp, _path, null); else File.Move(temp, _path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp file is harmless */ }
        }
    }
}
