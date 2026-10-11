using System.Reflection;

namespace Rotula.Data;

/// <summary>
/// Decides whether a SAVED password may be reused for EDITED connection settings. The rule is deny by default: the password
/// is reused only when every property of the settings is equal to the saved one, except the short allowlist of properties that
/// cannot change where, how or as whom the password is sent. Because the comparison walks all properties by reflection, a
/// property added to <see cref="DatabaseConnectionSettings"/> later is automatically treated as "must be identical".
/// </summary>
internal static class SavedPasswordPolicy
{
    /// <summary>
    /// Properties that may differ without forcing the user to re-enter the password. <c>Name</c> is how the saved entry was found.
    /// A TLS downgrade is never on this list: Encrypt and TrustServerCertificate are compared like everything else, in both directions.
    /// </summary>
    public static readonly IReadOnlySet<string> SafeToChange = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(DatabaseConnectionSettings.Name),
        nameof(DatabaseConnectionSettings.Database),
        nameof(DatabaseConnectionSettings.CommandTimeoutSeconds),
        nameof(DatabaseConnectionSettings.ConnectTimeoutSeconds),
    };

    private static readonly PropertyInfo[] Compared = typeof(DatabaseConnectionSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && !SafeToChange.Contains(p.Name))
        .ToArray();

    /// <summary>All settings properties and whether they are compared (true) or allowed to change (false); used by the tests.</summary>
    public static IEnumerable<(PropertyInfo Property, bool Compared)> Classify()
        => typeof(DatabaseConnectionSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p, !SafeToChange.Contains(p.Name)));

    /// <summary>True when the saved password may be used with <paramref name="edited"/>.</summary>
    public static bool CanReuse(DatabaseConnectionSettings saved, DatabaseConnectionSettings edited)
    {
        // Oracle's "alias" mode keeps a TNS alias in Database, and the alias decides which server is contacted. There, changing the
        // database name is changing the destination, so it must not reuse the saved password like a plain database switch would.
        if (IsAliasMode(saved) || IsAliasMode(edited))
        {
            if (!Same(saved.Database, edited.Database)) return false;
        }
        foreach (var p in Compared)
        {
            object? a = p.GetValue(saved);
            object? b = p.GetValue(edited);
            // A password typed inside a raw ODBC string is removed from the saved copy, so compare without it.
            if (p.Name == nameof(DatabaseConnectionSettings.OdbcConnectionString))
            {
                a = Strip(a as string);
                b = Strip(b as string);
            }
            if (!Same(a, b)) return false;
        }
        return true;
    }

    private static bool IsAliasMode(DatabaseConnectionSettings s) => string.Equals(s.Extras.Get("connectBy"), "alias", StringComparison.Ordinal);

    private static string? Strip(string? odbc) => string.IsNullOrWhiteSpace(odbc) ? null : SecretRedactor.ExtractPassword(odbc).Sanitized.Trim().TrimEnd(';');

    private static bool Same(object? a, object? b)
    {
        // null and empty text mean the same "not set"; everything else is compared exactly (host names too: a look-alike host must not match).
        if (a is string || b is string)
            return string.Equals(a as string ?? string.Empty, b as string ?? string.Empty, StringComparison.Ordinal);
        return Equals(a, b);
    }
}
