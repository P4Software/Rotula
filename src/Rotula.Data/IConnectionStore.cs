using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Rotula.Data;

/// <summary>A connection read from the store. The password itself is never exposed; only whether one is saved.</summary>
/// <param name="Settings">The password-free settings.</param>
/// <param name="HasPassword">True when a protected password is saved for it.</param>
public sealed record SavedConnection(DatabaseConnectionSettings Settings, bool HasPassword);

/// <summary>What <see cref="IConnectionStore.LoadAsync"/> found.</summary>
public sealed class ConnectionStoreContent
{
    internal ConnectionStoreContent(IReadOnlyList<SavedConnection> connections, string? errorKey, int skipped)
    {
        Connections = connections;
        ErrorKey = errorKey;
        SkippedEntries = skipped;
    }

    /// <summary>Valid saved connections (possibly empty).</summary>
    public IReadOnlyList<SavedConnection> Connections { get; }

    /// <summary>
    /// Null when the file was fine or absent. Otherwise <see cref="DataKeys.StoreDamaged"/> or <see cref="DataKeys.StoreTooLarge"/>:
    /// the file was ignored (and is preserved as <c>.damaged</c> on the next save) and the list is empty.
    /// </summary>
    public string? ErrorKey { get; }

    /// <summary>Entries inside an otherwise readable file that failed validation and were left out.</summary>
    public int SkippedEntries { get; }
}

/// <summary>
/// Persists connection settings without ever writing a password in clear. The only thing a .rotula document may keep about a
/// database is the connection's <see cref="DatabaseConnectionSettings.Name"/> (see <see cref="DatabaseQueryReference"/>).
/// </summary>
public interface IConnectionStore
{
    /// <summary>Reads the store. Never throws for a damaged or oversized file: see <see cref="ConnectionStoreContent.ErrorKey"/>.</summary>
    Task<ConnectionStoreContent> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or replaces the connection with the same name (case-insensitive). <paramref name="password"/>: null keeps the
    /// existing saved password, an empty string removes it, anything else replaces it.
    /// </summary>
    /// <exception cref="DatabaseException">Invalid settings, or the file could not be written.</exception>
    Task SaveAsync(DatabaseConnectionSettings settings, string? password, CancellationToken cancellationToken = default);

    /// <summary>Removes a connection; false when the name was not there.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Creates a ready-to-use source from a saved connection, unprotecting its password internally.</summary>
    /// <exception cref="DatabaseException"><see cref="DataKeys.ConnectionNotFound"/> or <see cref="DataKeys.StorePasswordUnreadable"/>.</exception>
    Task<IDatabaseSource> OpenAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>Protects small secrets so only the current user on this machine can read them back.</summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="data"/>; <paramref name="entropy"/> must be supplied again to decrypt.</summary>
    byte[] Protect(byte[] data, byte[] entropy);

    /// <summary>Decrypts; throws <see cref="CryptographicException"/> when the data, user or entropy do not match.</summary>
    byte[] Unprotect(byte[] protectedData, byte[] entropy);
}

/// <summary>Windows DPAPI, CurrentUser scope.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <inheritdoc />
    public byte[] Protect(byte[] data, byte[] entropy) => ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedData, byte[] entropy) => ProtectedData.Unprotect(protectedData, entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// What a .rotula file may store about a database data source: the NAME of a saved connection and the query text. It has
/// no member that can hold host, user, password or connection string, so none can leak into a document.
/// </summary>
/// <param name="ConnectionName">Name of a connection in the <see cref="IConnectionStore"/>.</param>
/// <param name="Query">The SELECT text (or empty when a whole table is used).</param>
public sealed record DatabaseQueryReference(string ConnectionName, string Query);

internal static class StoreEntropy
{
    /// <summary>Per-entry entropy: binds a protected blob to its connection name so blobs cannot be swapped between entries.</summary>
    public static byte[] For(string connectionName) => Encoding.UTF8.GetBytes("Rotula.Connections.v1|" + connectionName.ToLowerInvariant());
}
