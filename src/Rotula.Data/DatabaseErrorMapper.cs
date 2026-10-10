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
        bool timedOutByUs, string? password, string? connectionString)
        => Map(ProviderRegistry.Default.TryGet(ProviderIds.For(provider)), ex, connecting, cancellation, timedOutByUs, password, connectionString);

    /// <summary>As above, with the provider's own error mapper (null for none).</summary>
    public static DatabaseException Map(IProviderDescriptor? provider, Exception ex, bool connecting, CancellationToken cancellation,
        bool timedOutByUs, string? password, string? connectionString)
    {
        if (ex is DatabaseException de) return de;
        if (cancellation.IsCancellationRequested) return new DatabaseException(DataKeys.Cancelled);
        if (timedOutByUs) return new DatabaseException(DataKeys.Timeout);

        string detail = SecretRedactor.RedactText(FirstLine(ex), password, connectionString);
        var (number, state) = provider?.Errors.Extract(ex) ?? Extract(ex);
        string key = Classify(provider?.Errors, number, state, connecting);

        if (key == DataKeys.ConnectionFailed || key == DataKeys.QueryFailed)
        {
            // Network-level and TLS failures carry no database number; look at the exception chain.
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is DllNotFoundException or BadImageFormatException or PlatformNotSupportedException)
                    { key = DataKeys.DriverNotInstalled; break; }
                if (e is SocketException) { key = connecting ? DataKeys.HostUnreachable : DataKeys.ConnectionFailed; break; }
                if (e is AuthenticationException) { key = DataKeys.CertificateNotTrusted; break; }
                if (e is TimeoutException) { key = DataKeys.Timeout; break; }
            }
            if (connecting && key == DataKeys.ConnectionFailed && ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                key = DataKeys.CertificateNotTrusted;
        }
        return new DatabaseException(key, null, null, detail);
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
