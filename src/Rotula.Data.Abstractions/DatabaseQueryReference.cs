namespace Rotula.Data;

/// <summary>
/// What a .rotula file may store about a database data source: the NAME of a saved connection and the query text. It has
/// no member that can hold host, user, password or connection string, so none can leak into a document.
/// </summary>
/// <param name="ConnectionName">Name of a connection in the connection store.</param>
/// <param name="Query">The SELECT text (or empty when a whole table is used).</param>
public sealed record DatabaseQueryReference(string ConnectionName, string Query);

/// <summary>Size limits shared by the file format and the database engine (single source of truth).</summary>
public static class DatabaseLimits
{
    /// <summary>Default cap for the number of rows a query returns.</summary>
    public const int DefaultMaxRows = 100_000;

    /// <summary>Largest row cap accepted; beyond this the streaming API must be used.</summary>
    public const int HardMaxRows = 1_000_000;

    /// <summary>Longest accepted query text, in characters.</summary>
    public const int MaxQueryLength = 100_000;
}
