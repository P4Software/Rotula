namespace Rotula.Data;

/// <summary>
/// Stable message keys raised by this library. The library carries no UI text: the application maps each key to
/// localized text and formats it with <see cref="DatabaseException.Args"/>.
/// </summary>
public static class DataKeys
{
    /// <summary>The server could not be reached or the connection failed for an unclassified reason.</summary>
    public const string ConnectionFailed = "Data.Error.ConnectionFailed";
    /// <summary>The user name or password was refused.</summary>
    public const string AuthenticationFailed = "Data.Error.AuthenticationFailed";
    /// <summary>Connecting or running the command took longer than the configured timeout.</summary>
    public const string Timeout = "Data.Error.Timeout";
    /// <summary>The host name did not resolve or nothing answered on that address and port.</summary>
    public const string HostUnreachable = "Data.Error.HostUnreachable";
    /// <summary>The database (or SQLite file) does not exist or is not accessible.</summary>
    public const string DatabaseNotFound = "Data.Error.DatabaseNotFound";
    /// <summary>The server's TLS certificate could not be validated; the UI may offer the explicit "trust this server" option.</summary>
    public const string CertificateNotTrusted = "Data.Error.CertificateNotTrusted";
    /// <summary>The account may not read the requested object.</summary>
    public const string PermissionDenied = "Data.Error.PermissionDenied";
    /// <summary>The query was refused by the read-only guard; <see cref="DatabaseException.ReasonKey"/> says why.</summary>
    public const string QueryRejected = "Data.Error.QueryRejected";
    /// <summary>The database reported an error while running the query (bad column, syntax, ...).</summary>
    public const string QueryFailed = "Data.Error.QueryFailed";
    /// <summary>The operation was cancelled by the caller.</summary>
    public const string Cancelled = "Data.Error.Cancelled";
    /// <summary>The requested row cap is above the hard limit; use the streaming API instead.</summary>
    public const string TooManyRows = "Data.Error.TooManyRows";
    /// <summary>The ODBC driver or data source named in the settings is not installed on this machine.</summary>
    public const string DriverNotInstalled = "Data.Error.DriverNotInstalled";
    /// <summary>A setting is missing or contains characters that are not allowed; args[0] is the setting name.</summary>
    public const string InvalidSettings = "Data.Error.InvalidSettings";
    /// <summary>The saved connection does not exist; args[0] is the name.</summary>
    public const string ConnectionNotFound = "Data.Error.ConnectionNotFound";
    /// <summary>
    /// The server reported that it cannot work out the type of a query parameter (PostgreSQL 42P18/42P08, for example <c>(@x IS NULL OR col = @x)</c>
    /// with a null value). <see cref="DatabaseException.ReasonKey"/> is <see cref="ParameterTypeUnknownHint"/> (cast the parameter, for example <c>@x::text</c>).
    /// </summary>
    public const string ParameterTypeUnknown = "Data.Error.ParameterTypeUnknown";
    /// <summary>Hint for <see cref="ParameterTypeUnknown"/>: give the parameter a type in the query with a cast such as <c>@x::text</c>.</summary>
    public const string ParameterTypeUnknownHint = "Data.Error.ParameterTypeUnknown.Hint";
    /// <summary>
    /// The server could not compare a parameter with a column of another type (PostgreSQL 42883 on a query that has parameters, for example text against a
    /// number or a date). <see cref="DatabaseException.ReasonKey"/> is <see cref="ParameterTypeMismatchHint"/>.
    /// </summary>
    public const string ParameterTypeMismatch = "Data.Error.ParameterTypeMismatch";
    /// <summary>Hint for <see cref="ParameterTypeMismatch"/>: pass a value of the column's type, or cast the parameter (<c>@x::numeric</c>, <c>@x::date</c>).</summary>
    public const string ParameterTypeMismatchHint = "Data.Error.ParameterTypeMismatch.Hint";
    /// <summary>
    /// Reason (<see cref="DatabaseException.ReasonKey"/>) of <see cref="AuthenticationFailed"/> when the server only closed the connection during login
    /// instead of saying why: the user name or password is probably wrong (a server certificate that is not trusted can look the same).
    /// </summary>
    public const string AuthenticationProbable = "Data.Error.AuthenticationFailed.Probable";
    /// <summary>
    /// Help text of the "Trust this server's certificate" option for MySQL/MariaDB: a MariaDB 11.4+ account with a password verifies the
    /// server's certificate by its fingerprint inside the password handshake, so a self-signed certificate connects even without that option.
    /// </summary>
    public const string MariaDbCertificateFingerprintHelp = "Data.Option.TrustServerCertificate.Help.MariaDb";

    /// <summary>Reason: the statement is not a single SELECT / WITH ... SELECT.</summary>
    public const string RejectNotSelect = "Data.Query.Rejected.NotSelect";
    /// <summary>Reason: more than one statement.</summary>
    public const string RejectMultipleStatements = "Data.Query.Rejected.MultipleStatements";
    /// <summary>Reason: empty text.</summary>
    public const string RejectEmpty = "Data.Query.Rejected.Empty";
    /// <summary>Reason: a data-changing or administrative keyword was found; args[0] is the keyword.</summary>
    public const string RejectForbiddenKeyword = "Data.Query.Rejected.ForbiddenKeyword";
    /// <summary>Reason: SELECT ... INTO (creates a table or writes a file).</summary>
    public const string RejectSelectInto = "Data.Query.Rejected.SelectInto";
    /// <summary>Reason: row locking (FOR UPDATE / FOR SHARE / LOCK IN SHARE MODE).</summary>
    public const string RejectLocking = "Data.Query.Rejected.Locking";
    /// <summary>Reason: a function that reads files, reaches other servers or runs code; args[0] is the function.</summary>
    public const string RejectForbiddenFunction = "Data.Query.Rejected.ForbiddenFunction";
    /// <summary>Reason: a MySQL executable comment (<c>/*! ... */</c>), whose content runs as SQL.</summary>
    public const string RejectExecutableComment = "Data.Query.Rejected.ExecutableComment";
    /// <summary>Reason: an unterminated string, comment or quoted name.</summary>
    public const string RejectUnterminated = "Data.Query.Rejected.Unterminated";
    /// <summary>Reason: the query text is longer than the allowed maximum.</summary>
    public const string RejectTooLong = "Data.Query.Rejected.TooLong";
    /// <summary>Reason: the text could be read in more than one way (a backslash or quote the database might treat differently), so it is refused rather than guessed.</summary>
    public const string RejectAmbiguous = "Data.Query.Rejected.Ambiguous";
    /// <summary>Reason: a @parameter used by the query has no value; args[0] is the name.</summary>
    public const string RejectMissingParameter = "Data.Query.Rejected.MissingParameter";

    /// <summary>Table issue (warning): the result was cut at the row cap; args[0] is the cap.</summary>
    public const string QueryTruncated = "Data.Query.Truncated";

    /// <summary>Store: the connections file could not be read as valid data.</summary>
    public const string StoreDamaged = "Data.Store.Damaged";
    /// <summary>Store: the connections file is larger than the allowed size and was ignored.</summary>
    public const string StoreTooLarge = "Data.Store.TooLarge";
    /// <summary>Store: a saved password could not be unprotected (different Windows user or machine); re-enter it.</summary>
    public const string StorePasswordUnreadable = "Data.Store.PasswordUnreadable";
    /// <summary>Store: the file could not be written.</summary>
    public const string StoreWriteFailed = "Data.Store.WriteFailed";
    /// <summary>Store: password protection is not available on this operating system.</summary>
    public const string StoreProtectionUnavailable = "Data.Store.ProtectionUnavailable";
}

/// <summary>
/// The only exception type this library throws for expected failures. <see cref="Exception.Message"/> is the key plus
/// arguments (developer-facing, never localized, never containing a password or full connection string).
/// </summary>
public sealed class DatabaseException : Exception
{
    /// <summary>Creates an exception. <paramref name="detail"/> must already be redacted (see <see cref="SecretRedactor"/>).</summary>
    public DatabaseException(string key, string? reasonKey = null, IReadOnlyList<string>? args = null, string? detail = null)
        : base(BuildMessage(key, reasonKey, args))
    {
        Key = key;
        ReasonKey = reasonKey;
        Args = args ?? Array.Empty<string>();
        Detail = detail;
    }

    /// <summary>One of the <c>Data.Error.*</c> / <c>Data.Store.*</c> keys in <see cref="DataKeys"/>.</summary>
    public string Key { get; }

    /// <summary>
    /// For <see cref="DataKeys.QueryRejected"/>, the <c>Data.Query.Rejected.*</c> reason. A few other keys carry an optional hint here
    /// (<see cref="DataKeys.AuthenticationProbable"/>, <see cref="DataKeys.ParameterTypeUnknownHint"/>, <see cref="DataKeys.ParameterTypeMismatchHint"/>); otherwise null.
    /// </summary>
    public string? ReasonKey { get; }

    /// <summary>Culture-neutral format arguments for the localized message.</summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>
    /// The database's own (already redacted) message, offered for a "show details" expander. Never contains the password
    /// or connection string. May be in the server's language.
    /// </summary>
    public string? Detail { get; }

    /// <summary>True when the failure is the caller's own cancellation.</summary>
    public bool IsCancellation => Key == DataKeys.Cancelled;

    private static string BuildMessage(string key, string? reason, IReadOnlyList<string>? args)
    {
        string m = reason is null ? key : key + " (" + reason + ")";
        return args is { Count: > 0 } ? m + ": " + string.Join(", ", args) : m;
    }
}
