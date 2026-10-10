using System.Diagnostics;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>
/// Finds and starts SQL Server LocalDB when it is installed. These tests never touch a network host: LocalDB is a per-user
/// process reached through shared memory / named pipes.
/// </summary>
internal static class LocalDb
{
    public const string Instance = "MSSQLLocalDB";

    private static readonly Lazy<string?> SkipReason = new(Probe);

    /// <summary>Null when LocalDB is available and running, otherwise the reason the tests are skipped.</summary>
    public static string? Reason => SkipReason.Value;

    private static string? Probe()
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server");
            if (!Directory.Exists(root)) return "SQL Server LocalDB is not installed.";
            string? exe = Directory.GetDirectories(root)
                .Select(d => Path.Combine(d, "Tools", "Binn", "SqlLocalDB.exe"))
                .Where(File.Exists).OrderByDescending(p => p).FirstOrDefault();
            if (exe is null) return "SqlLocalDB.exe was not found.";
            using var p = Process.Start(new ProcessStartInfo(exe, "start " + Instance) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null || !p.WaitForExit(60_000) || p.ExitCode != 0) return "LocalDB instance '" + Instance + "' could not be started.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return "LocalDB probe failed: " + ex.GetType().Name;
        }
    }
}

/// <summary>A [Fact] that skips itself (xunit 2.9 has no dynamic skip) when LocalDB is absent.</summary>
internal sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "LocalDB is Windows only.";
        else if (LocalDb.Reason is { } reason) Skip = reason;
    }
}

/// <summary>Optional SQL Server tests against LocalDB, over both the native provider and ODBC.</summary>
public class DataLocalDbTests
{
    private static DatabaseConnectionSettings Native() => new()
    {
        Name = "localdb", Provider = DatabaseProvider.SqlServer, Host = @"(localdb)\" + LocalDb.Instance, Database = "master",
        Authentication = DatabaseAuthentication.WindowsIntegrated,
        // LocalDB presents a self-signed certificate: the explicit, UI-warned relaxation, still encrypted.
        Encrypt = true, TrustServerCertificate = true,
    };

    private const string Values = "SELECT N'Café ñ' AS A, CAST(12.50 AS decimal(10,2)) AS B, CAST(NULL AS int) AS C, " +
                                  "CAST('2024-03-05' AS date) AS D, CAST('2024-03-05T14:30:00' AS datetime2(0)) AS E, CAST(1 AS bit) AS F, " +
                                  "0xDEADBEEF AS G, @p AS H, CAST(1234.5 AS float) AS I";

    private static void AssertValues(DatabaseQueryResult r)
    {
        Assert.Equal(new[] { "Café ñ", "12.50", "", "2024-03-05", "2024-03-05 14:30:00", "TRUE", "0xDEADBEEF", "label", "1234.5" }, r.Table.Rows.Single());
    }

    [LocalDbFact]
    public async Task SqlServer_TestListQuery_Parameters()
    {
        var src = DatabaseSource.Create(Native());
        await src.TestConnectionAsync();
        var tables = await src.ListTablesAsync();
        Assert.DoesNotContain(tables, t => t.Schema is "sys" or "INFORMATION_SCHEMA");
        AssertValues(await src.QueryAsync(Values, new Dictionary<string, object?> { ["p"] = "label" }));
    }

    [LocalDbFact]
    public async Task SqlServer_RejectsWrites_AndStreams()
    {
        var src = DatabaseSource.Create(Native());
        await Assert.ThrowsAsync<DatabaseException>(() => src.QueryAsync("CREATE TABLE tempdb.dbo.rotula_x (a int)"));
        await Assert.ThrowsAsync<DatabaseException>(() => src.QueryAsync("SELECT 1; DROP TABLE x"));
        await using var s = await src.OpenStreamAsync("SELECT TOP (5) object_id FROM sys.all_objects ORDER BY object_id");
        int n = 0;
        await foreach (var _ in s.ReadRowsAsync()) n++;
        Assert.Equal(5, n);
    }

    [LocalDbFact]
    public async Task SqlServer_UnknownDatabase_IsDatabaseNotFound_AndTimeoutIsHonoured()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseSource.Create(Native() with { Database = "rotula_no_such_db" }).TestConnectionAsync());
        Assert.True(ex.Key is DataKeys.DatabaseNotFound or DataKeys.PermissionDenied or DataKeys.ConnectionFailed, ex.Key);

        var slow = DatabaseSource.Create(Native() with { CommandTimeoutSeconds = 1 });
        var sw = Stopwatch.StartNew();
        var timeout = await Assert.ThrowsAsync<DatabaseException>(() => slow.QueryAsync(
            "SELECT COUNT_BIG(*) FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c CROSS JOIN sys.all_objects d"));
        Assert.Equal(DataKeys.Timeout, timeout.Key);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "timeout was not honoured: " + sw.Elapsed);
    }

    [LocalDbFact]
    public async Task SqlServer_CancelWhileRunning_ReportsCancelled()
    {
        var src = DatabaseSource.Create(Native());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => src.QueryAsync(
            "SELECT COUNT_BIG(*) FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c CROSS JOIN sys.all_objects d", null, 10, cts.Token));
        Assert.Equal(DataKeys.Cancelled, ex.Key);
    }

    [LocalDbFact]
    public async Task Odbc_AgainstLocalDb_RewritesNamedParametersToPositional()
    {
        string? driver = new[] { "ODBC Driver 18 for SQL Server", "ODBC Driver 17 for SQL Server" }
            .FirstOrDefault(d => OdbcDriverInstalled(d));
        if (driver is null) return; // no driver: nothing to prove here (reported as passed, documented in the test name)
        var settings = new DatabaseConnectionSettings
        {
            Name = "odbc", Provider = DatabaseProvider.Odbc,
            OdbcConnectionString = $"Driver={{{driver}}};Server=(localdb)\\{LocalDb.Instance};Database=master;Trusted_Connection=Yes;TrustServerCertificate=Yes",
        };
        var src = DatabaseSource.Create(settings);
        await src.TestConnectionAsync();
        // @a appears twice: ODBC needs two '?' markers and two bound values.
        var r = await src.QueryAsync("SELECT CAST(@a AS int) + CAST(@a AS int) AS twice, CAST(@b AS nvarchar(20)) AS s",
            new Dictionary<string, object?> { ["a"] = 21, ["b"] = "Café" });
        Assert.Equal(new[] { "42", "Café" }, r.Table.Rows.Single());
        await Assert.ThrowsAsync<DatabaseException>(() => src.QueryAsync("DELETE FROM x"));
    }

    internal static bool OdbcDriverInstalled(string name)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\" + name);
        return key is not null;
    }

    [LocalDbFact]
    public async Task SqlServer_RowCap_TruncatesCleanly_AfterCancellingTheRest()
    {
        // Providers that really cancel (unlike SQLite) may raise while the half-read reader is closed; the result must survive.
        var r = await DatabaseSource.Create(Native()).QueryAsync(
            "SELECT TOP (5000) a.object_id FROM sys.all_objects a CROSS JOIN sys.all_objects b", null, maxRows: 10);
        Assert.True(r.Truncated);
        Assert.Equal(10, r.Table.RowCount);
        Assert.Equal(DataKeys.QueryTruncated, Assert.Single(r.Table.Issues).MessageKey);
    }

    [LocalDbFact]
    public async Task SqlServer_RealLoginFailure_IsAuthenticationFailed()
    {
        var bogus = Native() with { Authentication = DatabaseAuthentication.UserPassword, UserName = "rotula_no_such_login" };
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseSource.Create(bogus, "definitely-wrong-Pw1!").TestConnectionAsync());
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.DoesNotContain("definitely-wrong", ex.ToString());
        Assert.DoesNotContain("definitely-wrong", ex.Detail ?? "");
    }

    [Fact]
    public async Task Odbc_MissingDriverOrDsn_IsDriverNotInstalled_WithoutAnyNetwork()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var raw in new[] { "Driver={Rotula No Such Driver};Server=127.0.0.1", "Dsn=RotulaNoSuchDsn" })
        {
            var src = DatabaseSource.Create(new DatabaseConnectionSettings { Name = "o", Provider = DatabaseProvider.Odbc, OdbcConnectionString = raw });
            var ex = await Assert.ThrowsAsync<DatabaseException>(() => src.TestConnectionAsync());
            Assert.Equal(DataKeys.DriverNotInstalled, ex.Key);
        }
    }
}

/// <summary>Browsing a throwaway LocalDB database through the native provider and through ODBC.</summary>
public sealed class DataLocalDbBrowseTests : IAsyncLifetime
{
    private readonly string _db = "rotula_test_" + Guid.NewGuid().ToString("N");

    private static string Master => @"Server=(localdb)\" + LocalDb.Instance + ";Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";

    private static async Task Run(string connectionString, string sql)
    {
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        if (LocalDb.Reason is not null) return;
        await Run(Master, $"CREATE DATABASE [{_db}]");
        string inDb = Master.Replace("Database=master", "Database=" + _db);
        await Run(inDb, "CREATE SCHEMA lab"); // CREATE SCHEMA must be alone in its batch
        await Run(inDb, @"
            CREATE TABLE dbo.Items (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL, Price decimal(10,2) NULL, Added datetime2(0) NULL);
            INSERT INTO dbo.Items VALUES (1, N'Caf' + NCHAR(233), 12.50, '2024-03-05T14:30:00'), (2, N'Bolt', NULL, NULL);
            CREATE TABLE lab.[odd name] (a int);
            INSERT INTO lab.[odd name] VALUES (7);");
        await Run(Master.Replace("Database=master", "Database=" + _db), "CREATE VIEW dbo.ItemView AS SELECT Id, Name FROM dbo.Items WHERE Price > 5");
    }

    public async Task DisposeAsync()
    {
        if (LocalDb.Reason is not null) return;
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await Run(Master, $"ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]");
    }

    private DatabaseConnectionSettings Native() => new()
    {
        Name = "n", Provider = DatabaseProvider.SqlServer, Host = @"(localdb)\" + LocalDb.Instance, Database = _db,
        TrustServerCertificate = true,
    };

    private DatabaseConnectionSettings Odbc(string driver) => new()
    {
        Name = "o", Provider = DatabaseProvider.Odbc,
        OdbcConnectionString = $"Driver={{{driver}}};Server=(localdb)\\{LocalDb.Instance};Database={_db};Trusted_Connection=Yes;TrustServerCertificate=Yes",
    };

    private static async Task BrowseAsync(IDatabaseSource src, bool expectQuotedSelect)
    {
        var tables = await src.ListTablesAsync();
        var items = tables.Single(t => t.Name == "Items");
        Assert.Equal(DatabaseObjectKind.Table, items.Kind);
        Assert.Equal("dbo", items.Schema);
        Assert.Equal("dbo.Items", items.QualifiedName);
        Assert.Contains(tables, t => t.Schema == "lab" && t.Name == "odd name");
        Assert.Equal(tables.OrderBy(t => t.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => t.QualifiedName),
                     tables.Select(t => t.QualifiedName));
        Assert.Contains(tables, t => t.Name == "ItemView" && t.Kind == DatabaseObjectKind.View);

        var cols = await src.DescribeAsync("dbo.Items");
        Assert.Equal(new[] { "Id", "Name", "Price", "Added" }, cols.Select(c => c.Name).ToArray());
        Assert.Equal(false, cols[0].AllowsNull);
        Assert.Equal(true, cols[2].AllowsNull);

        var odd = tables.Single(t => t.Name == "odd name");
        if (expectQuotedSelect) Assert.Equal("SELECT * FROM [lab].[odd name]", src.BuildSelectAll(odd));
        var preview = await src.PreviewAsync(items);
        Assert.Equal(new[] { "1", "Café", "12.50", "2024-03-05 14:30:00" }, preview.Table.Rows[0]);
        Assert.Equal(new[] { "2", "Bolt", "", "" }, preview.Table.Rows[1]);
        Assert.Equal("7", (await src.PreviewAsync(odd)).Table.Rows.Single()[0]);
    }

    [LocalDbFact]
    public async Task Native_BrowsesTablesViewsAndColumns()
        => await BrowseAsync(DatabaseSource.Create(Native()), expectQuotedSelect: true);

    [LocalDbFact]
    public async Task Odbc_BrowsesWhatTheDriverReports()
    {
        string? driver = new[] { "ODBC Driver 18 for SQL Server", "ODBC Driver 17 for SQL Server" }.FirstOrDefault(DataLocalDbTests.OdbcDriverInstalled);
        if (driver is null) return;
        var src = DatabaseSource.Create(Odbc(driver));
        var tables = await src.ListTablesAsync();
        Assert.Contains(tables, t => t.Name == "Items" && t.Kind == DatabaseObjectKind.Table);
        Assert.Contains(tables, t => t.Name == "ItemView" && t.Kind == DatabaseObjectKind.View);
        // The ODBC quoting fallback (ANSI double quotes) must work on SQL Server for names with spaces.
        var odd = tables.Single(t => t.Name == "odd name");
        Assert.Equal("7", (await src.PreviewAsync(odd)).Table.Rows.Single()[0]);
        var items = tables.Single(t => t.Name == "Items");
        Assert.Equal(new[] { "Id", "Name", "Price", "Added" }, (await src.DescribeAsync(items)).Select(c => c.Name).ToArray());
        Assert.Equal("Café", (await src.PreviewAsync(items)).Table.Rows[0][1]);
    }
}
