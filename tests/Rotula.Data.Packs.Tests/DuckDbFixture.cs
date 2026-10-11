using DuckDB.NET.Data;
using Rotula.Data;
using Rotula.Data.Pack.DuckDb;

namespace Rotula.Data.Packs.Tests;

/// <summary>A temporary folder holding a small real DuckDB database file, removed afterwards.</summary>
public sealed class DuckDbFixture : IDisposable
{
    public DuckDbFixture()
    {
        Folder = Path.Combine(Path.GetTempPath(), "rotula-duck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        DatabasePath = Path.Combine(Folder, "data.duckdb");
        Execute(DatabasePath,
            "CREATE TABLE people(id INTEGER, name VARCHAR, city VARCHAR, price DECIMAL(10,2));" +
            "INSERT INTO people VALUES (1,'Ana','Madrid',12.50),(2,'Niño','Peña',1.5),(3,'Zoë','Zürich',0),(4,'日本語','東京',100),(5,'Ünï 😀','Łódź',NULL);" +
            "CREATE VIEW adults AS SELECT * FROM people WHERE id > 1;" +
            "CREATE TABLE \"we\"\"ird name\"(\"a b\" INTEGER);");
        CsvPath = Path.Combine(Folder, "secret.csv");
        File.WriteAllText(CsvPath, "id,secret\n1,top-secret\n");
        ProviderRegistry = new ProviderRegistry();
        ProviderRegistry.Register(new DuckDbPack());
    }

    /// <summary>The folder with every file of the fixture.</summary>
    public string Folder { get; }

    /// <summary>The database file.</summary>
    public string DatabasePath { get; }

    /// <summary>A CSV outside the database that the engine could read if external access were on.</summary>
    public string CsvPath { get; }

    /// <summary>A registry holding the DuckDB pack loaded in-process.</summary>
    public ProviderRegistry ProviderRegistry { get; }

    /// <summary>Settings for the fixture database.</summary>
    public DatabaseConnectionSettings Settings(string? path = null) => new()
    {
        Name = "duck", Provider = DatabaseProvider.Custom, ProviderId = "duckdb", FilePath = path ?? DatabasePath,
        CommandTimeoutSeconds = 5, ConnectTimeoutSeconds = 5,
    };

    /// <summary>A source over the fixture database.</summary>
    public IDatabaseSource Source(DatabaseConnectionSettings? settings = null)
        => DatabaseSource.CreateUsing(settings ?? Settings(), null, ProviderRegistry);

    /// <summary>Runs a statement with full rights on a database file (test set-up only).</summary>
    public static void Execute(string path, string sql)
    {
        using var c = new DuckDBConnection("DataSource=" + path);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Opens a raw connection with exactly the connection string the pack builds, bypassing the query guard.</summary>
    public DuckDBConnection RawConnection()
    {
        var d = (DuckDbDescriptor)ProviderRegistry.Get("duckdb");
        var c = new DuckDBConnection(d.BuildConnectionString(Settings(), null));
        c.Open();
        return c;
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
