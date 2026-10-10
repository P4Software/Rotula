using System.Text.RegularExpressions;

namespace Rotula.Data;

/// <summary>Removes passwords from text that may end up on screen, in a log or in an exception.</summary>
public static partial class SecretRedactor
{
    /// <summary>The replacement shown instead of a secret.</summary>
    public const string Mask = "***";

    // key=value pairs whose key names a secret. Values may be quoted/braced (ODBC {a;b}) so those are matched first.
    [GeneratedRegex("(?<k>\\b(?:pwd|password|passwd|secret|access\\s*token)\\s*=\\s*)(?<v>\\{(?:[^}]|\\}\\})*\\}|\"[^\"]*\"|'[^']*'|[^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPair();

    /// <summary>Masks every <c>PWD=</c>, <c>Password=</c> ... value of a connection string.</summary>
    public static string RedactConnectionString(string? connectionString)
        => string.IsNullOrEmpty(connectionString) ? string.Empty : SecretPair().Replace(connectionString, m => m.Groups["k"].Value + Mask);

    /// <summary>
    /// Redacts free text (a provider's error message): masks secret key/value pairs and every occurrence of the known
    /// password and connection string.
    /// </summary>
    public static string RedactText(string? text, string? knownPassword = null, string? knownConnectionString = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string result = text;
        if (!string.IsNullOrEmpty(knownConnectionString)) result = result.Replace(knownConnectionString, "[connection string]", StringComparison.Ordinal);
        // A one- or two-character password would shred the message; the pair mask below still catches PWD=x.
        if (!string.IsNullOrEmpty(knownPassword) && knownPassword.Length >= 3) result = result.Replace(knownPassword, Mask, StringComparison.Ordinal);
        return SecretPair().Replace(result, m => m.Groups["k"].Value + Mask);
    }

    /// <summary>
    /// Splits the password out of a raw ODBC string: returns the string without <c>PWD=</c>/<c>Password=</c> parts and the
    /// password value (braces removed), or null when there was none.
    /// </summary>
    public static (string Sanitized, string? Password) ExtractPassword(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        string? password = null;
        string stripped = SecretPair().Replace(connectionString, m =>
        {
            string v = m.Groups["v"].Value;
            if (v.Length >= 2 && v[0] == '{' && v[^1] == '}') v = v[1..^1].Replace("}}", "}");
            else if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0]) v = v[1..^1];
            password = v;
            return string.Empty;
        });
        // Tidy the ';;' / leading ';' left behind by removal.
        stripped = Regex.Replace(stripped, ";{2,}", ";").Trim().TrimStart(';');
        return (stripped, password);
    }
}
