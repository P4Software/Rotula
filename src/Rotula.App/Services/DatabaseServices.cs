using Rotula.Data;

namespace Rotula.App.Services;

/// <summary>
/// The database facilities a view-model needs: the saved connections and a way to make a source from settings that are
/// being edited and are not saved yet (the "Test connection" button). Behind an interface so view-models run with fakes.
/// </summary>
public interface IDatabaseServices
{
    /// <summary>The saved connections (settings plus DPAPI-protected passwords).</summary>
    IConnectionStore Connections { get; }

    /// <summary>Creates a source from settings and an optional plain password (null for Windows-integrated sign-in and SQLite).</summary>
    IDatabaseSource Create(DatabaseConnectionSettings settings, string? password);
}

/// <summary>The real implementation: <c>%AppData%\Rotula\connections.json</c> and <see cref="DatabaseSource.Create"/>.</summary>
public sealed class DatabaseServices : IDatabaseServices
{
    /// <summary>Creates the services; tests pass their own store.</summary>
    public DatabaseServices(IConnectionStore? store = null) => Connections = store ?? new JsonConnectionStore();

    /// <inheritdoc />
    public IConnectionStore Connections { get; }

    /// <inheritdoc />
    public IDatabaseSource Create(DatabaseConnectionSettings settings, string? password) => DatabaseSource.Create(settings, password);
}
