namespace Rotula.Data;

/// <summary>Entry point: creates an <see cref="IDatabaseSource"/> from settings.</summary>
public static class DatabaseSource
{
    /// <summary>Default cap for <see cref="IDatabaseSource.QueryAsync"/>.</summary>
    public const int DefaultMaxRows = DatabaseLimits.DefaultMaxRows;

    /// <summary>Largest cap accepted by <see cref="IDatabaseSource.QueryAsync"/>; beyond this use <see cref="IDatabaseSource.OpenStreamAsync"/>.</summary>
    public const int HardMaxRows = DatabaseLimits.HardMaxRows;

    /// <summary>
    /// Creates a source. <paramref name="password"/> is held only by the returned object, never exposed again, and never
    /// printed; pass null for Windows-integrated authentication and for providers without one (SQLite).
    /// </summary>
    /// <exception cref="DatabaseException"><see cref="DataKeys.InvalidSettings"/> when a setting is missing or unsafe.</exception>
    public static IDatabaseSource Create(DatabaseConnectionSettings settings, string? password = null)
        => CreateUsing(settings, password, ProviderRegistry.Default);

    /// <summary>Same as <see cref="Create(DatabaseConnectionSettings, string?)"/> with an explicit registry (tests, hosts with their own packs).</summary>
    /// <exception cref="DatabaseException"><see cref="ProviderKeys.ProviderUnavailable"/> when the provider (a pack) is not loaded.</exception>
    public static IDatabaseSource CreateUsing(DatabaseConnectionSettings settings, string? password, ProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ConnectionStrings.Validate(settings, registry, allowUnknownProvider: false);
        var descriptor = registry.Resolve(settings);
        // Engines outside ADO.NET (document stores, web services) bring their own source; everything else gets the shared
        // implementation, which applies the query guard and the read-only transaction.
        return descriptor.CreateCustomSource(settings, password) ?? new AdoDatabaseSource(settings, password, registry);
    }
}
