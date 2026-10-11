using System.Data.Common;
using Rotula.Core.Merge;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>A provider as a pack would write it: an ordinary descriptor that also says how its databases are listed.</summary>
internal sealed class FakeListingDescriptor : IProviderDescriptor, IProviderErrorMapper, IDatabaseListingProvider
{
    public string Id => "listing-db";
    public string DisplayNameKey => "Pack.Listing.Name";
    public string DisplayName => "Listing DB";
    public ProviderCategory Category => ProviderCategory.Server;
    public int? DefaultPort => 7001;
    public IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = new[] { DatabaseAuthentication.UserPassword };
    public IReadOnlyList<ProviderOption> Options { get; } = new[] { StandardOptions.Host(), StandardOptions.Port(), StandardOptions.Database(), StandardOptions.UserName() };
    public bool RequiresServer => true;
    public SqlDialect Dialect => SqlDialects.PostgreSql;
    public ReadOnlyPolicy ReadOnly { get; } = new() { GuardIsPrimaryControl = true, Description = "test" };
    public IProviderErrorMapper Errors => this;
    public void Validate(DatabaseConnectionSettings settings) { }
    public string BuildConnectionString(DatabaseConnectionSettings settings, string? password) => "x";
    public DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings) => throw new NotSupportedException();
    public (int? Number, string? SqlState) Extract(Exception ex) => (null, null);
    public string? ClassifyNumber(int? number, bool connecting) => null;

    public DatabaseListingQuery? GetDatabaseListingQuery(DatabaseConnectionSettings settings)
        => new("SELECT name FROM catalogue.databases ORDER BY 1", "admin", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "admin" });
}

internal sealed class FakeListingPack : IDatabaseProviderPack
{
    public string PackId => "listing";
    public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[] { new FakeListingDescriptor() };
}

/// <summary>The engine side of the database drop-down: queries, rules, caps and fallbacks, without any server.</summary>
public class DataDatabaseListingTests
{
    private static DatabaseConnectionSettings Settings(DatabaseProvider provider, string? database = null) => new()
    {
        Name = "n", Provider = provider, Host = "h", Database = database, Authentication = DatabaseAuthentication.UserPassword, UserName = "u",
    };

    private static FakeDatabaseSource SourceReturning(DatabaseProvider provider, IEnumerable<string> names, bool truncated = false)
        => new(Settings(provider)) { Result = new MergeTable(new[] { "name" }, names.Select(n => new[] { n }).ToList()), ReportTruncated = truncated };

    // ------------------------------------------------------------------------------------ the catalogue queries

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    [InlineData(DatabaseProvider.MySql)]
    public void TheCatalogueQuery_IsOneParameterlessSelect_ThatPassesTheReadOnlyGuard(DatabaseProvider provider)
    {
        var descriptor = ProviderRegistry.Default.Get(ProviderIds.For(provider));
        var query = DatabaseLister.GetQuery(descriptor, Settings(provider));
        Assert.NotNull(query);
        var verdict = SqlGuard.Check(query!.Sql, descriptor.Dialect);
        Assert.True(verdict.IsAllowed, query.Sql);
        Assert.Empty(verdict.ParameterNames);                          // nothing the user typed is ever put into this text
        Assert.DoesNotContain('@', query.Sql);
        Assert.DoesNotContain(';', query.Sql);
    }

    [Fact]
    public void TheQueryText_IsTheDocumentedOne()
    {
        Assert.Equal("SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 AND state = 0 ORDER BY name", DatabaseLister.SqlServerSql);
        Assert.Equal("SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY 1", DatabaseLister.PostgreSqlSql);
        Assert.Equal("SELECT schema_name FROM information_schema.schemata ORDER BY 1", DatabaseLister.MySqlSql);
        // SHOW DATABASES is not a SELECT, so the read-only guard would refuse it; this is why MySQL uses the catalogue view instead.
        Assert.False(SqlGuard.Check("SHOW DATABASES", SqlDialects.MySql).IsAllowed);
    }

    [Fact]
    public void SqliteAndOdbc_AreNotApplicable_WithoutOpeningAnything()
    {
        foreach (var provider in new[] { DatabaseProvider.Sqlite, DatabaseProvider.Odbc })
        {
            var descriptor = ProviderRegistry.Default.Get(ProviderIds.For(provider));
            Assert.Null(DatabaseLister.GetQuery(descriptor, Settings(provider)));
            Assert.Empty(DatabaseLister.GetListingSettings(Settings(provider)));
        }
        Assert.False(DatabaseLister.IsSupported(Settings(DatabaseProvider.Sqlite)));
        Assert.True(DatabaseLister.IsSupported(Settings(DatabaseProvider.SqlServer)));
    }

    [Fact]
    public async Task Sqlite_ReturnsTheNotApplicableResult_NotAnError_AndNeverTouchesTheFile()
    {
        var settings = new DatabaseConnectionSettings { Name = "s", Provider = DatabaseProvider.Sqlite, FilePath = Path.Combine(Path.GetTempPath(), "rotula-no-such-file.db") };
        var r = await DatabaseLister.ListDatabasesAsync(settings, password: null);
        Assert.False(r.Supported);
        Assert.Empty(r.Databases);
        Assert.False(File.Exists(settings.FilePath));

        // A real SQLite source gives the same answer when it is handed over.
        string path = Path.Combine(Path.GetTempPath(), "rotula-list-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (a int)";
                await cmd.ExecuteNonQueryAsync();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var source = DatabaseSource.Create(settings with { FilePath = path });
            Assert.False((await DatabaseLister.ListDatabasesAsync(source)).Supported);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------------------------ which database to connect to

    [Fact]
    public void TheListingConnectsToTheMaintenanceDatabase_ThenToTheTypedOne()
    {
        var sql = DatabaseLister.GetListingSettings(Settings(DatabaseProvider.SqlServer, "Sales"));
        Assert.Equal(new[] { "master", "Sales" }, sql.Select(s => s.Database).ToArray());
        Assert.Equal(new[] { "master" }, DatabaseLister.GetListingSettings(Settings(DatabaseProvider.SqlServer, "MASTER")).Select(s => s.Database).ToArray());
        Assert.Equal(new[] { "master" }, DatabaseLister.GetListingSettings(Settings(DatabaseProvider.SqlServer)).Select(s => s.Database).ToArray());
        Assert.Equal(new[] { "postgres", "labels" }, DatabaseLister.GetListingSettings(Settings(DatabaseProvider.PostgreSql, "labels")).Select(s => s.Database).ToArray());
        // MySQL needs no database to list schemas, so a wrong typed name cannot break the listing.
        Assert.Equal(new string?[] { null, "shop" }, DatabaseLister.GetListingSettings(Settings(DatabaseProvider.MySql, "shop")).Select(s => s.Database).ToArray());
        Assert.All(sql, s => Assert.Equal("h", s.Host));
    }

    [Fact]
    public async Task OnlyAMissingOrForbiddenDatabase_MovesOnToTheNextCandidate()
    {
        var opened = new List<string?>();
        Func<DatabaseConnectionSettings, CancellationToken, Task<IDatabaseSource>> failFirst = (s, _) =>
        {
            opened.Add(s.Database);
            if (s.Database == "postgres") throw new DatabaseException(DataKeys.PermissionDenied);
            return Task.FromResult<IDatabaseSource>(SourceReturning(DatabaseProvider.PostgreSql, new[] { "labels" }));
        };
        var r = await DatabaseLister.ListDatabasesAsync(Settings(DatabaseProvider.PostgreSql, "labels"), failFirst);
        Assert.Equal(new[] { "postgres", "labels" }, opened.ToArray());
        Assert.Equal("labels", Assert.Single(r.Databases).Name);

        opened.Clear();
        Func<DatabaseConnectionSettings, CancellationToken, Task<IDatabaseSource>> wrongLogin = (s, _) =>
        {
            opened.Add(s.Database);
            throw new DatabaseException(DataKeys.AuthenticationFailed);
        };
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseLister.ListDatabasesAsync(Settings(DatabaseProvider.PostgreSql, "labels"), wrongLogin));
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.Single(opened);                                         // a wrong password would fail the same way again: not retried

        // When every candidate is refused the last refusal is what the user hears about.
        Func<DatabaseConnectionSettings, CancellationToken, Task<IDatabaseSource>> allRefuse = (_, _) => throw new DatabaseException(DataKeys.DatabaseNotFound);
        var last = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseLister.ListDatabasesAsync(Settings(DatabaseProvider.SqlServer, "Sales"), allRefuse));
        Assert.Equal(DataKeys.DatabaseNotFound, last.Key);
    }

    // ------------------------------------------------------------------------------------ the result

    [Fact]
    public async Task Names_AreSortedWithoutRegardToCase_DeduplicatedAndSystemOnesFlagged()
    {
        var source = SourceReturning(DatabaseProvider.SqlServer, new[] { "tempdb", "zeta", "Alpha", "master", "alpha", "  ", "bad\u0007name", "model", "msdb", "Beta" });
        var r = await DatabaseLister.ListDatabasesAsync(source);
        Assert.True(r.Supported);
        Assert.Equal(new[] { "Alpha", "Beta", "master", "model", "msdb", "tempdb", "zeta" }, r.Databases.Select(d => d.Name).ToArray());
        Assert.Equal(new[] { "master", "model", "msdb", "tempdb" }, r.Databases.Where(d => d.IsSystem).Select(d => d.Name).ToArray());
        Assert.False(r.Truncated);
        var query = Assert.Single(source.Queries);
        Assert.Equal(DatabaseLister.SqlServerSql, query.Sql);
        Assert.Null(query.Parameters);
        Assert.Equal(DatabaseLister.MaxNames + 1, query.MaxRows);      // one row past the cap proves the list was cut
    }

    [Fact]
    public async Task MySqlSystemSchemas_AreFlagged()
    {
        var r = await DatabaseLister.ListDatabasesAsync(SourceReturning(DatabaseProvider.MySql, new[] { "information_schema", "shop", "mysql", "performance_schema", "sys" }));
        Assert.Equal(new[] { "shop" }, r.Databases.Where(d => !d.IsSystem).Select(d => d.Name).ToArray());
        Assert.Equal(4, r.Databases.Count(d => d.IsSystem));
    }

    [Fact]
    public async Task MoreThanTheCap_IsCutAndReported()
    {
        var many = Enumerable.Range(0, DatabaseLister.MaxNames + 3).Select(i => "db" + i.ToString("D5"));
        var r = await DatabaseLister.ListDatabasesAsync(SourceReturning(DatabaseProvider.PostgreSql, many));
        Assert.Equal(DatabaseLister.MaxNames, r.Databases.Count);
        Assert.True(r.Truncated);

        var cut = await DatabaseLister.ListDatabasesAsync(SourceReturning(DatabaseProvider.PostgreSql, new[] { "a", "b" }, truncated: true));
        Assert.True(cut.Truncated);
        Assert.Equal(2, cut.Databases.Count);
    }

    [Fact]
    public async Task ACancelledCall_ReportsTheStableCancelledKey()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseLister.ListDatabasesAsync(Settings(DatabaseProvider.SqlServer), "pw", cts.Token));
        Assert.Equal(DataKeys.Cancelled, ex.Key);
    }

    [Fact]
    public async Task AFailingQuery_KeepsItsStableErrorKey()
    {
        var source = SourceReturning(DatabaseProvider.SqlServer, Array.Empty<string>());
        source.Failure = new DatabaseException(DataKeys.PermissionDenied);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseLister.ListDatabasesAsync(source));
        Assert.Equal(DataKeys.PermissionDenied, ex.Key);
    }

    // ------------------------------------------------------------------------------------ packs and the option kind

    [Fact]
    public void TheFormDecidesFromTheDescriptor_WhichFieldsGetAList()
    {
        var reg = ProviderRegistry.Default;
        foreach (string id in new[] { ProviderIds.SqlServer, ProviderIds.PostgreSql, ProviderIds.MySql })
        {
            var d = reg.Get(id);
            Assert.Equal(OptionLookupKind.DatabaseList, OptionLookups.For(d, d.Options.Single(o => o.Key == ProviderOptionKeys.Database)));
            Assert.Equal(OptionLookupKind.None, OptionLookups.For(d, d.Options.Single(o => o.Key == ProviderOptionKeys.Host)));
        }
        var sqlite = reg.Get(ProviderIds.Sqlite);
        Assert.All(sqlite.Options, o => Assert.Equal(OptionLookupKind.None, OptionLookups.For(sqlite, o)));
        var odbc = reg.Get(ProviderIds.Odbc);
        Assert.Equal(OptionLookupKind.OdbcDataSource, OptionLookups.For(odbc, odbc.Options.Single(o => o.Key == ProviderOptionKeys.Dsn)));
        Assert.Equal(OptionLookupKind.None, OptionLookups.For(odbc, odbc.Options.Single(o => o.Key == ProviderOptionKeys.OdbcConnectionString)));
    }

    [Fact]
    public async Task APackThatOffersAListing_GetsTheSameDropDown_AndTheSameGuardedPath()
    {
        var registry = new ProviderRegistry();
        registry.Register(new FakeListingPack());
        var d = registry.Get("listing-db");
        Assert.Equal(OptionLookupKind.DatabaseList, OptionLookups.For(d, d.Options.Single(o => o.Key == ProviderOptionKeys.Database)));

        var settings = new DatabaseConnectionSettings { Name = "p", Provider = DatabaseProvider.Custom, ProviderId = "listing-db", Host = "h", Database = "mine", UserName = "u", Authentication = DatabaseAuthentication.UserPassword };
        Assert.Equal(new[] { "admin", "mine" }, DatabaseLister.GetListingSettings(settings, registry).Select(s => s.Database).ToArray());

        var source = new FakeDatabaseSource(settings) { Result = new MergeTable(new[] { "name" }, new List<string[]> { new[] { "mine" }, new[] { "admin" } }) };
        var r = await DatabaseLister.ListDatabasesAsync(source, default, registry);
        Assert.Equal(new[] { "admin", "mine" }, r.Databases.Select(x => x.Name).ToArray());
        Assert.True(r.Databases[0].IsSystem);
        Assert.Equal("SELECT name FROM catalogue.databases ORDER BY 1", Assert.Single(source.Queries).Sql);
    }

    [Fact]
    public void AProviderWithoutAListing_KeepsATextBox()
    {
        var d = ProviderRegistry.Default.Get(ProviderIds.Sqlite);
        Assert.False(d is IDatabaseListingProvider);
        Assert.Same(DatabaseListResult.NotSupported, DatabaseListResult.NotSupported);
        Assert.False(DatabaseListResult.NotSupported.Supported);
    }

    [Fact]
    public void TheOdbcDataSourceCatalogue_ReadsTheRegistryWithoutFailing()
    {
        var found = OdbcDataSourceCatalogue.GetDataSources();
        Assert.All(found, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
        Assert.Equal(found.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), found.Count);
        Assert.DoesNotContain(found, d => d.Name.Contains(';') || d.Name.Contains('='));
    }
}

/// <summary>The listing against a real SQL Server (LocalDB, Windows sign-in): system databases, HAS_DBACCESS, state and the error mapping.</summary>
public sealed class DataDatabaseListingLocalDbTests : IAsyncLifetime
{
    // A private LocalDB instance: the shared one may hold dozens of auto-closing databases, which makes HAS_DBACCESS take minutes.
    private const string Inst = "RotulaListTest";

    private static string? Exe() => Directory.GetDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server"))
        .Select(d => Path.Combine(d, "Tools", "Binn", "SqlLocalDB.exe")).Where(File.Exists).OrderByDescending(p => p).FirstOrDefault();

    private static void LocalDbCommand(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Exe()!, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit(60_000);
    }

    private readonly string _online = "rotula_list_on_" + Guid.NewGuid().ToString("N");
    private readonly string _offline = "rotula_list_off_" + Guid.NewGuid().ToString("N");
    private readonly string _login = "rotula_lister_" + Guid.NewGuid().ToString("N")[..12];
    private const string LoginPassword = "R0tula!Lister-pw-9";
    private bool _loginCreated;

    private static string Master => @"Server=(localdb)\" + Inst + ";Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";

    private static async Task Run(string sql)
    {
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(Master);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        if (LocalDb.Reason is not null) return;
        LocalDbCommand("create " + Inst);
        LocalDbCommand("start " + Inst);
        await Run($"CREATE DATABASE [{_online}]");
        await Run($"CREATE DATABASE [{_offline}]");
        await Run($"ALTER DATABASE [{_offline}] SET OFFLINE WITH ROLLBACK IMMEDIATE");
        try
        {
            await Run($"CREATE LOGIN [{_login}] WITH PASSWORD = '{LoginPassword}', CHECK_POLICY = OFF");
            _loginCreated = true;
        }
        catch (Microsoft.Data.SqlClient.SqlException) { /* creating a login needs rights; the limited-login test then skips itself */ }
    }

    public async Task DisposeAsync()
    {
        if (LocalDb.Reason is not null) return;
        LocalDbCommand("stop " + Inst + " -k");
        LocalDbCommand("delete " + Inst);
    }

    private static DatabaseConnectionSettings Windows(string? database = null) => new()
    {
        Name = "localdb", Provider = DatabaseProvider.SqlServer, Host = @"(localdb)\" + Inst, Database = database,
        Authentication = DatabaseAuthentication.WindowsIntegrated, TrustServerCertificate = true,
    };

    [LocalDbFact]
    public async Task ListsTheOnlineDatabases_SortedWithTheSystemOnesFlagged_AndSkipsTheOfflineOne()
    {
        var r = await DatabaseLister.ListDatabasesAsync(Windows(), password: null);
        Assert.True(r.Supported);
        var names = r.Databases.Select(d => d.Name).ToArray();
        Assert.Contains(_online, names);
        Assert.DoesNotContain(_offline, names);                        // state <> 0: picking it would fail
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(), names);
        foreach (string system in new[] { "master", "model", "msdb", "tempdb" })
            Assert.True(r.Databases.Single(d => d.Name == system).IsSystem, system);
        Assert.False(r.Databases.Single(d => d.Name == _online).IsSystem);
    }

    [LocalDbFact]
    public async Task AWrongDatabaseTypedInTheForm_DoesNotBreakTheListing()
    {
        // The listing connects to master first, so a typo in the box does not matter.
        var r = await DatabaseLister.ListDatabasesAsync(Windows("rotula_no_such_database"), password: null);
        Assert.Contains(r.Databases, d => d.Name == "master");
    }

    [LocalDbFact]
    public async Task ALoginThatCannotOpenADatabase_DoesNotSeeIt()
    {
        if (!_loginCreated) return;
        var settings = Windows() with { Authentication = DatabaseAuthentication.UserPassword, UserName = _login };
        DatabaseListResult r;
        try { r = await DatabaseLister.ListDatabasesAsync(settings, LoginPassword); }
        catch (DatabaseException ex) when (ex.Key == DataKeys.AuthenticationFailed)
        {
            return; // this LocalDB instance accepts Windows sign-in only, so there is no SQL login to try
        }
        Assert.Contains(r.Databases, d => d.Name == "master");
        Assert.DoesNotContain(r.Databases, d => d.Name == _online);    // HAS_DBACCESS = 0: the login has no user there
    }

    [LocalDbFact]
    public async Task AWrongLogin_IsAuthenticationFailed_AndNeverEchoesThePassword()
    {
        var settings = Windows() with { Authentication = DatabaseAuthentication.UserPassword, UserName = "rotula_no_such_login" };
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseLister.ListDatabasesAsync(settings, "definitely-wrong-Pw1!"));
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.DoesNotContain("definitely-wrong", ex.ToString());
        Assert.DoesNotContain("definitely-wrong", ex.Detail ?? string.Empty);
    }

    [LocalDbFact]
    public async Task CancellingWhileConnecting_ReportsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var task = DatabaseLister.ListDatabasesAsync(Windows(), (string?)null, cts.Token);
        cts.Cancel();
        try { await task; }
        catch (DatabaseException ex) { Assert.Equal(DataKeys.Cancelled, ex.Key); }   // a fast machine may finish first, which is fine
    }
}
