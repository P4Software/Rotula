using System.Net.Sockets;
using System.Text.Json;

namespace Rotula.Data.Integration;

/// <summary>What Start-TestServers.ps1 recorded about one running server (see servers.json in the scratch folder).</summary>
/// <param name="Engine">"postgresql" or "mariadb".</param>
/// <param name="Version">Server version as listed in servers.settings.json.</param>
/// <param name="Host">Always 127.0.0.1.</param>
/// <param name="Port">The random port the server listens on.</param>
/// <param name="AdminUser">Administrator (PostgreSQL superuser / MariaDB root); used by tests to seed data and to check effects.</param>
/// <param name="AdminPassword">Generated throw-away password; lives only in the scratch folder.</param>
/// <param name="ReaderUser">Account the tests create with SELECT-only rights.</param>
/// <param name="ReaderPassword">Generated throw-away password of the reader.</param>
/// <param name="CaCertificate">Path of the self-signed server certificate (PEM), which is also its own authority.</param>
internal sealed record ServerInfo(string Engine, string Version, string Host, int Port, string AdminUser, string AdminPassword,
    string ReaderUser, string ReaderPassword, string CaCertificate);

/// <summary>
/// Finds the servers started by the scripts, without environment variables: the scripts write a small state file to a fixed place
/// under %LOCALAPPDATA%, and the tests also probe the TCP port so a stale file (servers stopped by a reboot) means "skipped", not "failed".
/// </summary>
internal static class ServerCatalog
{
    public const string PostgreSql = "postgresql";
    public const string MariaDb = "mariadb";

    /// <summary>Where Start-TestServers.ps1 writes the state file.</summary>
    public static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rotula", "test-servers", "run", "servers.json");

    private static readonly Lazy<Dictionary<string, (ServerInfo? Info, string? Skip)>> Cache = new(Load);

    /// <summary>The server, or null when it is not available (see <see cref="SkipReason"/>).</summary>
    public static ServerInfo? Get(string engine) => Cache.Value[engine].Info;

    /// <summary>Why tests for <paramref name="engine"/> are skipped, or null when the server is up.</summary>
    public static string? SkipReason(string engine) => Cache.Value.TryGetValue(engine, out var e) ? e.Skip : "Unknown engine '" + engine + "'.";

    private static Dictionary<string, (ServerInfo? Info, string? Skip)> Load()
    {
        var result = new Dictionary<string, (ServerInfo? Info, string? Skip)>();
        foreach (string engine in new[] { PostgreSql, MariaDb })
            result[engine] = Probe(engine);
        return result;
    }

    private static (ServerInfo? Info, string? Skip) Probe(string engine)
    {
        string how = "Run tests\\Rotula.Data.Integration\\servers\\Start-TestServers.cmd to start real test servers (see docs\\testing-databases.md).";
        try
        {
            if (!File.Exists(StatePath)) return (null, "No test servers are running (" + engine + "). " + how);
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            if (!doc.RootElement.TryGetProperty(engine, out var e)) return (null, "The running test servers do not include " + engine + ". " + how);
            string S(string name) => e.GetProperty(name).GetString() ?? "";
            var info = new ServerInfo(engine, S("version"), S("host"), e.GetProperty("port").GetInt32(), S("adminUser"), S("adminPassword"),
                S("readerUser"), S("readerPassword"), S("caCertificate"));
            if (!CanConnect(info.Host, info.Port)) return (null, "The " + engine + " test server is not answering on port " + info.Port + " (stopped?). " + how);
            return (info, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Never let discovery throw: a damaged file just means "no server".
            return (null, "The test server state file could not be read (" + ex.GetType().Name + "). " + how);
        }
    }

    private static bool CanConnect(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch (Exception) { return false; }
    }
}
