namespace Rotula.Data;

/// <summary>The database engines Rotula can read from.</summary>
public enum DatabaseProvider
{
    /// <summary>Microsoft SQL Server (including Express and LocalDB), through Microsoft.Data.SqlClient.</summary>
    SqlServer,

    /// <summary>PostgreSQL, through Npgsql.</summary>
    PostgreSql,

    /// <summary>MySQL and MariaDB, through MySqlConnector.</summary>
    MySql,

    /// <summary>A SQLite database file, through Microsoft.Data.Sqlite.</summary>
    Sqlite,

    /// <summary>
    /// Any source reachable through an ODBC driver or DSN installed on the machine (Oracle, Access, ...).
    /// The backing engine is unknown, so the query guard applies the union of every dialect's rules.
    /// </summary>
    Odbc,

    /// <summary>
    /// A provider that is not one of the five built-in engines (it comes from a provider pack). Such settings must also carry
    /// <see cref="DatabaseConnectionSettings.ProviderId"/>. Appended last so the persisted names and values of the others never change.
    /// </summary>
    Custom,
}

/// <summary>How the user signs in to the server.</summary>
public enum DatabaseAuthentication
{
    /// <summary>The current Windows account (preferred where the engine supports it: SQL Server, ODBC drivers that do).</summary>
    WindowsIntegrated,

    /// <summary>A user name and a password. The password is never part of the settings object.</summary>
    UserPassword,
}

/// <summary>What a browsable database object is.</summary>
public enum DatabaseObjectKind
{
    /// <summary>A base table.</summary>
    Table,

    /// <summary>A view.</summary>
    View,
}
