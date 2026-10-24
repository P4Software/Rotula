using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rotula.Files.Station;

/// <summary>
/// The exit PIN of the print station. The configuration only ever holds a salted PBKDF2-SHA256 hash
/// (<c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c>, both in Base64), so a station.json that is read by everyone on the PC does not
/// give the PIN away. Comparison runs in constant time.
/// </summary>
public static class StationPin
{
    /// <summary>The prefix of every hash this version writes.</summary>
    public const string Scheme = "pbkdf2-sha256";

    /// <summary>Iterations of a new hash (the OWASP recommendation for PBKDF2-SHA256).</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Shortest PIN accepted.</summary>
    public const int MinLength = 4;

    /// <summary>Longest PIN accepted.</summary>
    public const int MaxLength = 32;

    // Bounds for a hash read from a file: fewer iterations would be weak, many more would let a crafted file freeze the station on every attempt.
    private const int MinIterations = 10_000;
    private const int MaxIterations = 10_000_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>True when <paramref name="pin"/> may be used: 4 to 32 characters without control characters, and not only spaces.</summary>
    public static bool IsValidPin(string? pin) =>
        pin is not null && pin.Trim().Length is >= MinLength and <= MaxLength && !pin.Any(char.IsControl);

    /// <summary>Hashes a new PIN with a random salt. Throws <see cref="ArgumentException"/> when <see cref="IsValidPin"/> is false.</summary>
    /// <param name="pin">The PIN as typed (surrounding spaces are ignored).</param>
    /// <param name="iterations">PBKDF2 iterations; tests pass a smaller number to stay fast.</param>
    public static string Hash(string pin, int iterations = DefaultIterations)
    {
        if (!IsValidPin(pin)) throw new ArgumentException("The PIN must have 4 to 32 characters.", nameof(pin));
        if (iterations is < MinIterations or > MaxIterations) throw new ArgumentOutOfRangeException(nameof(iterations));
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(pin, salt, iterations);
        return string.Join('$', Scheme, iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>True when <paramref name="stored"/> has the shape of a hash made by <see cref="Hash"/>.</summary>
    public static bool IsHash(string? stored) => TryParse(stored, out _, out _, out _);

    /// <summary>True when <paramref name="pin"/> is the PIN of <paramref name="stored"/>. A malformed hash never matches.</summary>
    public static bool Verify(string? pin, string? stored)
    {
        if (pin is null || !TryParse(stored, out int iterations, out byte[] salt, out byte[] expected)) return false;
        if (!IsValidPin(pin)) return false;
        return CryptographicOperations.FixedTimeEquals(Derive(pin, salt, iterations), expected);
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin.Trim()), salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var parts = stored.Trim().Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Scheme, StringComparison.Ordinal)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations) || iterations is < MinIterations or > MaxIterations) return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length >= 8 && hash.Length == HashBytes;
    }
}
