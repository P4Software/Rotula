using System.Security.Cryptography;
using System.Text;

namespace Rotula.Engine;

/// <summary>
/// API keys of the REST server. The settings file keeps only the SHA-256 hash of each key, so reading the file (or a backup of it) does not
/// give anyone a working key. A key is 32 random bytes, written as 64 hexadecimal characters.
/// </summary>
public static class ApiKeys
{
    /// <summary>The request header that carries the key.</summary>
    public const string Header = "X-Api-Key";

    /// <summary>Makes a new random key and its hash.</summary>
    public static (string Key, string Hash) Create()
    {
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return (key, Hash(key));
    }

    /// <summary>The hash stored in the settings file: SHA-256 of the key's UTF-8 bytes, lower-case hexadecimal.</summary>
    public static string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()))).ToLowerInvariant();
    }

    /// <summary>True when <paramref name="presented"/> hashes to one of <paramref name="hashes"/>. Compared in constant time.</summary>
    public static bool Matches(string? presented, IReadOnlyCollection<string> hashes)
    {
        if (string.IsNullOrWhiteSpace(presented) || presented.Length > 256 || hashes.Count == 0) return false;
        byte[] candidate = Encoding.ASCII.GetBytes(Hash(presented));
        bool hit = false;
        // Every stored hash is compared, so the time taken does not tell which one (if any) matched.
        foreach (var hash in hashes)
            hit |= CryptographicOperations.FixedTimeEquals(candidate, Encoding.ASCII.GetBytes(hash.Trim().ToLowerInvariant()));
        return hit;
    }

    /// <summary>True for a value that looks like a stored hash (64 hexadecimal characters).</summary>
    public static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
