using System.Net.Sockets;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;
using Rotula.Data;
using Rotula.Data.Pack.Firebird;
using Xunit.Sdk;

namespace Rotula.Data.Packs.Tests;

/// <summary>
/// Where a running Firebird test server is. The tests never start one and never look at environment variables: a small state file
/// (<c>%LOCALAPPDATA%\Rotula\pack-tests\servers.json</c>, or the <c>firebird</c> entry of the shared <c>servers.json</c> written by
/// tests\Rotula.Data.Integration\servers) names it, and the tests also probe the port, so a stale file means "skipped", never "failed".
/// </summary>
public sealed record FirebirdServerInfo(string Host, int Port, string AdminUser, string AdminPassword, string Database)
{
    private static readonly Lazy<FirebirdServerInfo?> Cache = new(Load);

    /// <summary>The server, or null when none is running.</summary>
    public static FirebirdServerInfo? Current => Cache.Value;

    /// <summary>Plain-language reason why the Firebird tests are skipped.</summary>
    public const string SkipReason = "No Firebird test server is running on this computer (see the README of the Firebird pack, section Tests).";

    private static FirebirdServerInfo? Load()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rotula");
        // The shared state file of tests\Rotula.Data.Integration\servers is looked at too, in case it gains a "firebird" entry.
        foreach (string path in new[] { Path.Combine(root, "pack-tests", "servers.json"), Path.Combine(root, "test-servers", "run", "servers.json") })
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("firebird", out var e)) continue;
                string S(string n) => e.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
                var info = new FirebirdServerInfo(S("host"), e.GetProperty("port").GetInt32(), S("adminUser"), S("adminPassword"), S("database"));
                if (info.Database.Length > 0 && CanConnect(info.Host, info.Port)) return info;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return null;
    }

    private static bool CanConnect(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is SocketException or AggregateException) { return false; }
    }

    /// <summary>Settings that sign in as the administrator.</summary>
    public DatabaseConnectionSettings AdminSettings() => new()
    {
        Name = "fb-admin", Provider = DatabaseProvider.Custom, ProviderId = "firebird", Host = Host, Port = Port, Database = Database,
        Authentication = DatabaseAuthentication.UserPassword, UserName = AdminUser, CommandTimeoutSeconds = 10, ConnectTimeoutSeconds = 10,
    };

    /// <summary>Runs statements with full rights (set-up only).</summary>
    public void Execute(params string[] statements)
    {
        using var c = new FbConnection(new FbConnectionStringBuilder
        {
            DataSource = Host, Port = Port, Database = Database, UserID = AdminUser, Password = AdminPassword, Charset = "UTF8", Pooling = false,
        }.ConnectionString);
        c.Open();
        foreach (string sql in statements)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Same, but ignores a statement that fails (cleanup of objects that may not exist).</summary>
    public void TryExecute(params string[] statements)
    {
        foreach (string s in statements)
        {
            try { Execute(s); }
            catch (FbException) { }
        }
    }
}

/// <summary>A fact that is skipped, with a plain reason, when no Firebird test server is reachable (decided when tests are discovered).</summary>
public sealed class FirebirdFactAttribute : FactAttribute
{
    public FirebirdFactAttribute()
    {
        if (FirebirdServerInfo.Current is null) Skip = FirebirdServerInfo.SkipReason;
    }
}

/// <summary>A theory that is skipped when no Firebird test server is reachable.</summary>
public sealed class FirebirdTheoryAttribute : TheoryAttribute
{
    public FirebirdTheoryAttribute()
    {
        if (FirebirdServerInfo.Current is null) Skip = FirebirdServerInfo.SkipReason;
    }
}
