namespace Rotula.Data;

/// <summary>What kind of row the UI shows in its "Where is your data?" list.</summary>
public enum ProviderEntryKind
{
    /// <summary>A provider (engine).</summary>
    Provider,
    /// <summary>A preset over a provider (Supabase, Amazon Redshift, Microsoft Access...).</summary>
    Preset,
}

/// <summary>One row of the data-driven engine list the connection manager shows.</summary>
/// <param name="Kind">Provider or preset.</param>
/// <param name="Id">Provider id or preset id.</param>
/// <param name="ProviderId">The provider that handles it (equals <paramref name="Id"/> for a provider).</param>
/// <param name="DisplayNameKey">Message key of the name.</param>
/// <param name="DisplayName">English fallback name.</param>
/// <param name="Category">Grouping.</param>
/// <param name="PackId">Id of the pack that contributed it (<c>builtin</c> for the five that ship in the box).</param>
/// <param name="IsBuiltIn">True for the five original providers and the presets over them.</param>
/// <param name="DefaultPort">Port suggested to the user, if any.</param>
public sealed record ProviderDisplayEntry(ProviderEntryKind Kind, string Id, string ProviderId, string DisplayNameKey, string DisplayName,
    ProviderCategory Category, string PackId, bool IsBuiltIn, int? DefaultPort);

/// <summary>Raised when a pack cannot be registered (duplicate provider id, invalid id...).</summary>
public sealed class ProviderRegistrationException : Exception
{
    /// <summary>Creates the exception; <paramref name="key"/> is a <c>Data.Pack.*</c> key.</summary>
    public ProviderRegistrationException(string key, string argument, string message) : base(message)
    {
        Key = key;
        Argument = argument;
    }

    /// <summary>Message key (see <see cref="PackKeys"/>).</summary>
    public string Key { get; }

    /// <summary>The offending provider or preset id.</summary>
    public string Argument { get; }
}

/// <summary>
/// The set of providers and presets that are available: the five built-in ones, then whatever provider packs add. The default
/// instance is what the application uses; tests create their own. Registration is atomic and can only ADD: a pack can never
/// replace or shadow an existing provider id (so a pack cannot hijack "sqlserver").
/// </summary>
public sealed class ProviderRegistry
{
    private static readonly Lazy<ProviderRegistry> DefaultInstance = new(() => new ProviderRegistry(includeBuiltIn: true));

    private readonly object _gate = new();
    private readonly Dictionary<string, (IProviderDescriptor Descriptor, string PackId)> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(ProviderPreset Preset, string PackId)> _presets = new();
    private readonly List<string> _packs = new();

    /// <summary>The registry the application uses. Contains the built-in providers; the pack loader adds to it at startup.</summary>
    public static ProviderRegistry Default => DefaultInstance.Value;

    /// <summary>Creates a registry, optionally pre-filled with the five built-in providers and their presets.</summary>
    public ProviderRegistry(bool includeBuiltIn = true)
    {
        if (includeBuiltIn) Register(new BuiltInPack());
    }

    /// <summary>Ids of the registered packs, in registration order.</summary>
    public IReadOnlyList<string> PackIds { get { lock (_gate) return _packs.ToArray(); } }

    /// <summary>All providers in registration order (the five built-in ones first, in their stable order).</summary>
    public IReadOnlyList<IProviderDescriptor> Providers { get { lock (_gate) return _providers.Values.Select(v => v.Descriptor).ToArray(); } }

    /// <summary>All presets in registration order.</summary>
    public IReadOnlyList<ProviderPreset> Presets { get { lock (_gate) return _presets.Select(p => p.Preset).ToArray(); } }

    /// <summary>
    /// Adds a pack's providers and presets, all or nothing.
    /// </summary>
    /// <exception cref="ProviderRegistrationException">An id is malformed or already taken; nothing was added.</exception>
    public void Register(IDatabaseProviderPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        string packId = pack.PackId;
        // Run pack code first, outside the lock and before any change, so a throwing pack leaves the registry untouched.
        var providers = pack.GetProviders();
        var presets = pack.GetPresets();
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(packId) || _packs.Contains(packId, StringComparer.OrdinalIgnoreCase))
                throw new ProviderRegistrationException(PackKeys.DuplicatePack, packId ?? string.Empty, "Pack id is empty or already registered.");
            var newIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in providers)
            {
                if (!IsValidId(d.Id)) throw new ProviderRegistrationException(PackKeys.InvalidProviderId, d.Id ?? string.Empty, "Provider id must be lower-case letters, digits and dashes.");
                if (_providers.ContainsKey(d.Id) || !newIds.Add(d.Id)) throw new ProviderRegistrationException(PackKeys.DuplicateProvider, d.Id, "Provider id is already registered.");
            }
            var presetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pr in presets)
            {
                if (!IsValidId(pr.Id) || _presets.Any(x => x.Preset.Id.Equals(pr.Id, StringComparison.OrdinalIgnoreCase)) || !presetIds.Add(pr.Id))
                    throw new ProviderRegistrationException(PackKeys.DuplicateProvider, pr.Id, "Preset id is invalid or already registered.");
                if (!newIds.Contains(pr.ProviderId) && !_providers.ContainsKey(pr.ProviderId))
                    throw new ProviderRegistrationException(PackKeys.InvalidProviderId, pr.ProviderId, "Preset refers to an unknown provider.");
            }
            foreach (var d in providers) _providers[d.Id] = (d, packId);
            foreach (var pr in presets) _presets.Add((pr, packId));
            _packs.Add(packId);
        }
    }

    /// <summary>True for ids made of lower-case letters, digits and dashes, 1 to 40 characters, not starting with a dash.</summary>
    public static bool IsValidId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 40 && id[0] != '-' && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>Finds a provider by id (case-insensitive); null when unknown (for example its pack is not installed).</summary>
    public IProviderDescriptor? TryGet(string? id)
    {
        if (id is null) return null;
        lock (_gate) return _providers.TryGetValue(id, out var v) ? v.Descriptor : null;
    }

    /// <summary>Finds a provider by id.</summary>
    /// <exception cref="DatabaseException"><see cref="ProviderKeys.ProviderUnavailable"/> when unknown.</exception>
    public IProviderDescriptor Get(string id)
        => TryGet(id) ?? throw new DatabaseException(ProviderKeys.ProviderUnavailable, null, new[] { id });

    /// <summary>The provider of a connection: its <see cref="DatabaseConnectionSettings.GetEffectiveProviderId()"/>.</summary>
    /// <exception cref="DatabaseException"><see cref="ProviderKeys.ProviderUnavailable"/> when the provider is not registered.</exception>
    public IProviderDescriptor Resolve(DatabaseConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Get(settings.GetEffectiveProviderId());
    }

    /// <summary>The dialect the connection really speaks (preset flavour or ODBC engine hint included).</summary>
    public SqlDialect ResolveDialect(DatabaseConnectionSettings settings)
    {
        var d = Resolve(settings);
        return d.ResolveDialect(settings);
    }

    /// <summary>Finds a preset by id (case-insensitive).</summary>
    public ProviderPreset? FindPreset(string? id)
    {
        if (id is null) return null;
        lock (_gate) return _presets.Select(p => p.Preset).FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The data-driven list for the connection manager: every provider followed by its presets, grouped by registration order, so
    /// the five original engines keep their stable ids and positions at the top.
    /// </summary>
    public IReadOnlyList<ProviderDisplayEntry> GetDisplayEntries()
    {
        lock (_gate)
        {
            var list = new List<ProviderDisplayEntry>();
            foreach (var (d, packId) in _providers.Values)
                list.Add(new ProviderDisplayEntry(ProviderEntryKind.Provider, d.Id, d.Id, d.DisplayNameKey, d.DisplayName, d.Category, packId, ProviderIds.IsBuiltIn(d.Id), d.DefaultPort));
            foreach (var (p, packId) in _presets)
            {
                var d = _providers[p.ProviderId].Descriptor;
                list.Add(new ProviderDisplayEntry(ProviderEntryKind.Preset, p.Id, p.ProviderId, p.NameKey, p.Name, p.Category ?? d.Category, packId, packId == "builtin", p.DefaultPort ?? d.DefaultPort));
            }
            return list;
        }
    }
}
