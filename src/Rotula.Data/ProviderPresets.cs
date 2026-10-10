using System.Data.Odbc;
using System.Reflection;
using System.Text.Json;

namespace Rotula.Data;

/// <summary>
/// How to build an ODBC connection string for a preset that is a file format served by a vendor driver (Microsoft Access, Excel,
/// text files). The driver is never bundled: the user installs it, and a missing driver produces a plain-language hint.
/// </summary>
public sealed record OdbcPresetInfo
{
    /// <summary>Driver names in order of preference, exactly as the ODBC administrator lists them.</summary>
    public IReadOnlyList<string> DriverNames { get; init; } = Array.Empty<string>();

    /// <summary>Extra connection-string keys. The value <c>{path}</c> is replaced by the chosen file or folder.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>The key that receives the path (<c>Dbq</c>).</summary>
    public string PathKey { get; init; } = "Dbq";

    /// <summary><c>file</c> or <c>folder</c> (the text driver reads a folder of files).</summary>
    public string PathKind { get; init; } = "file";

    /// <summary>Accepted file extensions for <c>file</c> presets, lower case with the dot.</summary>
    public IReadOnlyList<string> FileExtensions { get; init; } = Array.Empty<string>();

    /// <summary>Dialect id of the engine behind the driver (<c>jet</c>), recorded in the settings so the guard uses it.</summary>
    public string? UnderlyingEngine { get; init; }
}

/// <summary>
/// A named starting point over a provider: it fills in the port, secure TLS defaults, provider options and hints for a well-known
/// service (Supabase, Amazon RDS, Microsoft Access...). Presets are data (<c>Presets/presets.json</c>), not code.
/// </summary>
public sealed record ProviderPreset
{
    /// <summary>Stable lower-case id (letters, digits, dashes).</summary>
    public required string Id { get; init; }
    /// <summary>Message key of the name, <c>Data.Preset.*</c>.</summary>
    public required string NameKey { get; init; }
    /// <summary>English name.</summary>
    public required string Name { get; init; }
    /// <summary>The provider that connects (<c>postgresql</c>, <c>mysql</c>, <c>sqlserver</c>, <c>odbc</c>...).</summary>
    public required string ProviderId { get; init; }
    /// <summary>Category override for the UI list; null uses the provider's.</summary>
    public ProviderCategory? Category { get; init; }
    /// <summary>Host patterns (<c>*</c> wildcard, case-insensitive) used to recognise the service from a pasted host name.</summary>
    public IReadOnlyList<string> HostPatterns { get; init; } = Array.Empty<string>();
    /// <summary>Port to suggest; null keeps the provider default.</summary>
    public int? DefaultPort { get; init; }
    /// <summary>Encrypt the connection. Always true in the shipped catalogue (secure by default).</summary>
    public bool Encrypt { get; init; } = true;
    /// <summary>Trust an unverifiable certificate. Always false in the shipped catalogue.</summary>
    public bool TrustServerCertificate { get; init; }
    /// <summary>Provider options set by the preset (for example <c>noStartupOptions</c>).</summary>
    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;
    /// <summary>True when the host patterns are a connection pooler (PgBouncer): the startup <c>options</c> parameter is not sent to such hosts.</summary>
    public bool PoolerHost { get; init; }
    /// <summary>Hint message keys (<c>Data.Hint.*</c>) the UI shows next to the form.</summary>
    public IReadOnlyList<string> HintKeys { get; init; } = Array.Empty<string>();
    /// <summary>Hint shown (as the reason) when the ODBC driver of this preset is not installed.</summary>
    public string? DriverMissingHintKey { get; init; }
    /// <summary>ODBC file-format information, for presets over the ODBC provider.</summary>
    public OdbcPresetInfo? Odbc { get; init; }
    /// <summary>True when the service accepts Microsoft Entra sign-in only (needs a later pack).</summary>
    public bool RequiresEntra { get; init; }
    /// <summary>False when the host patterns or defaults could not be confirmed against the vendor's documentation.</summary>
    public bool Verified { get; init; } = true;

    /// <summary>True when <paramref name="host"/> matches one of <see cref="HostPatterns"/>.</summary>
    public bool MatchesHost(string? host)
        => !string.IsNullOrWhiteSpace(host) && HostPatterns.Any(p => ProviderPresets.GlobMatch(p, host.Trim()));

    /// <summary>
    /// Creates settings for a server preset: provider, port, TLS flags and options filled in. The caller adds host, database and account.
    /// </summary>
    public DatabaseConnectionSettings CreateSettings(string connectionName, string? host = null, string? database = null, string? userName = null)
    {
        var en = ProviderIds.ToEnum(ProviderId);
        return new DatabaseConnectionSettings
        {
            Name = connectionName,
            Provider = en,
            ProviderId = en == DatabaseProvider.Custom ? ProviderId : null,
            PresetId = Id,
            Host = host,
            Port = DefaultPort,
            Database = database,
            UserName = userName,
            Authentication = DatabaseAuthentication.UserPassword,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            Extras = Extras,
        };
    }

    /// <summary>
    /// Creates settings for an ODBC file preset (Access, Excel, text). The connection string is made with the ODBC string builder, so the
    /// path cannot add keywords; UNC and URL paths are refused. The first installed driver is used, else the preferred one.
    /// </summary>
    /// <exception cref="DatabaseException"><see cref="DataKeys.InvalidSettings"/> when the preset is not an ODBC file preset or the path is not acceptable.</exception>
    public DatabaseConnectionSettings CreateOdbcSettings(string connectionName, string path, IReadOnlyCollection<string>? installedDrivers = null)
    {
        if (Odbc is null) throw ProviderValidation.Invalid(nameof(Odbc));
        ProviderValidation.RequireLocalPath(path, nameof(DatabaseConnectionSettings.FilePath));
        if (path.Contains(';') || path.Contains('{') || path.Contains('}')) throw ProviderValidation.Invalid(nameof(DatabaseConnectionSettings.FilePath));
        if (Odbc.PathKind == "file" && Odbc.FileExtensions.Count > 0
            && !Odbc.FileExtensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase))
            throw ProviderValidation.Invalid(nameof(DatabaseConnectionSettings.FilePath));

        installedDrivers ??= OdbcDriverCatalogue.GetInstalledDriverNames();
        string driver = Odbc.DriverNames.FirstOrDefault(d => installedDrivers.Contains(d, StringComparer.OrdinalIgnoreCase)) ?? Odbc.DriverNames[0];
        var b = new OdbcConnectionStringBuilder { Driver = driver };
        b[Odbc.PathKey] = path;
        foreach (var kv in Odbc.Parameters) b[kv.Key] = kv.Value.Replace("{path}", path, StringComparison.Ordinal);

        var extras = Extras;
        if (Odbc.UnderlyingEngine is { } engine) extras = extras.With(OdbcDescriptor.UnderlyingEngine, engine);
        return new DatabaseConnectionSettings
        {
            Name = connectionName,
            Provider = DatabaseProvider.Odbc,
            PresetId = Id,
            OdbcConnectionString = b.ConnectionString,
            Authentication = DatabaseAuthentication.WindowsIntegrated,
            Extras = extras,
        };
    }
}

/// <summary>Lists the ODBC drivers installed on this machine (best effort, Windows only).</summary>
public static class OdbcDriverCatalogue
{
    /// <summary>Names as the ODBC administrator shows them; empty when they cannot be read or the OS has no ODBC registry.</summary>
    public static IReadOnlyList<string> GetInstalledDriverNames()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers");
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>The preset catalogue: loading from JSON, validation and host recognition.</summary>
public static class ProviderPresets
{
    private const string ResourceName = "Rotula.Data.Presets.presets.json";
    private static readonly Lazy<IReadOnlyList<ProviderPreset>> BuiltinList = new(LoadBuiltin);

    /// <summary>The presets that ship with Rotula (empty only if the embedded file is damaged, which a test would catch).</summary>
    public static IReadOnlyList<ProviderPreset> Builtin => BuiltinList.Value;

    /// <summary>Finds a built-in preset by id (case-insensitive); null for none.</summary>
    public static ProviderPreset? Find(string? id)
        => id is null ? null : Builtin.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="host"/> belongs to a connection pooler (a preset with <see cref="ProviderPreset.PoolerHost"/>).</summary>
    public static bool IsPoolerHost(string? host) => Builtin.Any(p => p.PoolerHost && p.MatchesHost(host));

    /// <summary>
    /// Presets whose host patterns match <paramref name="host"/>, most specific first (more literal characters). When <paramref name="port"/>
    /// is given, presets with another default port rank after those that agree.
    /// </summary>
    public static IReadOnlyList<ProviderPreset> Detect(string? host, int? port = null, IEnumerable<ProviderPreset>? catalogue = null)
        => (catalogue ?? Builtin).Where(p => p.MatchesHost(host))
            .OrderByDescending(p => port is not null && p.DefaultPort == port)
            .ThenByDescending(p => p.HostPatterns.Where(x => GlobMatch(x, host!.Trim())).Max(x => x.Count(c => c != '*')))
            .ToList();

    /// <summary>Case-insensitive match where <c>*</c> stands for any run of characters.</summary>
    public static bool GlobMatch(string pattern, string text)
    {
        // Iterative wildcard match, no regex: patterns come from data files and must never become a ReDoS vector.
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (p < pattern.Length && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])) { p++; t++; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static IReadOnlyList<ProviderPreset> LoadBuiltin()
    {
        try
        {
            using var stream = typeof(ProviderPresets).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return Array.Empty<ProviderPreset>();
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd(), out _);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return Array.Empty<ProviderPreset>();
        }
    }

    /// <summary>
    /// Parses a preset catalogue. Entries that are malformed are left out and described in <paramref name="problems"/> (English, for
    /// developers); a whole-file failure returns an empty list and one problem.
    /// </summary>
    public static IReadOnlyList<ProviderPreset> Parse(string json, out IReadOnlyList<string> problems)
    {
        var errors = new List<string>();
        var list = new List<ProviderPreset>();
        problems = errors;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("presets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                errors.Add("The file has no 'presets' array.");
                return list;
            }
            int index = 0;
            foreach (var el in arr.EnumerateArray())
            {
                index++;
                try { list.Add(ParseOne(el)); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
                {
                    errors.Add($"Preset #{index}: {ex.Message}");
                }
            }
        }
        catch (JsonException ex)
        {
            errors.Add("Not valid JSON: " + ex.Message);
        }
        return list;
    }

    private static ProviderPreset ParseOne(JsonElement e)
    {
        string Str(string name) => e.GetProperty(name).GetString() ?? throw new JsonException($"'{name}' is empty.");
        string? Opt(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Flag(string name, bool dflt) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : dflt;
        IReadOnlyList<string> Strings(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : Array.Empty<string>();

        ProviderCategory? cat = null;
        if (Opt("category") is { } c) cat = Enum.Parse<ProviderCategory>(c, ignoreCase: true);
        var extras = new List<KeyValuePair<string, string>>();
        if (e.TryGetProperty("extras", out var ex) && ex.ValueKind == JsonValueKind.Object)
            foreach (var kv in ex.EnumerateObject()) extras.Add(new(kv.Name, kv.Value.ValueKind switch
            {
                JsonValueKind.True => "true", JsonValueKind.False => "false", _ => kv.Value.GetString() ?? string.Empty,
            }));
        OdbcPresetInfo? odbc = null;
        if (e.TryGetProperty("odbc", out var o) && o.ValueKind == JsonValueKind.Object)
        {
            var parms = new Dictionary<string, string>();
            if (o.TryGetProperty("parameters", out var pp) && pp.ValueKind == JsonValueKind.Object)
                foreach (var kv in pp.EnumerateObject()) parms[kv.Name] = kv.Value.GetString() ?? string.Empty;
            odbc = new OdbcPresetInfo
            {
                DriverNames = o.GetProperty("drivers").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList(),
                Parameters = parms,
                PathKey = o.TryGetProperty("pathKey", out var pk) ? pk.GetString() ?? "Dbq" : "Dbq",
                PathKind = o.TryGetProperty("pathKind", out var pkd) ? pkd.GetString() ?? "file" : "file",
                FileExtensions = o.TryGetProperty("extensions", out var fe) ? fe.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : Array.Empty<string>(),
                UnderlyingEngine = o.TryGetProperty("underlyingEngine", out var ue) ? ue.GetString() : null,
            };
        }
        return new ProviderPreset
        {
            Id = Str("id"), NameKey = Str("nameKey"), Name = Str("name"), ProviderId = Str("providerId"), Category = cat,
            HostPatterns = Strings("hostPatterns"),
            DefaultPort = e.TryGetProperty("defaultPort", out var port) && port.ValueKind == JsonValueKind.Number ? port.GetInt32() : null,
            Encrypt = Flag("encrypt", true), TrustServerCertificate = Flag("trustServerCertificate", false),
            Extras = new ProviderExtras(extras), PoolerHost = Flag("poolerHost", false),
            HintKeys = Strings("hintKeys"), DriverMissingHintKey = Opt("driverMissingHintKey"), Odbc = odbc,
            RequiresEntra = Flag("requiresEntra", false), Verified = Flag("verified", true),
        };
    }

    /// <summary>
    /// Checks a preset against a registry: every problem is one English sentence for developers. An empty list means the preset is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(ProviderPreset p, ProviderRegistry registry)
    {
        var problems = new List<string>();
        string id = p.Id;
        if (!ProviderRegistry.IsValidId(p.Id)) problems.Add($"{id}: id must be lower-case letters, digits and dashes.");
        if (!p.NameKey.StartsWith("Data.Preset.", StringComparison.Ordinal)) problems.Add($"{id}: nameKey must start with 'Data.Preset.'.");
        if (string.IsNullOrWhiteSpace(p.Name)) problems.Add($"{id}: name is empty.");
        var provider = registry.TryGet(p.ProviderId);
        if (provider is null) { problems.Add($"{id}: provider '{p.ProviderId}' is not registered."); return problems; }
        if (p.DefaultPort is < 1 or > 65535) problems.Add($"{id}: port out of range.");
        if (p.DefaultPort is not null && provider.DefaultPort is null && p.Odbc is null) problems.Add($"{id}: the provider has no port.");
        if (!p.Encrypt || p.TrustServerCertificate) problems.Add($"{id}: presets must be secure by default (encrypt on, trust-certificate off).");
        foreach (string pattern in p.HostPatterns)
        {
            if (pattern.Length is 0 or > 253 || pattern.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '*' or '_')) || pattern.Replace("*", "").Length < 3)
                problems.Add($"{id}: host pattern '{pattern}' is empty, too broad or has characters a host name cannot have.");
        }
        foreach (var kv in p.Extras)
        {
            var option = provider.Options.FirstOrDefault(o => o.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && !ProviderOptionKeys.IsStandard(o.Key));
            if (option is null) problems.Add($"{id}: extra '{kv.Key}' is not an option of provider '{p.ProviderId}'.");
        }
        foreach (string hint in p.HintKeys.Concat(p.DriverMissingHintKey is null ? Array.Empty<string>() : new[] { p.DriverMissingHintKey }))
            if (!hint.StartsWith("Data.Hint.", StringComparison.Ordinal)) problems.Add($"{id}: hint key '{hint}' must start with 'Data.Hint.'.");
        if (p.PoolerHost && p.HostPatterns.Count == 0) problems.Add($"{id}: a pooler preset needs host patterns.");
        if (p.Odbc is { } odbc)
        {
            if (p.ProviderId != ProviderIds.Odbc) problems.Add($"{id}: only ODBC presets can have an 'odbc' block.");
            if (odbc.DriverNames.Count == 0 || odbc.DriverNames.Any(string.IsNullOrWhiteSpace)) problems.Add($"{id}: odbc.drivers is empty.");
            if (odbc.PathKind is not ("file" or "folder")) problems.Add($"{id}: odbc.pathKind must be 'file' or 'folder'.");
            if (odbc.PathKind == "file" && odbc.FileExtensions.Count == 0) problems.Add($"{id}: a file preset needs extensions.");
            if (odbc.FileExtensions.Any(x => !x.StartsWith('.') || x != x.ToLowerInvariant())) problems.Add($"{id}: extensions are lower case with a dot.");
            if (odbc.UnderlyingEngine is { } eng && SqlDialects.ById(eng) is null) problems.Add($"{id}: unknown underlying engine '{eng}'.");
            if (odbc.Parameters.Keys.Any(k => k.Contains(';') || k.Contains('='))) problems.Add($"{id}: odbc parameter names must be plain.");
            if (p.DriverMissingHintKey is null) problems.Add($"{id}: an ODBC file preset needs driverMissingHintKey.");
        }
        return problems;
    }
}
