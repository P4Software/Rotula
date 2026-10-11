using System.Data.Common;
using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using FirebirdSql.Data.Types;

namespace Rotula.Data.Pack.Firebird;

/// <summary>The entry class named in pack.json: contributes the <c>firebird</c> provider.</summary>
public sealed class FirebirdPack : IDatabaseProviderPack
{
    /// <inheritdoc />
    public string PackId => "firebird";

    /// <inheritdoc />
    public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[] { new FirebirdDescriptor() };
}

/// <summary>
/// Firebird 2.5 to 5.x over the network (the driver speaks the wire protocol itself, no client library is needed). Every query runs in a
/// READ ONLY snapshot transaction, and the wire is encrypted whenever the sign-in method can carry a session key.
/// </summary>
public sealed class FirebirdDescriptor : IProviderDescriptor, IProviderErrorMapper
{
    /// <summary>The provider id persisted in connections.json.</summary>
    public const string ProviderId = "firebird";

    /// <summary>Extra: the SQL role to connect with (recommended: a role that may only SELECT).</summary>
    public const string Role = "role";

    /// <summary>Firebird's default TCP port.</summary>
    public const int DefaultFirebirdPort = 3050;

    // ISC error codes, checked against iberror_c.h of the Firebird 5.0.4 distribution. The driver reports the first status vector
    // entry as FbException.ErrorCode.
    private const int IscConnectReject = 335544421;
    private const int IscNetworkError = 335544721;
    private const int IscNetReadError = 335544726;
    private const int IscNetWriteError = 335544727;
    private const int IscLostDbConnection = 335544741;
    private const int IscLogin = 335544472;          // "Your user name and password are not defined"
    private const int IscLoginError = 335545106;     // "Error occurred during login, please check server firebird.log"
    private const int IscIoError = 335544344;        // "I/O error during ... operation for file" (also: file not found)
    private const int IscNoPrivilege = 335544352;    // "no permission for ... access to ..."
    private const int IscCancelled = 335544794;      // "operation cancelled"
    private const int IscShutdown = 335544528;       // "database shutdown"
    private const int IscAttachmentShutdown = 335544856;
    private const int IscUnavailable = 335544375;    // "unavailable database"
    private const int IscLockTimeout = 335544510;    // "lock time-out on wait transaction"
    private const int IscDeadlock = 335544336;       // "deadlock"
    private const int IscConfigStatementTimeout = 335545127;     // statement timeout (Firebird 4+): from the server configuration
    private const int IscAttachmentStatementTimeout = 335545128;  // ... from the attachment
    private const int IscRequestStatementTimeout = 335545129;     // ... from the request
    private const int IscAccessDenied = 335544831;   // "Use of database at location ... is not allowed by server configuration"
    /// <summary>Private code for "the driver gave up on a command by itself" (its own timeout), which carries no ISC code.</summary>
    private const int DriverCancelled = -1;

    /// <inheritdoc />
    public string Id => ProviderId;
    /// <inheritdoc />
    public string DisplayNameKey => "Conn.Provider.Firebird";
    /// <inheritdoc />
    public string DisplayName => "Firebird";
    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Server;
    /// <inheritdoc />
    public int? DefaultPort => DefaultFirebirdPort;

    /// <inheritdoc />
    public IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } =
        new[] { DatabaseAuthentication.UserPassword, DatabaseAuthentication.WindowsIntegrated };

    /// <inheritdoc />
    public IReadOnlyList<ProviderOption> Options { get; } = new[]
    {
        StandardOptions.Host(),
        StandardOptions.Port(),
        new ProviderOption(ProviderOptionKeys.Database, "Data.Option.Firebird.Database", ProviderOptionKind.Text)
            { Label = "Database path or alias on the server", Required = true, HelpKey = "Data.Option.Firebird.Database.Help" },
        StandardOptions.UserName(),
        new ProviderOption(ProviderOptionKeys.Encrypt, "Data.Option.Encrypt", ProviderOptionKind.Bool)
            { Label = "Encrypt the connection", DefaultValue = "true", HelpKey = "Data.Option.Firebird.Encrypt.Help" },
        new ProviderOption(Role, "Data.Option.Firebird.Role", ProviderOptionKind.Text)
            { Label = "SQL role (optional)", Advanced = true, HelpKey = "Data.Option.Firebird.Role.Help" },
    };

    /// <inheritdoc />
    public bool RequiresServer => true;
    /// <inheritdoc />
    public SqlDialect Dialect => SqlDialects.Firebird;

    /// <inheritdoc />
    public ReadOnlyPolicy ReadOnly { get; } = new()
    {
        // The transaction itself is started by BeginTransactionAsync with the READ flag (isc_tpb_read); there is no SQL statement for it.
        Description = "Every query runs in a READ ONLY snapshot transaction (isc_tpb_read + isc_tpb_concurrency) that is rolled back, behind the query guard. " +
                      "A selectable stored procedure cannot write through it: the server refuses (verified on Firebird 5.0.4, also for IN AUTONOMOUS TRANSACTION; older servers are untested). " +
                      "Even so, connect with an account or role that can only SELECT: the guard cannot see what a selectable procedure does.",
    };

    /// <inheritdoc />
    public IProviderErrorMapper Errors => this;

    /// <inheritdoc />
    public void Validate(DatabaseConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ProviderValidation.RequireHost(settings.Host);
        ProviderValidation.RequireIdentifierLike(settings.Database, nameof(settings.Database), 1024);
        if (settings.Authentication == DatabaseAuthentication.UserPassword) ProviderValidation.RequireIdentifierLike(settings.UserName, nameof(settings.UserName), 256);
        else if (!string.IsNullOrEmpty(settings.UserName)) ProviderValidation.RequireIdentifierLike(settings.UserName, nameof(settings.UserName), 256);
        if (settings.Extras.Get(Role) is { } role)
        {
            ProviderValidation.RequireIdentifierLike(role, "Extras." + Role, 256);
            if (role.Contains('"') || role.Contains('\'')) throw ProviderValidation.Invalid("Extras." + Role);
        }
    }

    /// <inheritdoc />
    public string BuildConnectionString(DatabaseConnectionSettings settings, string? password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var b = new FbConnectionStringBuilder
        {
            DataSource = settings.Host!,
            Port = settings.Port ?? DefaultFirebirdPort,
            Database = settings.Database!,
            // UTF8 so accents, n-tilde and inverted punctuation arrive intact whatever the database default character set is.
            Charset = "UTF8",
            ServerType = FbServerType.Default,
            Pooling = false,
            ConnectionTimeout = settings.ConnectTimeoutSeconds,
            CommandTimeout = settings.CommandTimeoutSeconds,
            ApplicationName = "Rotula",
            // A read-only tool has no business turning on trigger-less or no-garbage-collect attachments.
            NoDatabaseTriggers = false,
        };
        if (settings.Authentication == DatabaseAuthentication.UserPassword)
        {
            b.UserID = settings.UserName!;
            b.Password = password ?? string.Empty;
            // Wire encryption needs the session key of the SRP sign-in, so it can be required only with a user name and password.
            b.WireCrypt = settings.Encrypt ? FbWireCrypt.Required : FbWireCrypt.Enabled;
        }
        else
        {
            // Windows sign-in (the Win_Sspi plugin) has no session key to encrypt with: encrypt when the server can, never require it.
            b.WireCrypt = FbWireCrypt.Enabled;
        }
        if (settings.Extras.Get(Role) is { Length: > 0 } role) b.Role = role;
        return b.ConnectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings) => new FbConnection(connectionString);

    /// <summary>A READ ONLY snapshot transaction: the server itself refuses any write made through this connection.</summary>
    public async Task<DbTransaction> BeginTransactionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not FbConnection fb) throw new InvalidOperationException("Not a Firebird connection.");
        var options = new FbTransactionOptions
        {
            // Read: no writes. Concurrency: a consistent snapshot for the whole query. Wait: never fail on a lock held by a writer
            // (a snapshot read-only transaction does not conflict with writers anyway).
            TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.Wait,
        };
        return await fb.BeginTransactionAsync(options, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- values

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbDataReader, int[]> Scales = new();

    /// <summary>
    /// The driver returns TIME as a TimeSpan and a NUMERIC/DECIMAL as a System.Decimal that has lost its trailing zeros (12.50 arrives as
    /// 12.5). A label needs the price as stored, so both are written here: time of day without a day part, decimals with the column's scale.
    /// </summary>
    public string? FormatCell(DbDataReader reader, int ordinal)
    {
        Type type = reader.GetFieldType(ordinal);
        if (type == typeof(TimeSpan))
            return TimeOnly.FromTimeSpan(reader.GetFieldValue<TimeSpan>(ordinal)).ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        if (type == typeof(FbDecFloat))
        {
            // DECFLOAT arrives as coefficient and exponent and prints as 125E-2; write the plain number (1.25). inf and nan stay as text.
            var v = reader.GetFieldValue<FbDecFloat>(ordinal);
            string text = v.ToString();
            if (!text.Contains('E', StringComparison.Ordinal)) return text;
            if (v.Exponent <= 0) return FormatDecimal(v.Coefficient, -v.Exponent);
            return (v.Coefficient * System.Numerics.BigInteger.Pow(10, v.Exponent)).ToString(CultureInfo.InvariantCulture);
        }
        if (type == typeof(decimal))
        {
            int scale = Scales.GetValue(reader, ScalesOf)[ordinal];
            if (scale >= 0) return reader.GetDecimal(ordinal).ToString("F" + scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <summary>Writes an unscaled integer with <paramref name="scale"/> decimal places as exact text (12345 with scale 2 is 123.45).</summary>
    internal static string FormatDecimal(System.Numerics.BigInteger unscaled, int scale)
    {
        string digits = System.Numerics.BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture);
        if (scale > 0)
        {
            digits = digits.PadLeft(scale + 1, '0');
            digits = digits[..^scale] + "." + digits[^scale..];
        }
        return (unscaled.Sign < 0 ? "-" : string.Empty) + digits;
    }

    private static int[] ScalesOf(DbDataReader reader)
    {
        var scales = new int[reader.FieldCount];
        Array.Fill(scales, -1);
        using var schema = reader.GetSchemaTable();
        if (schema is null || !schema.Columns.Contains("NumericScale") || !schema.Columns.Contains("ColumnOrdinal")) return scales;
        foreach (System.Data.DataRow row in schema.Rows)
        {
            int ordinal = Convert.ToInt32(row["ColumnOrdinal"], CultureInfo.InvariantCulture);
            if (ordinal >= 0 && ordinal < scales.Length && row["NumericScale"] is not DBNull)
                scales[ordinal] = Convert.ToInt32(row["NumericScale"], CultureInfo.InvariantCulture);
        }
        return scales;
    }

    // ---------------------------------------------------------------- errors

    /// <inheritdoc />
    public (int? Number, string? SqlState) Extract(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is FbException fb)
            {
                int? code = fb.ErrorCode == 0 ? null : fb.ErrorCode;
                string? state = string.IsNullOrEmpty(fb.SQLSTATE) ? null : fb.SQLSTATE;
                // Firebird reuses generic SQLSTATE classes for unrelated things ("no permission" and "not allowed by configuration" are 28000,
                // which the shared mapper reads as a bad password; a missing file is 08001, "cannot connect"). A code we know wins.
                return (code, ClassifyNumber(code, connecting: true) is null ? state : null);
            }
            // A command the driver cancelled itself (its timeout). A cancel requested by the caller never gets here: the shared mapper
            // answers that one first.
            if (e is OperationCanceledException) return (DriverCancelled, null);
        }
        return (null, null);
    }

    /// <inheritdoc />
    public string? ClassifyNumber(int? number, bool connecting) => number switch
    {
        IscLogin or IscLoginError => DataKeys.AuthenticationFailed,
        IscAccessDenied => DataKeys.PermissionDenied,
        IscIoError => DataKeys.DatabaseNotFound,
        IscNoPrivilege => DataKeys.PermissionDenied,
        IscConnectReject or IscNetworkError or IscNetReadError or IscNetWriteError or IscLostDbConnection or IscUnavailable or IscShutdown or IscAttachmentShutdown
            => connecting ? DataKeys.HostUnreachable : DataKeys.ConnectionFailed,
        IscLockTimeout or IscDeadlock or IscConfigStatementTimeout or IscAttachmentStatementTimeout or IscRequestStatementTimeout or DriverCancelled => DataKeys.Timeout,
        IscCancelled => DataKeys.Cancelled,
        _ => null,
    };
}
