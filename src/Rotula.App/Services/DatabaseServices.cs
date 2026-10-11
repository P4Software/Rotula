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

    /// <summary>
    /// Lists the databases the login in <paramref name="settings"/> may open (for the "Database name" drop-down). Sources are opened
    /// through <see cref="IConnectionStore.OpenWithSettingsAsync(DatabaseConnectionSettings, string?, CancellationToken)"/>, so a saved
    /// password is reused only under the store's own rules. Returns the engine's "not applicable" result for providers without a listing.
    /// </summary>
    /// <param name="settings">The settings as typed in the form.</param>
    /// <param name="passwordArgument">The password argument of <c>OpenWithSettingsAsync</c> (null reuses the saved one, "" means none).</param>
    /// <param name="cancellationToken">Cancellation (the Stop button).</param>
    Task<DatabaseListResult> ListDatabasesAsync(DatabaseConnectionSettings settings, string? passwordArgument, CancellationToken cancellationToken)
        => DatabaseLister.ListDatabasesAsync(settings, (s, ct) => Connections.OpenWithSettingsAsync(s, passwordArgument, ct), cancellationToken);
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
