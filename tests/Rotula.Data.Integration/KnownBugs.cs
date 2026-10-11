namespace Rotula.Data.Integration;

/// <summary>
/// Tests that FAIL today because of a real problem in src/Rotula.Data (or the way it uses a driver). They are tagged
/// <c>Category=KnownBug</c> so a build can run everything else; when the product is fixed the entry is deleted and the test
/// becomes an ordinary regression test. The list is empty at the moment: the twelve problems found by the first run against real
/// PostgreSQL 18.6 and MariaDB 11.8.9 servers (command and connect timeouts, certificate rejection, wide DECIMAL, awkward PostgreSQL values,
/// Describe nullability, system-versioned tables, side-effect functions, CHARACTER SET in a cast, rejected logins reported as unreachable
/// servers, silent wrong output) are fixed and their tests are ordinary regression tests now. A new entry goes here as
/// <c>{ (ServerCatalog.PostgreSql, "Test_method_name"), "B13" }</c>.
/// </summary>
internal static class KnownBugs
{
    private static readonly Dictionary<(string Engine, string Method), string> Entries = new();

    /// <summary>The bug id for this engine and test method, or null when the test is expected to pass.</summary>
    public static string? Find(string engine, string method) => Entries.TryGetValue((engine, method), out var id) ? id : null;
}
