using Rotula.App.Localization;
using Rotula.Data;

namespace Rotula.App.ViewModels;

/// <summary>
/// Turns the stable keys of Rotula.Data into plain-language sentences in the current language. The library carries no UI text;
/// every <c>Data.Error.*</c>, <c>Data.Query.Rejected.*</c> and <c>Data.Store.*</c> key has an entry in both resource files
/// (a test scans the library for the keys and fails when one is missing).
/// </summary>
public static class DataMessages
{
    /// <summary>Describes a failure: what happened and what to do next. Never contains a password or connection string.</summary>
    public static string Describe(DatabaseException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        string first = error.Args.Count > 0 ? error.Args[0] : string.Empty;
        switch (error.Key)
        {
            case DataKeys.QueryRejected:
                return Loc.Get(DataKeys.QueryRejected, DescribeReason(error.ReasonKey, first));
            case DataKeys.InvalidSettings:
                // The argument is the settings property; show the label the user sees in the form.
                string field = Loc.Instance.HasKey("Conn.Field." + first) ? Loc.Get("Conn.Field." + first) : first;
                return Loc.Get(DataKeys.InvalidSettings, field);
            case DataKeys.TooManyRows:
                return Loc.Get(DataKeys.TooManyRows, error.Args.Count > 0 ? error.Args[0] : string.Empty, error.Args.Count > 1 ? error.Args[1] : string.Empty);
            case DataKeys.ConnectionNotFound:
                return Loc.Get(DataKeys.ConnectionNotFound, first);
        }
        return Loc.Instance.HasKey(error.Key) ? Loc.Get(error.Key, first) : Loc.Get("Data.Error.Unknown", error.Key);
    }

    /// <summary>Describes why the read-only guard refused a query (one sentence). <paramref name="arg"/> is the offending word, if any.</summary>
    public static string DescribeReason(string? reasonKey, string? arg)
    {
        if (reasonKey is not null && Loc.Instance.HasKey(reasonKey)) return Loc.Get(reasonKey, arg ?? string.Empty);
        return Loc.Get(DataKeys.RejectNotSelect);
    }

    /// <summary>Describes the problem with the connections file (<see cref="DataKeys.StoreDamaged"/> or <see cref="DataKeys.StoreTooLarge"/>).</summary>
    public static string DescribeStore(string key) => Loc.Instance.HasKey(key) ? Loc.Get(key) : Loc.Get("Data.Error.Unknown", key);

    /// <summary>Name of a provider for lists.</summary>
    public static string ProviderName(DatabaseProvider provider) => Loc.Get("Conn.Provider." + provider);

    /// <summary>
    /// Name of the engine a saved connection uses: its preset ("Supabase (direct connection)") when it has one, else its provider, else
    /// the provider id (an add-on that is not installed). Texts come from the language files, with the registry's English name as fallback.
    /// </summary>
    public static string ProviderName(DatabaseConnectionSettings settings, ProviderRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        registry ??= ProviderRegistry.Default;
        string id = settings.GetEffectiveProviderId();
        var preset = registry.FindPreset(settings.PresetId);
        if (preset is not null && preset.ProviderId.Equals(id, StringComparison.OrdinalIgnoreCase))
            return Loc.Instance.HasKey(preset.NameKey) ? Loc.Get(preset.NameKey) : preset.Name;
        var descriptor = registry.TryGet(id);
        if (descriptor is null) return id;
        return Loc.Instance.HasKey(descriptor.DisplayNameKey) ? Loc.Get(descriptor.DisplayNameKey) : descriptor.DisplayName;
    }
}
