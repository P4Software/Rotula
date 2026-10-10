namespace Rotula.Data;

/// <summary>Entry point: creates an <see cref="IDatabaseSource"/> from settings.</summary>
public static class DatabaseSource
{
    /// <summary>Default cap for <see cref="IDatabaseSource.QueryAsync"/>.</summary>
    public const int DefaultMaxRows = 100_000;

    /// <summary>Largest cap accepted by <see cref="IDatabaseSource.QueryAsync"/>; beyond this use <see cref="IDatabaseSource.OpenStreamAsync"/>.</summary>
    public const int HardMaxRows = 1_000_000;

    /// <summary>
    /// Creates a source. <paramref name="password"/> is held only by the returned object, never exposed again, and never
    /// printed; pass null for Windows-integrated authentication and for providers without one (SQLite).
    /// </summary>
    /// <exception cref="DatabaseException"><see cref="DataKeys.InvalidSettings"/> when a setting is missing or unsafe.</exception>
    public static IDatabaseSource Create(DatabaseConnectionSettings settings, string? password = null)
    {
        ConnectionStrings.Validate(settings);
        return new AdoDatabaseSource(settings, password);
    }
}
