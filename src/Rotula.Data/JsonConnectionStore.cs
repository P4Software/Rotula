using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rotula.Data;

/// <summary>
/// <see cref="IConnectionStore"/> backed by <c>%AppData%\Rotula\connections.json</c>. The file is treated as untrusted input
/// (size limit, entry limit, per-entry validation), written atomically, and holds passwords only as DPAPI-protected blobs.
/// </summary>
public sealed class JsonConnectionStore : IConnectionStore
{
    /// <summary>Largest file that will be read, in bytes.</summary>
    public const long MaxFileBytes = 1_048_576;

    /// <summary>Most connections a file may hold.</summary>
    public const int MaxEntries = 500;

    private const int CurrentVersion = 1;
    private const int MaxProtectedBase64 = 16_384;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 8,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly string _path;
    private readonly ISecretProtector? _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The default location: <c>%AppData%\Rotula\connections.json</c>.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rotula", "connections.json");

    /// <summary>Creates a store. Tests inject a temp <paramref name="path"/> and a fake <paramref name="protector"/>.</summary>
    /// <param name="path">File path; null uses <see cref="DefaultPath"/>.</param>
    /// <param name="protector">Password protection; null uses DPAPI (Windows only).</param>
    public JsonConnectionStore(string? path = null, ISecretProtector? protector = null)
    {
        _path = path ?? DefaultPath;
        _protector = protector ?? (OperatingSystem.IsWindows() ? new DpapiSecretProtector() : null);
    }

    // ---------------------------------------------------------------- file model (private, never public API)

    private sealed class FileModel
    {
        public int Version { get; set; } = CurrentVersion;
        public List<EntryModel> Connections { get; set; } = new();
    }

    private sealed class EntryModel
    {
        public string? Name { get; set; }
        public DatabaseProvider Provider { get; set; }
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? Database { get; set; }
        public DatabaseAuthentication Authentication { get; set; }
        public string? UserName { get; set; }
        public bool Encrypt { get; set; } = true;
        public bool TrustServerCertificate { get; set; }
        public string? FilePath { get; set; }
        public string? OdbcConnectionString { get; set; }
        public string? Dsn { get; set; }
        public int CommandTimeoutSeconds { get; set; } = DatabaseConnectionSettings.DefaultCommandTimeoutSeconds;
        public int ConnectTimeoutSeconds { get; set; } = DatabaseConnectionSettings.DefaultConnectTimeoutSeconds;
        /// <summary>Base64 of the DPAPI blob. The only place a password ever appears, and never in clear.</summary>
        public string? ProtectedPassword { get; set; }
    }

    // ---------------------------------------------------------------- IConnectionStore

    /// <inheritdoc />
    public async Task<ConnectionStoreContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var list = read.Entries.Select(e => new SavedConnection(ToSettings(e), !string.IsNullOrEmpty(e.ProtectedPassword))).ToList();
            return new ConnectionStoreContent(list, read.ErrorKey, read.Skipped);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task SaveAsync(DatabaseConnectionSettings settings, string? password, CancellationToken cancellationToken = default)
    {
        ConnectionStrings.Validate(settings);
        if (_protector is null && password is { Length: > 0 }) throw new DatabaseException(DataKeys.StoreProtectionUnavailable);

        // A raw ODBC string must not keep its PWD= part in the file: move it to the protected slot.
        string? odbc = settings.OdbcConnectionString;
        if (!string.IsNullOrWhiteSpace(odbc))
        {
            var (sanitized, embedded) = SecretRedactor.ExtractPassword(odbc);
            odbc = sanitized;
            if (embedded is not null && password is null) password = embedded;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var entries = read.Entries;
            int at = entries.FindIndex(e => string.Equals(e.Name, settings.Name, StringComparison.OrdinalIgnoreCase));
            string? blob = at >= 0 ? entries[at].ProtectedPassword : null;
            if (password is not null)
                blob = password.Length == 0 ? null : Convert.ToBase64String(_protector!.Protect(Encoding.UTF8.GetBytes(password), StoreEntropy.For(settings.Name)));

            var entry = new EntryModel
            {
                Name = settings.Name, Provider = settings.Provider, Host = settings.Host, Port = settings.Port,
                Database = settings.Database, Authentication = settings.Authentication, UserName = settings.UserName,
                Encrypt = settings.Encrypt, TrustServerCertificate = settings.TrustServerCertificate, FilePath = settings.FilePath,
                OdbcConnectionString = odbc, Dsn = settings.Dsn, CommandTimeoutSeconds = settings.CommandTimeoutSeconds,
                ConnectTimeoutSeconds = settings.ConnectTimeoutSeconds, ProtectedPassword = blob,
            };
            if (at >= 0) entries[at] = entry;
            else
            {
                if (entries.Count >= MaxEntries) throw new DatabaseException(DataKeys.StoreWriteFailed);
                entries.Add(entry);
            }
            await WriteAsync(entries, read.ErrorKey is not null, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
            int removed = read.Entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            await WriteAsync(read.Entries, read.ErrorKey is not null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<IDatabaseSource> OpenAsync(string name, CancellationToken cancellationToken = default)
    {
        EntryModel? entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
            entry = read.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        finally { _gate.Release(); }

        if (entry is null) throw new DatabaseException(DataKeys.ConnectionNotFound, null, new[] { name });
        string? password = null;
        if (!string.IsNullOrEmpty(entry.ProtectedPassword))
        {
            try
            {
                if (_protector is null) throw new DatabaseException(DataKeys.StoreProtectionUnavailable);
                password = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(entry.ProtectedPassword), StoreEntropy.For(entry.Name!)));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new DatabaseException(DataKeys.StorePasswordUnreadable, null, new[] { name });
            }
        }
        return DatabaseSource.Create(ToSettings(entry), password);
    }

    // ---------------------------------------------------------------- file handling

    private sealed record ReadResult(List<EntryModel> Entries, string? ErrorKey, int Skipped);

    private async Task<ReadResult> ReadAsync(CancellationToken ct)
    {
        var empty = new List<EntryModel>();
        FileInfo info = new(_path);
        if (!info.Exists) return new(empty, null, 0);
        if (info.Length > MaxFileBytes) return new(empty, DataKeys.StoreTooLarge, 0);

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(empty, DataKeys.StoreDamaged, 0); }
        if (bytes.Length > MaxFileBytes) return new(empty, DataKeys.StoreTooLarge, 0);

        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(empty, DataKeys.StoreDamaged, 0);
            if (!root.TryGetProperty("version", out var v) && !root.TryGetProperty("Version", out v)) return new(empty, DataKeys.StoreDamaged, 0);
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int version) || version != CurrentVersion) return new(empty, DataKeys.StoreDamaged, 0);
            if (!root.TryGetProperty("connections", out var arr) && !root.TryGetProperty("Connections", out arr)) return new(empty, DataKeys.StoreDamaged, 0);
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > MaxEntries) return new(empty, DataKeys.StoreDamaged, 0);

            var entries = new List<EntryModel>();
            int skipped = 0;
            foreach (var el in arr.EnumerateArray())
            {
                var e = TryParseEntry(el);
                if (e is null || entries.Any(x => string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                entries.Add(e);
            }
            return new(entries, null, skipped);
        }
        catch (JsonException) { return new(empty, DataKeys.StoreDamaged, 0); }
    }

    /// <summary>Deserializes and validates one entry exactly as a user-typed one would be; null if anything is off.</summary>
    private static EntryModel? TryParseEntry(JsonElement el)
    {
        try
        {
            var e = el.Deserialize<EntryModel>(Json);
            if (e?.Name is null) return null;
            if (e.ProtectedPassword is { } p && (p.Length > MaxProtectedBase64 || !IsBase64(p))) return null;
            ConnectionStrings.Validate(ToSettings(e));
            // Defence against a hand-edited file: a clear password must never be tolerated inside the ODBC string.
            if (e.OdbcConnectionString is { } o && SecretRedactor.ExtractPassword(o).Password is not null) return null;
            return e;
        }
        catch (Exception ex) when (ex is JsonException or DatabaseException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsBase64(string s)
    {
        try { Convert.FromBase64String(s); return true; }
        catch (FormatException) { return false; }
    }

    private async Task WriteAsync(List<EntryModel> entries, bool replacingDamaged, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(_path);
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Keep an unreadable file for the user instead of silently overwriting what they may want to repair.
            if (replacingDamaged && File.Exists(_path)) File.Move(_path, _path + ".damaged", overwrite: true);

            var model = new FileModel { Version = CurrentVersion, Connections = entries };
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(model, Json), ct).ConfigureAwait(false);
            // Write-then-swap so a crash never leaves a half-written file.
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DatabaseException(DataKeys.StoreWriteFailed);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp file is harmless */ }
        }
    }

    private static DatabaseConnectionSettings ToSettings(EntryModel e) => new()
    {
        Name = e.Name ?? string.Empty, Provider = e.Provider, Host = e.Host, Port = e.Port, Database = e.Database,
        Authentication = e.Authentication, UserName = e.UserName, Encrypt = e.Encrypt, TrustServerCertificate = e.TrustServerCertificate,
        FilePath = e.FilePath, OdbcConnectionString = e.OdbcConnectionString, Dsn = e.Dsn,
        CommandTimeoutSeconds = e.CommandTimeoutSeconds, ConnectTimeoutSeconds = e.ConnectTimeoutSeconds,
    };
}
