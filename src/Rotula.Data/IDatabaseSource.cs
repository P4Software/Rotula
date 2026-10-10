using Rotula.Core.Merge;

namespace Rotula.Data;

/// <summary>A table or view that can be browsed.</summary>
/// <param name="Schema">Schema (SQL Server, PostgreSQL), database (MySQL), or null (SQLite).</param>
/// <param name="Name">Object name without quoting.</param>
/// <param name="Kind">Table or view.</param>
public sealed record DatabaseObject(string? Schema, string Name, DatabaseObjectKind Kind)
{
    /// <summary>Display name, <c>schema.name</c> or just <c>name</c>. Not quoted: never paste it into SQL, use <see cref="IDatabaseSource.BuildSelectAll"/>.</summary>
    public string QualifiedName => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
}

/// <summary>A column of a table or view.</summary>
/// <param name="Ordinal">Zero-based position.</param>
/// <param name="Name">Column name.</param>
/// <param name="DataType">The engine's type name (for display only).</param>
/// <param name="AllowsNull">Whether NULL is allowed, or null when the driver does not say.</param>
public sealed record DatabaseColumn(int Ordinal, string Name, string DataType, bool? AllowsNull);

/// <summary>The rows of a query as a <see cref="MergeTable"/>, plus whether the row cap cut them off.</summary>
public sealed class DatabaseQueryResult
{
    internal DatabaseQueryResult(MergeTable table, bool truncated, int maxRows)
    {
        Table = table;
        Truncated = truncated;
        MaxRows = maxRows;
    }

    /// <summary>Columns and string rows, ready for <see cref="MergeDataSet"/>. When truncated it also carries a <see cref="DataKeys.QueryTruncated"/> warning in <see cref="MergeTable.Issues"/>.</summary>
    public MergeTable Table { get; }

    /// <summary>True when more rows existed than <see cref="MaxRows"/>.</summary>
    public bool Truncated { get; }

    /// <summary>The cap that was applied.</summary>
    public int MaxRows { get; }
}

/// <summary>
/// A database that can be browsed and queried (read-only). Every method opens its own short-lived connection, disposes it on
/// every path, honours the cancellation token and the configured timeouts, and reports failures as
/// <see cref="DatabaseException"/> with a stable key. Implementations never block the calling thread.
/// </summary>
public interface IDatabaseSource
{
    /// <summary>The (password-free) settings this source was created from.</summary>
    DatabaseConnectionSettings Settings { get; }

    /// <summary>Opens a connection and closes it again; throws <see cref="DatabaseException"/> if anything is wrong.</summary>
    Task TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists tables and views (system objects excluded), ordered by schema then name.</summary>
    Task<IReadOnlyList<DatabaseObject>> ListTablesAsync(CancellationToken cancellationToken = default);

    /// <summary>Describes the columns of a table given as <c>name</c> or <c>schema.name</c> (quote parts that contain dots).</summary>
    Task<IReadOnlyList<DatabaseColumn>> DescribeAsync(string table, CancellationToken cancellationToken = default);

    /// <summary>Describes the columns of an object returned by <see cref="ListTablesAsync"/>.</summary>
    Task<IReadOnlyList<DatabaseColumn>> DescribeAsync(DatabaseObject table, CancellationToken cancellationToken = default);

    /// <summary>The correctly quoted <c>SELECT * FROM ...</c> for an object (what the "browse table" UI should run).</summary>
    string BuildSelectAll(DatabaseObject table);

    /// <summary>Reads the first <paramref name="maxRows"/> rows of a table or view.</summary>
    Task<DatabaseQueryResult> PreviewAsync(DatabaseObject table, int maxRows = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a query after the read-only guard has accepted it. <paramref name="parameters"/> binds the query's <c>@name</c>
    /// placeholders (names without the @, case-insensitive). At most <paramref name="maxRows"/> rows are returned; if more
    /// existed <see cref="DatabaseQueryResult.Truncated"/> is set.
    /// </summary>
    /// <exception cref="DatabaseException">Always this type; see <see cref="DataKeys"/>.</exception>
    Task<DatabaseQueryResult> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        int maxRows = DatabaseSource.DefaultMaxRows, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a query row by row for very large batches without materialising it. The caller owns the returned stream and
    /// must dispose it (that closes the connection). Not capped; the caller decides when to stop.
    /// </summary>
    Task<DatabaseRowStream> OpenStreamAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An open forward-only result: normalized <see cref="Columns"/> and the formatted rows. Dispose to release the connection.
/// </summary>
public sealed class DatabaseRowStream : IAsyncDisposable
{
    private readonly Func<CancellationToken, IAsyncEnumerable<string[]>> _rows;
    private readonly Func<ValueTask> _dispose;
    private bool _disposed;

    internal DatabaseRowStream(IReadOnlyList<string> columns, Func<CancellationToken, IAsyncEnumerable<string[]>> rows, Func<ValueTask> dispose)
    {
        Columns = columns;
        _rows = rows;
        _dispose = dispose;
    }

    /// <summary>Column names, normalized exactly as <see cref="MergeTable"/> does (trimmed, unique, never empty).</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The rows, formatted with <see cref="DatabaseValueFormatter"/>. Enumerate once.</summary>
    public IAsyncEnumerable<string[]> ReadRowsAsync(CancellationToken cancellationToken = default) => _rows(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _dispose().ConfigureAwait(false);
    }
}
