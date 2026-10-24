using System.Text.Json;
using System.Text.Json.Serialization;
using Rotula.Printing;

namespace Rotula.Engine;

/// <summary>
/// The REST server's rules, from <c>rotula-engine.settings.json</c>. Everything a caller can reach is listed explicitly: the addresses the
/// server listens on, the API keys (as hashes), the label folders and the printers. Nothing is allowed by default.
/// </summary>
public sealed record EngineSettings
{
    /// <summary>The settings file name, looked for next to the program unless <c>--settings</c> names another.</summary>
    public const string FileName = "rotula-engine.settings.json";

    /// <summary>Default listening address: this computer only.</summary>
    public const string DefaultUrl = "http://127.0.0.1:9180";

    /// <summary>Addresses to listen on, for example <c>http://127.0.0.1:9180</c> or <c>https://0.0.0.0:9443</c>.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [DefaultUrl];

    /// <summary>SHA-256 hashes of the API keys that may call the server (<c>rotula new-key</c> makes a key and its hash).</summary>
    public IReadOnlyList<string> ApiKeyHashes { get; init; } = [];

    /// <summary>Folders labels may be printed from; a request names a label relative to one of them.</summary>
    public IReadOnlyList<string> LabelFolders { get; init; } = [];

    /// <summary>Printers the server may print to and query: Windows printer names, or <c>host</c> / <c>host:port</c> of network printers.</summary>
    public IReadOnlyList<string> AllowedPrinters { get; init; } = [];

    /// <summary>Accept labels sent as content in the request (a caller then can print any design). Off by default.</summary>
    public bool AllowInlineLabels { get; init; }

    /// <summary>Most copies per label or record.</summary>
    public int MaxCopies { get; init; } = 1000;

    /// <summary>Most records per request.</summary>
    public int MaxRecords { get; init; } = 10_000;

    /// <summary>Largest request body, in bytes.</summary>
    public long MaxRequestBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Largest label file, in bytes.</summary>
    public long MaxLabelBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Language of the messages when the request sends no <c>Accept-Language</c>: <c>en</c>, <c>es</c>, <c>pt-BR</c> or <c>fr-CA</c>.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Counter file of serial numbers that remember their value; null = the running account's own (<c>%AppData%\Rotula\counters.json</c>).</summary>
    public string? CounterFile { get; init; }

    /// <summary>The request limits as the engine takes them.</summary>
    public EngineLimits Limits => new(MaxCopies, MaxRecords);

    /// <summary>True when the server may print to (or query) <paramref name="target"/>. Names and hosts are compared ignoring case.</summary>
    public bool IsPrinterAllowed(PrinterTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var entry in AllowedPrinters)
        {
            string e = entry.Trim();
            if (target.Transport == PrinterTransport.Spooler)
            {
                if (string.Equals(e, target.PrinterName, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            // "host" means the default raw port; "host:port" exactly that port.
            int colon = e.LastIndexOf(':');
            string host = colon > 0 ? e[..colon] : e;
            int port = colon > 0 && int.TryParse(e[(colon + 1)..], out int p) ? p : PrinterTarget.DefaultTcpPort;
            if (string.Equals(host, target.Host, StringComparison.OrdinalIgnoreCase) && port == target.Port) return true;
        }
        return false;
    }
}

/// <summary>The outcome of reading the settings: the settings, or the reasons they cannot be used.</summary>
/// <param name="Settings">The settings, or null when <paramref name="Errors"/> is not empty.</param>
/// <param name="Errors">Problems that stop the server (as sentences, in the requested language).</param>
/// <param name="Warnings">Things worth knowing that do not stop it.</param>
/// <param name="Path">The file that was read.</param>
public sealed record EngineSettingsLoad(EngineSettings? Settings, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string Path);

/// <summary>Reads <c>rotula-engine.settings.json</c>. Unknown names are an error, so a typo never silently leaves a rule out.</summary>
public static class EngineSettingsLoader
{
    private const long MaxFileBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    /// <summary>Loads and checks the settings. Never throws.</summary>
    public static EngineSettingsLoad Load(string path, EngineText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var errors = new List<string>();
        var warnings = new List<string>();
        EngineSettings? settings;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new(null, [text.Get("Engine.Settings.Missing", path)], [], path);
            if (info.Length > MaxFileBytes) return new(null, [text.Get("Engine.Settings.Invalid", path, "> 256 KB")], [], path);
            settings = JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(info.FullName), Options);
        }
        catch (JsonException ex)
        {
            return new(null, [text.Get("Engine.Settings.Invalid", path, ex.Message)], [], path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(null, [text.Get("Engine.Settings.Invalid", path, ex.Message)], [], path);
        }
        if (settings is null) return new(null, [text.Get("Engine.Settings.Invalid", path, "null")], [], path);

        if (settings.ApiKeyHashes.Count == 0) errors.Add(text.Get("Engine.Settings.NoKeys", path));
        foreach (var hash in settings.ApiKeyHashes.Where(h => !ApiKeys.IsHash(h?.Trim())))
            errors.Add(text.Get("Engine.Settings.BadHash", hash ?? string.Empty));
        if (settings.Urls.Count == 0) errors.Add(text.Get("Engine.Settings.Invalid", path, "urls"));
        foreach (var url in settings.Urls)
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                errors.Add(text.Get("Engine.Settings.Invalid", path, "urls: " + url));
        if (settings.MaxCopies is < 1 or > 99_999_999) errors.Add(text.Get("Engine.Settings.Invalid", path, "maxCopies"));
        if (settings.MaxRecords is < 1 or > 1_000_000) errors.Add(text.Get("Engine.Settings.Invalid", path, "maxRecords"));
        if (settings.MaxRequestBytes is < 1024 or > 256L * 1024 * 1024) errors.Add(text.Get("Engine.Settings.Invalid", path, "maxRequestBytes"));
        if (settings.MaxLabelBytes is < 1024 or > 64L * 1024 * 1024) errors.Add(text.Get("Engine.Settings.Invalid", path, "maxLabelBytes"));

        if (settings.LabelFolders.Count == 0 && !settings.AllowInlineLabels) warnings.Add(text.Get("Engine.Settings.NoFolders"));
        foreach (var folder in settings.LabelFolders.Where(f => !Directory.Exists(f))) warnings.Add(text.Get("Engine.Settings.FolderMissing", folder));
        if (settings.AllowedPrinters.Count == 0) warnings.Add(text.Get("Engine.Settings.NoPrinters"));
        if (settings.Urls.Any(u => Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme == "http" && !x.IsLoopback))
            warnings.Add(text.Get("Engine.Settings.PlainHttp"));

        settings = settings with { Language = EngineText.Resolve(settings.Language) };
        return errors.Count > 0 ? new(null, errors, warnings, path) : new(settings, [], warnings, path);
    }

    /// <summary>A commented starter file, written by <c>rotula serve --init</c>.</summary>
    public static string Starter(string keyHash, string labelFolder) =>
        "{\n" +
        "  // Where the server listens. 127.0.0.1 accepts calls from this computer only. Use https with a certificate for other computers.\n" +
        "  \"urls\": [\"" + EngineSettings.DefaultUrl + "\"],\n" +
        "  // SHA-256 hashes of the API keys (rotula new-key). Callers send the key itself in the X-Api-Key header.\n" +
        "  \"apiKeyHashes\": [\"" + keyHash + "\"],\n" +
        "  // Labels are named relative to one of these folders.\n" +
        "  \"labelFolders\": [" + JsonSerializer.Serialize(labelFolder) + "],\n" +
        "  // Windows printer names, or host / host:port of network printers. Nothing else can be printed to.\n" +
        "  \"allowedPrinters\": [],\n" +
        "  \"allowInlineLabels\": false,\n" +
        "  \"maxCopies\": 1000,\n" +
        "  \"maxRecords\": 10000,\n" +
        "  \"language\": \"en\"\n" +
        "}\n";
}
