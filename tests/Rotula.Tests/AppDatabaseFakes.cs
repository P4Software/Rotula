using Rotula.App.Services;
using Rotula.Core.Merge;
using Rotula.Core.Validation;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>
/// An in-memory <see cref="IDatabaseSource"/>: scripted tables and query results, every call recorded, no network and no files.
/// A call waits on <see cref="Gate"/> (when set) so tests can cancel it while it is "in flight".
/// </summary>
internal sealed class FakeDatabaseSource : IDatabaseSource
{
    public FakeDatabaseSource(DatabaseConnectionSettings settings) => Settings = settings;

    public DatabaseConnectionSettings Settings { get; }
    public List<DatabaseObject> Tables = new() { new(null, "Products", DatabaseObjectKind.Table), new(null, "Customers", DatabaseObjectKind.Table), new(null, "ActiveProducts", DatabaseObjectKind.View) };
    public MergeTable Result = new(new[] { "Name", "Qty" }, new[] { new[] { "Tuerca", "5" }, new[] { "Perno", "7" } });
    public bool ReportTruncated;
    public DatabaseException? Failure;
    public TaskCompletionSource? Gate;
    public readonly List<(string Sql, IReadOnlyDictionary<string, object?>? Parameters, int MaxRows)> Queries = new();
    public readonly List<(DatabaseObject Table, int MaxRows)> Previews = new();
    public int Tests, Lists;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        Tests++;
        await WaitAsync(cancellationToken);
        if (Failure is not null) throw Failure;
    }

    public Task<IReadOnlyList<DatabaseObject>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        Lists++;
        if (Failure is not null) throw Failure;
        return Task.FromResult<IReadOnlyList<DatabaseObject>>(Tables);
    }

    public Task<IReadOnlyList<DatabaseColumn>> DescribeAsync(string table, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DatabaseColumn>>(Array.Empty<DatabaseColumn>());

    public Task<IReadOnlyList<DatabaseColumn>> DescribeAsync(DatabaseObject table, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DatabaseColumn>>(Array.Empty<DatabaseColumn>());

    public string BuildSelectAll(DatabaseObject table) => "SELECT * FROM \"" + table.Name + "\"";

    public async Task<DatabaseQueryResult> PreviewAsync(DatabaseObject table, int maxRows = 100, CancellationToken cancellationToken = default)
    {
        Previews.Add((table, maxRows));
        await WaitAsync(cancellationToken);
        if (Failure is not null) throw Failure;
        return Make(maxRows);
    }

    public async Task<DatabaseQueryResult> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, int maxRows = DatabaseSource.DefaultMaxRows,
        CancellationToken cancellationToken = default)
    {
        // The real engine runs the guard again before sending anything; the fake does too, so a test cannot slip a forbidden query through.
        var verdict = SqlGuard.Check(sql, Settings.Provider);
        if (!verdict.IsAllowed) throw verdict.ToException();
        Queries.Add((sql, parameters, maxRows));
        await WaitAsync(cancellationToken);
        if (Failure is not null) throw Failure;
        return Make(maxRows);
    }

    public Task<DatabaseRowStream> OpenStreamAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    private DatabaseQueryResult Make(int maxRows)
    {
        var table = new MergeTable(Result.Columns, Result.Rows);
        if (ReportTruncated)
            table.AddIssue(new ValidationIssue(ValidationSeverity.Warning, DataKeys.QueryTruncated, null, maxRows.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new DatabaseQueryResult(table, ReportTruncated, maxRows);
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        if (Gate is null) return;
        try { await Gate.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { throw new DatabaseException(DataKeys.Cancelled); }
    }
}

/// <summary>An in-memory <see cref="IConnectionStore"/> that records every use, with the real password semantics (null keeps, empty removes).</summary>
internal sealed class FakeConnectionStore : IConnectionStore
{
    public readonly Dictionary<string, (DatabaseConnectionSettings Settings, string? Password)> Entries = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, FakeDatabaseSource> Sources = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Opened = new();
    public readonly List<(string Name, string? Password)> Saves = new();
    public string? ErrorKey;
    public int Loads;

    public Task<ConnectionStoreContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        Loads++;
        var list = Entries.Values.Select(e => new SavedConnection(e.Settings, !string.IsNullOrEmpty(e.Password))).ToList();
        return Task.FromResult(new ConnectionStoreContent(list, ErrorKey, 0));
    }

    public Task SaveAsync(DatabaseConnectionSettings settings, string? password, CancellationToken cancellationToken = default)
    {
        Saves.Add((settings.Name, password));
        string? keep = password is null && Entries.TryGetValue(settings.Name, out var old) ? old.Password : password;
        Entries[settings.Name] = (settings, string.IsNullOrEmpty(keep) ? null : keep);
        if (!Sources.ContainsKey(settings.Name)) Sources[settings.Name] = new FakeDatabaseSource(settings);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Sources.Remove(name);
        return Task.FromResult(Entries.Remove(name));
    }

    public Task<IDatabaseSource> OpenAsync(string name, CancellationToken cancellationToken = default)
    {
        Opened.Add(name);
        if (!Entries.TryGetValue(name, out var entry)) throw new DatabaseException(DataKeys.ConnectionNotFound, null, new[] { name });
        if (!Sources.TryGetValue(name, out var source)) Sources[name] = source = new FakeDatabaseSource(entry.Settings);
        return Task.FromResult<IDatabaseSource>(source);
    }

    /// <summary>Every call of <see cref="OpenWithSettingsAsync(DatabaseConnectionSettings, string?, CancellationToken)"/>: the settings and the password argument (null = reuse the saved one).</summary>
    public readonly List<(DatabaseConnectionSettings Settings, string? PasswordArgument)> OpenedWithSettings = new();
    /// <summary>Makes the source for settings tested with an explicit password (set by <see cref="FakeDatabaseServices"/> so existing tests can see them in <c>Created</c>).</summary>
    public Func<DatabaseConnectionSettings, string?, IDatabaseSource>? CreateForExplicitPassword;

    public Task<IDatabaseSource> OpenWithSettingsAsync(DatabaseConnectionSettings edited, string? newPassword, CancellationToken cancellationToken = default)
    {
        OpenedWithSettings.Add((edited, newPassword));
        if (newPassword is null)
        {
            // Same rules as the real store: the saved password only follows an edit that keeps where and how the connection signs in.
            if (!Entries.TryGetValue(edited.Name, out var saved)) throw new DatabaseException(DataKeys.ConnectionNotFound, null, new[] { edited.Name });
            if (!SavedPasswordPolicy.CanReuse(saved.Settings, edited)) throw new DatabaseException(ProviderKeys.PasswordReenterRequired, null, new[] { edited.Name });
            return OpenAsync(edited.Name, cancellationToken);
        }
        return Task.FromResult(CreateForExplicitPassword?.Invoke(edited, newPassword) ?? new FakeDatabaseSource(edited));
    }

    /// <summary>Adds a saved connection and its scripted source.</summary>
    public FakeDatabaseSource Add(DatabaseConnectionSettings settings, string? password = null)
    {
        Entries[settings.Name] = (settings, password);
        var source = new FakeDatabaseSource(settings);
        Sources[settings.Name] = source;
        return source;
    }
}

/// <summary>The database services of the App tests: the fake store plus a recording factory for unsaved settings.</summary>
internal sealed class FakeDatabaseServices : IDatabaseServices
{
    public FakeConnectionStore Store { get; } = new();
    public readonly List<(DatabaseConnectionSettings Settings, string? Password)> Created = new();
    public FakeDatabaseSource CreatedSource = new(new DatabaseConnectionSettings { Name = "created", Provider = DatabaseProvider.Sqlite, FilePath = "x.db" });

    public FakeDatabaseServices() => Store.CreateForExplicitPassword = Create;

    // ---- the database list of the connection manager
    /// <summary>Every list request: the settings as typed and the password argument handed to the store (null = reuse the saved one).</summary>
    public readonly List<(DatabaseConnectionSettings Settings, string? PasswordArgument)> ListCalls = new();
    /// <summary>What the "server" answers.</summary>
    public DatabaseListResult ListAnswer = FakeLists.Of("Sales", "Stock", "master");
    /// <summary>When set, the request fails with it.</summary>
    public DatabaseException? ListFailure;
    /// <summary>When set, the request waits for it (so a test can change the form or press Stop while it is in flight).</summary>
    public TaskCompletionSource? ListGate;

    /// <inheritdoc />
    public async Task<DatabaseListResult> ListDatabasesAsync(DatabaseConnectionSettings settings, string? passwordArgument, CancellationToken cancellationToken)
    {
        ListCalls.Add((settings, passwordArgument));
        // The real default opens the source through the store; doing the same keeps the saved-password policy in the test.
        await Store.OpenWithSettingsAsync(settings with { Database = "master" }, passwordArgument, cancellationToken);
        if (ListGate is not null)
        {
            try { await ListGate.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { throw new DatabaseException(DataKeys.Cancelled); }
        }
        if (ListFailure is not null) throw ListFailure;
        return ListAnswer;
    }

    public IConnectionStore Connections => Store;

    public IDatabaseSource Create(DatabaseConnectionSettings settings, string? password)
    {
        Created.Add((settings, password));
        return CreatedSource;
    }
}

/// <summary>Builds <see cref="DatabaseListResult"/> values for tests (the constructor is internal to the engine).</summary>
internal static class FakeLists
{
    private static readonly string[] System = { "master", "model", "msdb", "tempdb" };

    public static DatabaseListResult Of(params string[] names)
        => new(names.Select(n => new DatabaseListEntry(n, System.Contains(n, StringComparer.OrdinalIgnoreCase))).ToList(), false, true);

    public static DatabaseListResult Truncated(params string[] names)
        => new(names.Select(n => new DatabaseListEntry(n, false)).ToList(), true, true);
}
