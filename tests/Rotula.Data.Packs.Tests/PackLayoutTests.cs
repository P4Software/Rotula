using System.Diagnostics;
using System.Runtime.Loader;
using Rotula.Data;
using Rotula.Data.Packs;

namespace Rotula.Data.Packs.Tests;

/// <summary>
/// The published layout of every pack (what scripts/build-packs.ps1 produces) loaded through the real <see cref="PackLoader"/> from a
/// temporary <c>packs</c> folder. The folders under <c>testpacks</c> are published by the test project's build exactly like the script does.
/// </summary>
public sealed class PackLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rotula-packs-" + Guid.NewGuid().ToString("N"));

    public PackLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Native libraries stay mapped until the process ends; a leftover temp folder is harmless.
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static IEnumerable<object[]> Packs => new[]
    {
        new object[] { "duckdb", "duckdb" },
        new object[] { "firebird", "firebird" },
        new object[] { "clickhouse", "clickhouse" },
        new object[] { "oracle", "oracle" },
    };

    private static string Published(string id) => Path.Combine(AppContext.BaseDirectory, "testpacks", id);

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private PackLoadOptions Options() => new()
    {
        Locations = new PackLocations(Path.Combine(_root, "packs"), null),
        TrustStore = new PackTrustStore(Path.Combine(_root, "trusted.json")),
        Registry = new ProviderRegistry(),
        IncludeUserPacks = false,
    };

    private string Place(string id)
    {
        string dest = Path.Combine(_root, "packs", id);
        Copy(Published(id), dest);
        return dest;
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void ThePublishedFolder_HoldsOnlyThePacksOwnFiles_AndItsLicenceNotices(string id, string providerId)
    {
        string folder = Published(id);
        Assert.True(File.Exists(Path.Combine(folder, "pack.json")), folder);
        var manifest = PackManifest.TryParse(File.ReadAllText(Path.Combine(folder, "pack.json")), out string? error);
        Assert.True(manifest is not null, error);
        Assert.Equal(id, manifest!.Id);
        Assert.Equal(new[] { providerId }, manifest.ProviderIds);
        Assert.True(File.Exists(Path.Combine(folder, manifest.EntryAssembly)));
        Assert.True(File.Exists(Path.Combine(folder, "README.md")));

        // Licence notices: present, and not the one-line placeholder.
        string notices = File.ReadAllText(Path.Combine(folder, manifest.LicenseNoticesFile!));
        Assert.True(notices.Length > 1000, "NOTICES too short: " + notices.Length);

        // Nothing of the host may be copied into the pack (the shared contract is loaded from Rotula), and nothing of the other
        // database stacks either: their licence texts are not in this pack's notices.
        string[] names = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Select(n => n!).ToArray();
        foreach (string forbidden in new[] { "Rotula.Data.dll", "Rotula.Data.Abstractions.dll", "Rotula.Core.dll", "Microsoft.Data.SqlClient.dll",
                     "Microsoft.Data.SqlClient.SNI.dll", "Npgsql.dll", "MySqlConnector.dll", "Microsoft.Data.Sqlite.dll", "e_sqlite3.dll", "System.Data.Odbc.dll" })
            Assert.DoesNotContain(forbidden, names);

        // Every third-party assembly or native library in the folder is named in the notices file.
        foreach (string dll in names.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Rotula.", StringComparison.Ordinal)))
        {
            string stem = Path.GetFileNameWithoutExtension(dll);
            Assert.True(notices.Contains(stem, StringComparison.OrdinalIgnoreCase) || NoticeCovers(notices, stem), $"{dll} is not covered by NOTICES.txt");
        }
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void EveryPackageInTheDepsFile_IsNamedWithItsExactVersion_InTheNotices(string id, string providerId)
    {
        _ = providerId;
        string folder = Published(id);
        string deps = Directory.GetFiles(folder, "*.deps.json").Single();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(deps));
        string notices = File.ReadAllText(Path.Combine(folder, "NOTICES.txt"));
        var packages = doc.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(l => l.Value.GetProperty("type").GetString() == "package")
            .Select(l => l.Name.Replace('/', ' '))
            .ToList();
        Assert.NotEmpty(packages);
        // A package version bump without regenerating NOTICES (scripts\make-pack-notices.ps1) fails here.
        foreach (string package in packages)
            Assert.True(notices.Contains(package, StringComparison.Ordinal), $"NOTICES.txt of the {id} pack does not list \"{package}\". Run scripts\\make-pack-notices.ps1.");
    }

    /// <summary>A package family is listed once for all its assemblies (System.* under one heading, DuckDB.NET.* ...).</summary>
    private static bool NoticeCovers(string notices, string assemblyName)
    {
        int cut = assemblyName.IndexOf('.');
        while (cut > 0)
        {
            if (notices.Contains(assemblyName[..cut] + ".*", StringComparison.OrdinalIgnoreCase)) return true;
            cut = assemblyName.IndexOf('.', cut + 1);
        }
        return notices.Contains(assemblyName.Split('.')[0] + ".*", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void ThePublishedPack_LoadsThroughPackLoader_AndRegistersExactlyItsProvider(string id, string providerId)
    {
        string folder = Place(id);
        var options = Options();
        var report = PackLoader.LoadAll(options);

        Assert.Empty(report.Warnings);
        var loaded = Assert.Single(report.Loaded);
        Assert.Equal(id, loaded.Candidate.Manifest!.Id);
        Assert.Equal(PackOrigin.Bundled, loaded.Candidate.Origin);
        Assert.NotNull(loaded.LicenseNoticesPath);
        var d = options.Registry.Get(providerId);
        Assert.Equal(id, options.Registry.GetDisplayEntries().Single(e => e.Id == providerId).PackId);
        Assert.StartsWith("Conn.Provider.", d.DisplayNameKey);
        Assert.NotEmpty(d.Options);

        // The pack runs in its own load context, with the contract shared with Rotula.
        var packAssembly = loaded.Pack.GetType().Assembly;
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(packAssembly));
        Assert.Equal(folder, Path.GetDirectoryName(packAssembly.Location));
        Assert.DoesNotContain(loaded.Context.Assemblies, a => a.GetName().Name is "Rotula.Data" or "Rotula.Core" or "Rotula.Data.Abstractions");

        // Every provider answers its own questions from inside the pack: the form, the dialect and the connection string builder.
        Assert.Same(d.Dialect, options.Registry.ResolveDialect(new DatabaseConnectionSettings { Name = "x", Provider = DatabaseProvider.Custom, ProviderId = providerId }));
    }

    [Fact]
    public async Task ThePublishedDuckDbPack_RunsARealQuery_ThroughTheLoader_WithItsOwnNativeLibrary()
    {
        string folder = Place("duckdb");
        var options = Options();
        var report = PackLoader.LoadAll(options);
        Assert.Empty(report.Warnings);
        var loaded = Assert.Single(report.Loaded);

        string dbFolder = Path.Combine(_root, "db");
        Directory.CreateDirectory(dbFolder);
        string db = Path.Combine(dbFolder, "t.duckdb");
        DuckDbFixture.Execute(db, "CREATE TABLE t(id INTEGER, name VARCHAR); INSERT INTO t VALUES (1,'Niño'),(2,'日本語');");

        var settings = new DatabaseConnectionSettings { Name = "duck", Provider = DatabaseProvider.Custom, ProviderId = "duckdb", FilePath = db };
        var source = DatabaseSource.CreateUsing(settings, null, options.Registry);
        var result = await source.QueryAsync("SELECT id, name FROM t WHERE id >= @min ORDER BY id", new Dictionary<string, object?> { ["min"] = 1 });
        Assert.Equal(2, result.Table.RowCount);
        Assert.Equal("Niño", result.Table.Rows[0][1]);
        Assert.Equal("日本語", result.Table.Rows[1][1]);
        var objects = await source.ListTablesAsync();
        Assert.Contains(objects, o => o.Name == "t");
        await Assert.ThrowsAsync<DatabaseException>(() => source.QueryAsync("DROP TABLE t"));

        // The managed driver of the pack lives in the pack's context, and the native library that answered came from the pack folder.
        Assert.Contains(loaded.Context.Assemblies, a => a.GetName().Name == "DuckDB.NET.Data");
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.FileName);
        Assert.Contains(modules, m => m.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(m).Equals("duckdb.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void TheManifest_DeclaresTheApiVersionTheCodeNeeds(string id, string providerId)
    {
        _ = providerId;
        var manifest = PackManifest.TryParse(File.ReadAllText(Path.Combine(Published(id), "pack.json")), out _)!;
        // A pack must never claim an older API than it uses; the host is never older than the minimum.
        Assert.True(manifest.MinDataApiVersion <= DataApi.Current);
        Assert.Equal(1, manifest.MinDataApiVersion.Major);
    }
}
