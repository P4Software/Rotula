using System.Data.Common;

namespace Rotula.Data;

/// <summary>
/// Validates structured settings and turns them into provider connection strings exclusively through each provider's
/// <c>ConnectionStringBuilder</c>, so a value can never add or override a keyword. The provider-specific parts live in the
/// <see cref="IProviderDescriptor"/> of the connection's provider; this class runs the checks every provider shares.
/// </summary>
internal static class ConnectionStrings
{
    /// <summary>Application name sent to servers so a DBA can recognise Rotula sessions.</summary>
    public const string ApplicationName = "Rotula";

    /// <summary>Checks every field; throws <see cref="DatabaseException"/> (<see cref="DataKeys.InvalidSettings"/>, arg = field) on the first problem.</summary>
    public static void Validate(DatabaseConnectionSettings s) => Validate(s, null, allowUnknownProvider: false);

    /// <summary>
    /// Checks every field against the registry. With <paramref name="allowUnknownProvider"/> a pack provider that is not loaded is
    /// accepted after the provider-independent checks (used when reading the connections file, so entries of a missing pack survive).
    /// </summary>
    public static void Validate(DatabaseConnectionSettings s, ProviderRegistry? registry, bool allowUnknownProvider)
    {
        ArgumentNullException.ThrowIfNull(s);
        registry ??= ProviderRegistry.Default;
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 100 || HasControl(s.Name)) throw Invalid(nameof(s.Name));
        if (!Enum.IsDefined(s.Provider)) throw Invalid(nameof(s.Provider));
        if (!Enum.IsDefined(s.Authentication)) throw Invalid(nameof(s.Authentication));
        if (s.CommandTimeoutSeconds is < 1 or > 3600) throw Invalid(nameof(s.CommandTimeoutSeconds));
        if (s.ConnectTimeoutSeconds is < 1 or > 300) throw Invalid(nameof(s.ConnectTimeoutSeconds));
        if (s.Port is < 1 or > 65535) throw Invalid(nameof(s.Port));

        // Provider identity: the enum for the built-in five, the string id for everything else; the two must agree.
        if (s.ProviderId is not null && !ProviderRegistry.IsValidId(s.ProviderId)) throw Invalid(nameof(s.ProviderId));
        if (s.Provider == DatabaseProvider.Custom && s.ProviderId is null) throw Invalid(nameof(s.ProviderId));
        if (s.ProviderId is not null && ProviderIds.ToEnum(s.ProviderId) != s.Provider) throw Invalid(nameof(s.ProviderId));
        if (s.PresetId is not null && !ProviderRegistry.IsValidId(s.PresetId)) throw Invalid(nameof(s.PresetId));
        if (s.CaCertificatePath is not null)
        {
            ProviderValidation.RequireLocalPath(s.CaCertificatePath, nameof(s.CaCertificatePath));
        }
        ValidateExtrasShape(s.Extras);

        var descriptor = registry.TryGet(s.GetEffectiveProviderId());
        if (descriptor is null)
        {
            if (allowUnknownProvider && s.Provider == DatabaseProvider.Custom) return;
            throw new DatabaseException(ProviderKeys.ProviderUnavailable, null, new[] { s.GetEffectiveProviderId() });
        }

        if (!string.IsNullOrWhiteSpace(s.CaCertificatePath) && !descriptor.Options.Any(o => o.Key == ProviderOptionKeys.CaCertificatePath))
            throw Invalid(nameof(s.CaCertificatePath));
        ValidateExtras(s.Extras, descriptor);
        descriptor.Validate(s);
    }

    /// <summary>Builds the (secret-bearing) connection string. Never log the result; use <see cref="SecretRedactor"/>.</summary>
    public static string Build(DatabaseConnectionSettings s, string? password) => Build(s, password, null);

    /// <summary>Builds the connection string through the descriptor found in <paramref name="registry"/>.</summary>
    public static string Build(DatabaseConnectionSettings s, string? password, ProviderRegistry? registry)
    {
        registry ??= ProviderRegistry.Default;
        Validate(s, registry, allowUnknownProvider: false);
        return registry.Resolve(s).BuildConnectionString(s, password);
    }

    /// <summary>A connection string safe to show: every password value masked.</summary>
    public static string BuildRedacted(DatabaseConnectionSettings s, string? password = null)
        => SecretRedactor.RedactConnectionString(Build(s, password ?? "x"));

    private static void ValidateExtrasShape(ProviderExtras extras)
    {
        if (extras.Count > ProviderExtras.MaxEntries) throw Invalid(nameof(DatabaseConnectionSettings.Extras));
        foreach (var kv in extras)
        {
            if (kv.Key.Length is 0 or > 64 || kv.Value.Length > ProviderExtras.MaxLength || HasControl(kv.Key) || HasControl(kv.Value))
                throw Invalid(nameof(DatabaseConnectionSettings.Extras));
        }
    }

    /// <summary>Extras must be options the provider declares, with values that fit the declared kind (a hand-edited file cannot add unknown switches).</summary>
    private static void ValidateExtras(ProviderExtras extras, IProviderDescriptor d)
    {
        foreach (var kv in extras)
        {
            var option = d.Options.FirstOrDefault(o => o.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && !ProviderOptionKeys.IsStandard(o.Key));
            if (option is null) throw Invalid("Extras." + kv.Key);
            bool ok = option.Kind switch
            {
                ProviderOptionKind.Bool => kv.Value is "true" or "false",
                ProviderOptionKind.Number => int.TryParse(kv.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
                    && n >= (option.Minimum ?? 0) && n <= (option.Maximum ?? int.MaxValue),
                ProviderOptionKind.Choice => option.Choices.Any(c => c.Value.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)),
                ProviderOptionKind.FilePath => kv.Value.Length <= 1024,
                _ => kv.Value.Length <= 512,
            };
            if (!ok) throw Invalid("Extras." + kv.Key);
        }
    }

    private static bool HasControl(string v) => v.Any(char.IsControl);

    private static DatabaseException Invalid(string field) => new(DataKeys.InvalidSettings, null, new[] { field });

    /// <summary>Parses a built connection string back and returns its keys; used by tests to prove no keyword was injected.</summary>
    internal static IReadOnlyDictionary<string, string> Parse(string connectionString)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in b.Keys) d[k] = Convert.ToString(b[k], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return d;
    }
}
