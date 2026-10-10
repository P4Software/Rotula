namespace Rotula.Data;

/// <summary>
/// The stable string ids of the five built-in providers. They are persisted (connections.json) and referenced by packs and
/// presets, so they never change. Pack providers choose their own ids (lower case, letters, digits, dash).
/// </summary>
public static class ProviderIds
{
    /// <summary>Microsoft SQL Server.</summary>
    public const string SqlServer = "sqlserver";
    /// <summary>PostgreSQL.</summary>
    public const string PostgreSql = "postgresql";
    /// <summary>MySQL and MariaDB.</summary>
    public const string MySql = "mysql";
    /// <summary>SQLite file.</summary>
    public const string Sqlite = "sqlite";
    /// <summary>Any ODBC driver or DSN.</summary>
    public const string Odbc = "odbc";

    /// <summary>The id of a built-in enum value; <see cref="DatabaseProvider.Custom"/> has none and returns an empty string (settings must then carry an explicit id).</summary>
    public static string For(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => SqlServer,
        DatabaseProvider.PostgreSql => PostgreSql,
        DatabaseProvider.MySql => MySql,
        DatabaseProvider.Sqlite => Sqlite,
        DatabaseProvider.Odbc => Odbc,
        _ => string.Empty,
    };

    /// <summary>The enum value of a built-in id, or <see cref="DatabaseProvider.Custom"/> for any other id.</summary>
    public static DatabaseProvider ToEnum(string id) => id.ToLowerInvariant() switch
    {
        SqlServer => DatabaseProvider.SqlServer,
        PostgreSql => DatabaseProvider.PostgreSql,
        MySql => DatabaseProvider.MySql,
        Sqlite => DatabaseProvider.Sqlite,
        Odbc => DatabaseProvider.Odbc,
        _ => DatabaseProvider.Custom,
    };

    /// <summary>True for the five ids above.</summary>
    public static bool IsBuiltIn(string id) => ToEnum(id) != DatabaseProvider.Custom;
}

/// <summary>
/// Message keys of the provider and pack layer. Kept apart from <see cref="DataKeys"/> on purpose: the application checks that
/// every <c>DataKeys</c> error has a text, and these are added together with their texts by the UI when it adopts the registry.
/// </summary>
public static class ProviderKeys
{
    /// <summary>
    /// The saved password is not reused because the edited settings differ from the saved ones in a field that could change where
    /// or how the password is sent; the user must type it again. args[0] is the connection name.
    /// </summary>
    public const string PasswordReenterRequired = "Data.Store.PasswordReenterRequired";

    /// <summary>The provider id in the settings is not known (its pack is not installed or failed to load); args[0] is the id.</summary>
    public const string ProviderUnavailable = "Data.Error.ProviderUnavailable";

    /// <summary>ODBC driver missing, with the Microsoft Access / Excel / text next steps (64-bit Access Database Engine).</summary>
    public const string HintInstallAce64 = "Data.Hint.InstallAce64";
    /// <summary>The server is behind a connection pooler: use the direct host if features fail.</summary>
    public const string HintPoolerHost = "Data.Hint.PoolerHost";
    /// <summary>The service only accepts Microsoft Entra sign-in, which needs a later provider pack.</summary>
    public const string HintEntraOnly = "Data.Hint.EntraOnly";
    /// <summary>The server's certificate comes from a private authority: choose its CA certificate file.</summary>
    public const string HintCaCertificate = "Data.Hint.CaCertificate";
    /// <summary>Use an account that can only read (SELECT-only); the server cannot enforce read-only itself.</summary>
    public const string HintReadOnlyAccount = "Data.Hint.ReadOnlyAccount";
}
