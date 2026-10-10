using System.Text.Json;

namespace Rotula.Data.Packs;

/// <summary>The version of the Rotula.Data provider API that packs are built against.</summary>
public static class DataApi
{
    /// <summary>
    /// Current provider API version. The major number changes when a pack compiled against an older major can no longer work; the
    /// minor number grows when members are added. A pack states the lowest version it needs (<see cref="PackManifest.MinDataApiVersion"/>).
    /// </summary>
    public static readonly Version Current = new(1, 0);
}

/// <summary>Where a pack folder was found.</summary>
public enum PackOrigin
{
    /// <summary>The <c>packs</c> folder next to the program: installed with Rotula, trusted.</summary>
    Bundled,
    /// <summary><c>%AppData%\Rotula\packs</c>: put there by the user, loaded only after an explicit approval.</summary>
    User,
}

/// <summary>The content of a pack's <c>pack.json</c>.</summary>
public sealed record PackManifest
{
    /// <summary>Largest manifest file that will be read.</summary>
    public const int MaxBytes = 65_536;

    /// <summary>Pack id: lower-case letters, digits and dashes. Equals the folder purpose and <see cref="IDatabaseProviderPack.PackId"/>.</summary>
    public required string Id { get; init; }
    /// <summary>Human-readable name (English; the UI may translate through <c>Data.Pack.Name.{Id}</c>).</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>The pack's own version, for display.</summary>
    public string Version { get; init; } = "0.0";
    /// <summary>Who made the pack (shown in the approval prompt).</summary>
    public string? Publisher { get; init; }
    /// <summary>Ids of every provider the pack registers; the loader checks the pack registers exactly these.</summary>
    public IReadOnlyList<string> ProviderIds { get; init; } = Array.Empty<string>();
    /// <summary>Lowest <see cref="DataApi.Current"/> the pack works with, for example <c>1.0</c>.</summary>
    public Version MinDataApiVersion { get; init; } = new(1, 0);
    /// <summary>File name (no folder) of the assembly with the pack's entry class.</summary>
    public required string EntryAssembly { get; init; }
    /// <summary>Full name of the entry class, or null to use the single <see cref="IDatabaseProviderPack"/> implementation found.</summary>
    public string? EntryType { get; init; }
    /// <summary>File name (no folder) of the pack's licence and third-party notices, shown in the pack list.</summary>
    public string? LicenseNoticesFile { get; init; }

    /// <summary>Reads and validates a manifest. Returns null and an English reason (for developers) when it is unusable.</summary>
    public static PackManifest? TryParse(string json, out string? error)
    {
        error = null;
        try
        {
            if (json.Length > MaxBytes) { error = "pack.json is too large."; return null; }
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) { error = "pack.json must be an object."; return null; }
            string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            string? id = Str("id");
            if (!ProviderRegistry.IsValidId(id)) { error = "'id' must be lower-case letters, digits and dashes."; return null; }
            string? entry = Str("entryAssembly");
            if (!IsPlainFileName(entry) || !entry!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { error = "'entryAssembly' must be a .dll file name without a folder."; return null; }
            string? notices = Str("licenseNoticesFile");
            if (notices is not null && !IsPlainFileName(notices)) { error = "'licenseNoticesFile' must be a file name without a folder."; return null; }

            var ids = new List<string>();
            if (r.TryGetProperty("providerIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var x in arr.EnumerateArray())
                {
                    string? pid = x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                    if (!ProviderRegistry.IsValidId(pid)) { error = "'providerIds' holds an invalid id."; return null; }
                    ids.Add(pid!);
                }
            if (ids.Count == 0) { error = "'providerIds' must list at least one provider."; return null; }
            if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count) { error = "'providerIds' has a duplicate."; return null; }

            Version min = new(1, 0);
            if (Str("minDataApiVersion") is { } mv && !System.Version.TryParse(mv, out min!)) { error = "'minDataApiVersion' is not a version like 1.0."; return null; }

            return new PackManifest
            {
                Id = id!, Name = Str("name") ?? id!, Version = Str("version") ?? "0.0", Publisher = Str("publisher"),
                ProviderIds = ids, MinDataApiVersion = min, EntryAssembly = entry!, EntryType = Str("entryType"), LicenseNoticesFile = notices,
            };
        }
        catch (JsonException ex)
        {
            error = "pack.json is not valid JSON: " + ex.Message;
            return null;
        }
    }

    private static bool IsPlainFileName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");
}
