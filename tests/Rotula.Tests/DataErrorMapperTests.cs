using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>Error classification without servers: pure code tables plus the exception types that can be constructed.</summary>
public class DataErrorMapperTests
{
    [Theory]
    [InlineData(DatabaseProvider.SqlServer, 18456, null, true, DataKeys.AuthenticationFailed)]
    [InlineData(DatabaseProvider.SqlServer, 4060, null, true, DataKeys.DatabaseNotFound)]
    [InlineData(DatabaseProvider.SqlServer, -2, null, false, DataKeys.Timeout)]
    [InlineData(DatabaseProvider.SqlServer, 53, null, true, DataKeys.HostUnreachable)]
    [InlineData(DatabaseProvider.SqlServer, 229, null, false, DataKeys.PermissionDenied)]
    [InlineData(DatabaseProvider.SqlServer, 208, null, false, DataKeys.QueryFailed)]
    [InlineData(DatabaseProvider.SqlServer, 12345, null, true, DataKeys.ConnectionFailed)]
    [InlineData(DatabaseProvider.PostgreSql, null, "28P01", true, DataKeys.AuthenticationFailed)]
    [InlineData(DatabaseProvider.PostgreSql, null, "3D000", true, DataKeys.DatabaseNotFound)]
    [InlineData(DatabaseProvider.PostgreSql, null, "42501", false, DataKeys.PermissionDenied)]
    [InlineData(DatabaseProvider.PostgreSql, null, "57014", false, DataKeys.Timeout)]
    [InlineData(DatabaseProvider.PostgreSql, null, "42P01", false, DataKeys.QueryFailed)]
    [InlineData(DatabaseProvider.MySql, 1045, "28000", true, DataKeys.AuthenticationFailed)]
    [InlineData(DatabaseProvider.MySql, 1049, "42000", true, DataKeys.DatabaseNotFound)]
    [InlineData(DatabaseProvider.MySql, 1142, "42000", false, DataKeys.PermissionDenied)]
    [InlineData(DatabaseProvider.MySql, 1042, null, true, DataKeys.HostUnreachable)]
    [InlineData(DatabaseProvider.MySql, 1064, "42000", false, DataKeys.QueryFailed)]
    [InlineData(DatabaseProvider.Sqlite, 14, null, true, DataKeys.DatabaseNotFound)]
    [InlineData(DatabaseProvider.Sqlite, 5, null, false, DataKeys.Timeout)]
    [InlineData(DatabaseProvider.Sqlite, 1, null, false, DataKeys.QueryFailed)]
    [InlineData(DatabaseProvider.Odbc, 0, "IM002", true, DataKeys.DriverNotInstalled)]
    [InlineData(DatabaseProvider.Odbc, 0, "28000", true, DataKeys.AuthenticationFailed)]
    [InlineData(DatabaseProvider.Odbc, 0, "HYT00", false, DataKeys.Timeout)]
    [InlineData(DatabaseProvider.Odbc, 0, "08001", true, DataKeys.HostUnreachable)]
    [InlineData(DatabaseProvider.Odbc, 4060, "42000", true, DataKeys.DatabaseNotFound)]
    public void Classify_MapsNativeCodes(DatabaseProvider p, int? number, string? state, bool connecting, string expected)
        => Assert.Equal(expected, DatabaseErrorMapper.Classify(p, number, state, connecting));

    [Fact]
    public void Cancellation_WinsOverWhateverTheProviderThrew()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = DatabaseErrorMapper.Map(DatabaseProvider.PostgreSql, new NpgsqlException("boom"), false, cts.Token, false, null, null);
        Assert.Equal(DataKeys.Cancelled, ex.Key);
        Assert.True(ex.IsCancellation);
    }

    [Fact]
    public void OurOwnTimeout_IsReportedAsTimeout()
    {
        var ex = DatabaseErrorMapper.Map(DatabaseProvider.Sqlite, new OperationCanceledException(), false, CancellationToken.None, true, null, null);
        Assert.Equal(DataKeys.Timeout, ex.Key);
    }

    [Fact]
    public void RealPostgresException_IsMappedBySqlState()
    {
        var pg = new PostgresException("password authentication failed for user \"u\"", "FATAL", "FATAL", "28P01");
        var ex = DatabaseErrorMapper.Map(DatabaseProvider.PostgreSql, pg, true, CancellationToken.None, false, "pw", null);
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
    }

    [Fact]
    public void RealMySqlException_IsMappedByErrorCode()
    {
        // The provider keeps its error-code constructors internal, so build the exception reflectively.
        var ctor = typeof(MySqlException).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(MySqlErrorCode));
        var args = ctor.GetParameters().Select(p => p.ParameterType == typeof(MySqlErrorCode) ? (object)MySqlErrorCode.AccessDenied
            : p.ParameterType == typeof(string) ? "Access denied for user" : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        var my = (MySqlException)ctor.Invoke(args);
        Assert.Equal(DataKeys.AuthenticationFailed, DatabaseErrorMapper.Map(DatabaseProvider.MySql, my, true, CancellationToken.None, false, null, null).Key);
    }

    [Fact]
    public void RealSqliteException_IsMappedByResultCode()
    {
        var lite = new SqliteException("unable to open database file", 14);
        Assert.Equal(DataKeys.DatabaseNotFound, DatabaseErrorMapper.Map(DatabaseProvider.Sqlite, lite, true, CancellationToken.None, false, null, null).Key);
    }

    [Fact]
    public void NetworkAndTlsFailures_AreRecognisedFromTheExceptionChain()
    {
        var net = new InvalidOperationException("x", new SocketException(10061));
        Assert.Equal(DataKeys.HostUnreachable, DatabaseErrorMapper.Map(DatabaseProvider.PostgreSql, net, true, CancellationToken.None, false, null, null).Key);
        var tls = new InvalidOperationException("x", new AuthenticationException("The remote certificate is invalid"));
        Assert.Equal(DataKeys.CertificateNotTrusted, DatabaseErrorMapper.Map(DatabaseProvider.PostgreSql, tls, true, CancellationToken.None, false, null, null).Key);
        var native = new InvalidOperationException("x", new DllNotFoundException("e_sqlite3"));
        Assert.Equal(DataKeys.DriverNotInstalled, DatabaseErrorMapper.Map(DatabaseProvider.Sqlite, native, true, CancellationToken.None, false, null, null).Key);
        var to = new InvalidOperationException("x", new TimeoutException());
        Assert.Equal(DataKeys.Timeout, DatabaseErrorMapper.Map(DatabaseProvider.MySql, to, true, CancellationToken.None, false, null, null).Key);
    }

    [Fact]
    public void ErrorDetail_NeverContainsThePasswordOrConnectionString()
    {
        const string pw = "Sup3rSecret!";
        const string cs = "Host=h;Username=u;Password=Sup3rSecret!";
        var boom = new InvalidOperationException($"Failed with {cs} because {pw} Password=Sup3rSecret! pwd=other");
        var ex = DatabaseErrorMapper.Map(DatabaseProvider.PostgreSql, boom, true, CancellationToken.None, false, pw, cs);
        Assert.NotNull(ex.Detail);
        Assert.DoesNotContain("Sup3rSecret", ex.Detail);
        Assert.DoesNotContain("other", ex.Detail);
        Assert.DoesNotContain("Sup3rSecret", ex.Message);
        Assert.DoesNotContain("Sup3rSecret", ex.ToString());
    }

    [Fact]
    public void DatabaseException_PassesThroughUnchanged_AndMessageIsKeyOnly()
    {
        var original = new DatabaseException(DataKeys.QueryRejected, DataKeys.RejectNotSelect);
        Assert.Same(original, DatabaseErrorMapper.Map(DatabaseProvider.Sqlite, original, false, CancellationToken.None, false, null, null));
        Assert.Equal("Data.Error.QueryRejected (Data.Query.Rejected.NotSelect)", original.Message);
    }
}
