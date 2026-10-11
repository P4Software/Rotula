using System.Data.Common;
using System.Data.Odbc;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Rotula.Data;

/// <summary>
/// Maps provider exceptions to <see cref="DatabaseException"/> keys. The classification itself is a pure function of
/// (provider, native number, SQLSTATE, phase) so it can be tested without a server; the provider-specific exception types
/// are only unpacked into those numbers.
/// </summary>
internal static class DatabaseErrorMapper
{
    /// <summary>The pure classification for a built-in provider: returns a <see cref="DataKeys"/> error key.</summary>
    public static string Classify(DatabaseProvider provider, int? number, string? sqlState, bool connecting)
        => Classify(ProviderRegistry.Default.TryGet(ProviderIds.For(provider))?.Errors, number, sqlState, connecting);

    /// <summary>The pure classification with the provider's own error mapper (null for none): returns a <see cref="DataKeys"/> error key.</summary>
    public static string Classify(IProviderErrorMapper? mapper, int? number, string? sqlState, bool connecting)
    {
        string fallback = connecting ? DataKeys.ConnectionFailed : DataKeys.QueryFailed;

        // SQLSTATE classes understood by every driver that reports them (PostgreSQL, ODBC, MySQL).
        if (sqlState is { Length: >= 2 })
        {
            switch (sqlState)
            {
                case "28P01" or "28000" or "28001": return DataKeys.AuthenticationFailed;
                case "3D000": return DataKeys.DatabaseNotFound;
                case "42501": return DataKeys.PermissionDenied;
                case "57014": return DataKeys.Timeout;
                // PostgreSQL: 42P18 "could not determine data type of parameter $1"; 42P08 "inconsistent types deduced for parameter $1".
                case "42P18" or "42P08": return DataKeys.ParameterTypeUnknown;
                case "IM002" or "IM003" or "IM004" or "IM005": return DataKeys.DriverNotInstalled;
                case "HYT00" or "HYT01": return DataKeys.Timeout;
                case "08001" or "08S01" or "08003": return connecting ? DataKeys.HostUnreachable : DataKeys.ConnectionFailed;
            }
        }
        return mapper?.ClassifyNumber(number, connecting) ?? fallback;
    }

    /// <summary>
    /// Converts any exception raised while connecting or running a command (built-in provider). The caller's cancellation wins over
    /// whatever the provider wrapped it into; secrets are scrubbed from the detail.
    /// </summary>
    public static DatabaseException Map(DatabaseProvider provider, Exception ex, bool connecting, CancellationToken cancellation,
        bool timedOutByUs, string? password, string? connectionString, bool queryHasParameters = false)
        => Map(ProviderRegistry.Default.TryGet(ProviderIds.For(provider)), ex, connecting, cancellation, timedOutByUs, password, connectionString, queryHasParameters);

    /// <summary>
    /// As above, with the provider's own error mapper (null for none). <paramref name="queryHasParameters"/> lets a "function does not exist"
    /// answer (SQLSTATE 42883) be explained as a parameter type problem, which is what it almost always is when the query has parameters.
    /// </summary>
    public static DatabaseException Map(IProviderDescriptor? provider, Exception ex, bool connecting, CancellationToken cancellation,
        bool timedOutByUs, string? password, string? connectionString, bool queryHasParameters = false)
    {
        if (ex is DatabaseException de) return de;
        if (cancellation.IsCancellationRequested) return new DatabaseException(DataKeys.Cancelled);
        if (timedOutByUs) return new DatabaseException(DataKeys.Timeout);

        string detail = SecretRedactor.RedactText(FirstLine(ex), password, connectionString);
        var (number, state) = provider?.Errors.Extract(ex) ?? Extract(ex);
        string key = Classify(provider?.Errors, number, state, connecting);
        string? reason = key == DataKeys.ParameterTypeUnknown ? DataKeys.ParameterTypeUnknownHint : null;

        if (!connecting && state == "42883" && queryHasParameters)
        {
            // PostgreSQL reports "operator does not exist: integer = text" as 42883, the same code as an unknown function name. With
            // parameters in the query the first reading is by far the likelier one, and the hint costs nothing if it is the other.
            return new DatabaseException(DataKeys.ParameterTypeMismatch, DataKeys.ParameterTypeMismatchHint, null, detail);
        }

        // Network-level and TLS failures carry no database number, or a generic "cannot connect" one (MySQL 1042), so the exception
        // chain decides. HostUnreachable is included on purpose: it is what the number alone says, and the chain can be more precise.
        if (key is DataKeys.ConnectionFailed or DataKeys.QueryFailed or DataKeys.HostUnreachable)
        {
            if (connecting && HasAuthenticationException(ex))
            {
                // A failed TLS handshake (certificate not trusted, wrong host name). Checked before anything else because drivers
                // wrap it in their "cannot connect" error, which would otherwise read as an unreachable server.
                key = DataKeys.CertificateNotTrusted;
            }
            else if (connecting && IsRejectedLoginReset(provider?.Errors, ex, connecting))
            {
                // The server accepted the TCP connection and then dropped it during the login (it does that right after refusing some
                // logins). It is not "unreachable"; the likeliest cause is a rejected login, but the server did not say so.
                key = DataKeys.AuthenticationFailed;
                reason = DataKeys.AuthenticationProbable;
            }
            else
            {
                for (Exception? e = ex; e is not null; e = e.InnerException)
                {
                    if (e is DllNotFoundException or BadImageFormatException or PlatformNotSupportedException)
                        { key = DataKeys.DriverNotInstalled; break; }
                    if (e is SocketException) { key = connecting ? DataKeys.HostUnreachable : DataKeys.ConnectionFailed; break; }
                    if (e is AuthenticationException) { key = DataKeys.CertificateNotTrusted; break; }
                    if (e is TimeoutException) { key = DataKeys.Timeout; break; }
                }
            }
            if (connecting && key == DataKeys.ConnectionFailed && ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                key = DataKeys.CertificateNotTrusted;
        }
        return new DatabaseException(key, reason, null, detail);
    }

    /// <summary>True when a TLS handshake failure (<see cref="AuthenticationException"/>) is anywhere in the exception chain.</summary>
    private static bool HasAuthenticationException(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is AuthenticationException) return true;
        return false;
    }

    /// <summary>
    /// True when the connection was established and then reset or closed by the server while logging in, for a provider whose server does
    /// that after refusing a login (<see cref="IProviderErrorMapper.RejectedLoginShowsAsReset"/>). A refused connection (nobody listening)
    /// is not a reset and stays "host unreachable".
    /// </summary>
    internal static bool IsRejectedLoginReset(IProviderErrorMapper? mapper, Exception ex, bool connecting)
    {
        if (!connecting || mapper is not { RejectedLoginShowsAsReset: true }) return false;
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted }) return true;
            // A clean close by the server surfaces as "reached the end of the stream" instead of a socket error.
            if (e is EndOfStreamException) return true;
        }
        return false;
    }

    /// <summary>Pulls the native number and SQLSTATE out of the provider exception types.</summary>
    public static (int? Number, string? SqlState) Extract(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SqlException sql: return (sql.Number, null);
                case PostgresException pg: return (null, pg.SqlState);
                case MySqlException my: return ((int)my.ErrorCode, my.SqlState);
                case SqliteException lite: return (lite.SqliteErrorCode, null);
                case OdbcException odbc when odbc.Errors.Count > 0: return (odbc.Errors[0].NativeError, odbc.Errors[0].SQLState);
            }
        }
        return (null, null);
    }

    private static string FirstLine(Exception ex)
    {
        string m = ex.Message ?? string.Empty;
        int nl = m.IndexOfAny(new[] { '\r', '\n' });
        return nl > 0 ? m[..nl] : m;
    }

    /// <summary>True for exceptions that mean "the operation was cancelled" regardless of who wrapped them.</summary>
    public static bool IsCancellation(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is OperationCanceledException) return true;
        return false;
    }
}
