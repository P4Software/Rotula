using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using MySqlConnector;
using Npgsql;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>
/// The fixes that came out of running the engine against real PostgreSQL and MariaDB servers, tested without a server: the error
/// classification of driver exception chains, the wire-format decoders for awkward PostgreSQL values (the byte vectors were captured from a
/// real PostgreSQL 18.6), the MariaDB value rules, and the login-reset retry against a fake server that behaves like the real one.
/// </summary>
public class DataEngineFixTests
{
    private static DatabaseException MapIt(DatabaseProvider p, Exception ex, bool connecting, bool hasParameters = false)
        => DatabaseErrorMapper.Map(p, ex, connecting, CancellationToken.None, false, null, null, hasParameters);

    /// <summary>MySqlConnector keeps its error-code constructors internal, so the exception is built reflectively.</summary>
    private static MySqlException MySql(int code, Exception? inner = null)
    {
        var ctors = typeof(MySqlException).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(MySqlErrorCode));
        var ctor = (inner is null ? ctors.OrderBy(c => c.GetParameters().Length) : ctors.Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(Exception))))
            .First();
        var args = ctor.GetParameters().Select(p =>
            p.ParameterType == typeof(MySqlErrorCode) ? (object)(MySqlErrorCode)code
            : p.ParameterType == typeof(string) ? "message"
            : p.ParameterType == typeof(Exception) ? inner
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        return (MySqlException)ctor.Invoke(args);
    }

    // ---------------------------------------------------------------- MariaDB error chains

    [Fact]
    public void MySql_CommandTimeoutExpired_IsATimeout()
        => Assert.Equal(DataKeys.Timeout, MapIt(DatabaseProvider.MySql, MySql(-1), false).Key);

    [Fact]
    public void MySql_CommandTimeoutAroundQueryInterrupted_IsATimeout()
        => Assert.Equal(DataKeys.Timeout, MapIt(DatabaseProvider.MySql, MySql(-1, MySql(1317)), false).Key);

    [Fact]
    public void MySql_ConnectTimeout_IsATimeoutNotAnUnreachableServer()
    {
        // 1042 "Connect Timeout expired" > -1 "The Command Timeout expired" > SocketException(OperationAborted)
        var chain = MySql(1042, MySql(-1, new SocketException((int)SocketError.OperationAborted)));
        Assert.Equal(DataKeys.Timeout, MapIt(DatabaseProvider.MySql, chain, true).Key);
    }

    [Fact]
    public void MySql_SlowRejectedLogin_IsAnAuthenticationFailure()
    {
        // 1042 "Connect Timeout expired" around 1045 "Access denied": the refusal inside wins over the wrapper.
        var chain = MySql(1042, MySql(1045));
        Assert.Equal(DataKeys.AuthenticationFailed, MapIt(DatabaseProvider.MySql, chain, true).Key);
    }

    [Fact]
    public void MySql_TlsRejection_IsACertificateProblemNotAnUnreachableServer()
    {
        // 1042 "SSL Authentication Error" with the AuthenticationException of the failed handshake inside.
        var chain = MySql(1042, new AuthenticationException("The remote certificate is invalid"));
        Assert.Equal(DataKeys.CertificateNotTrusted, MapIt(DatabaseProvider.MySql, chain, true).Key);
        var wrapped = MySql(1042, MySql(1042, new IOException("handshake", new AuthenticationException("RemoteCertificateChainErrors"))));
        Assert.Equal(DataKeys.CertificateNotTrusted, MapIt(DatabaseProvider.MySql, wrapped, true).Key);
    }

    [Fact]
    public void MySql_ClosedPortAndRefusal_StayUnreachable()
    {
        Assert.Equal(DataKeys.HostUnreachable, MapIt(DatabaseProvider.MySql, MySql(1042), true).Key);
        var refused = MySql(1042, MySql(1042, new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Equal(DataKeys.HostUnreachable, MapIt(DatabaseProvider.MySql, refused, true).Key);
    }

    [Fact]
    public void MySql_ResetDuringLogin_IsAProbableAuthenticationFailure()
    {
        // The server resets the connection of an unknown user; the driver retries until the connect timeout and reports 1042 around the reset.
        var reset = new IOException("Unable to write data to the transport connection", new SocketException((int)SocketError.ConnectionReset));
        var ex = MapIt(DatabaseProvider.MySql, MySql(1042, MySql(1042, reset)), true);
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.Equal(DataKeys.AuthenticationProbable, ex.ReasonKey);
    }

    // ---------------------------------------------------------------- PostgreSQL error chains

    [Fact]
    public void PostgreSql_ResetDuringLogin_IsAProbableAuthenticationFailure()
    {
        var reset = new NpgsqlException("Exception while reading from stream", new IOException("read", new SocketException((int)SocketError.ConnectionReset)));
        var ex = MapIt(DatabaseProvider.PostgreSql, reset, true);
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.Equal(DataKeys.AuthenticationProbable, ex.ReasonKey);
        var closed = new NpgsqlException("Exception while reading from stream", new EndOfStreamException());
        Assert.Equal(DataKeys.AuthenticationProbable, MapIt(DatabaseProvider.PostgreSql, closed, true).ReasonKey);
    }

    [Fact]
    public void PostgreSql_RefusedConnectionAndResetOfARunningQuery_AreNotLoginRefusals()
    {
        var refused = new NpgsqlException("Failed to connect", new SocketException((int)SocketError.ConnectionRefused));
        Assert.Equal(DataKeys.HostUnreachable, MapIt(DatabaseProvider.PostgreSql, refused, true).Key);
        var reset = new NpgsqlException("Exception while reading from stream", new IOException("read", new SocketException((int)SocketError.ConnectionReset)));
        var query = MapIt(DatabaseProvider.PostgreSql, reset, false);
        Assert.Equal(DataKeys.ConnectionFailed, query.Key);
        Assert.Null(query.ReasonKey);
    }

    [Fact]
    public void ResetDuringLogin_OfEnginesWithoutThatBehaviour_StaysUnreachable()
    {
        var reset = new InvalidOperationException("x", new SocketException((int)SocketError.ConnectionReset));
        Assert.Equal(DataKeys.HostUnreachable, MapIt(DatabaseProvider.SqlServer, reset, true).Key);
        Assert.Equal(DataKeys.HostUnreachable, MapIt(DatabaseProvider.Odbc, reset, true).Key);
    }

    [Fact]
    public void PostgreSql_ParameterTypeProblems_GetTheirOwnKeysWithAHint()
    {
        var unknown = new PostgresException("could not determine data type of parameter $1", "ERROR", "ERROR", "42P18");
        var ex = MapIt(DatabaseProvider.PostgreSql, unknown, false, hasParameters: true);
        Assert.Equal(DataKeys.ParameterTypeUnknown, ex.Key);
        Assert.Equal(DataKeys.ParameterTypeUnknownHint, ex.ReasonKey);
        Assert.Equal(DataKeys.ParameterTypeUnknown, MapIt(DatabaseProvider.PostgreSql, new PostgresException("inconsistent types", "ERROR", "ERROR", "42P08"), false).Key);

        var mismatch = new PostgresException("operator does not exist: integer = text", "ERROR", "ERROR", "42883");
        var withParameters = MapIt(DatabaseProvider.PostgreSql, mismatch, false, hasParameters: true);
        Assert.Equal(DataKeys.ParameterTypeMismatch, withParameters.Key);
        Assert.Equal(DataKeys.ParameterTypeMismatchHint, withParameters.ReasonKey);
        // Without parameters 42883 is an unknown function name: an ordinary failed query.
        Assert.Equal(DataKeys.QueryFailed, MapIt(DatabaseProvider.PostgreSql, mismatch, false, hasParameters: false).Key);
    }

    // ---------------------------------------------------------------- PostgreSQL wire formats (bytes captured from a real server)

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Theory]
    [InlineData("00000000 C000 0000", "NaN")]
    [InlineData("00000000 D000 0020", "Infinity")]
    [InlineData("00000000 F000 0000", "-Infinity")]
    [InlineData("0001 0007 0000 0000 0064", "1000000000000000000000000000000")]
    [InlineData("0002 0000 0000 0002 000C 1388", "12.50")]
    [InlineData("0001 0000 4000 0002 0001", "-1.00")]
    [InlineData("0000 0000 0000 0000", "0")]
    [InlineData("0000 0000 0000 0003", "0.000")]
    [InlineData("000A 0009 0000 0000 007B 11D7 22C5 0929 1A85 007B 11D7 22C5 0929 1A85", "123456789012345678901234567890123456789")]
    [InlineData("000A FFFF 0000 0025 04D2 162E 2334 0D80 1ED2 04D2 162E 2334 0D80 1B58", "0.1234567890123456789012345678901234567")]
    [InlineData("0001 FFFF 4000 0003 0032", "-0.005")]
    public void Numeric_IsDecodedDigitForDigit(string hex, string expected)
        => Assert.Equal(expected, PostgresValueDecoder.FormatNumeric(Hex(hex)));

    [Theory]
    [InlineData("000000036C966EE0 00000003 0000000E", "1 year 2 mons 3 days 04:05:06.7")]
    [InlineData("0000000000000000 00000000 00000000", "00:00:00")]
    [InlineData("0000000000000000 00000001 00000000", "1 day")]
    [InlineData("0000000000000000 00000000 0000000C", "1 year")]
    [InlineData("0000000000000000 FFFFFFFF 00000000", "-1 days")]
    [InlineData("00000015CAFEA800 00000000 00000000", "26:00:00")]
    public void Interval_IsWrittenLikePostgreSqlPrintsIt(string hex, string expected)
        => Assert.Equal(expected, PostgresValueDecoder.FormatInterval(Hex(hex)));

    [Fact]
    public void Interval_NegativeTime_KeepsItsSign()
    {
        var raw = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(raw, -5_400_000_000L);   // -1 h 30 min
        Assert.Equal("-01:30:00", PostgresValueDecoder.FormatInterval(raw));
    }

    [Theory]
    [InlineData("7FFFFFFF", "infinity")]
    [InlineData("80000000", "-infinity")]
    [InlineData("FFE99446", "2024-01-01 BC")]
    [InlineData("00000000", null)]                     // 2000-01-01: an ordinary date, left to the shared path
    [InlineData("FFFFFFFF", null)]                     // 1999-12-31
    public void Date_HandlesInfinityAndBc(string hex, string? expected)
        => Assert.Equal(expected, PostgresValueDecoder.FormatDate(Hex(hex)));

    [Theory]
    [InlineData("8000000000000000", false, "-infinity")]
    [InlineData("7FFFFFFFFFFFFFFF", false, "infinity")]
    [InlineData("7FFFFFFFFFFFFFFF", true, "infinity")]
    [InlineData("FF1AF9E8FB46D000", false, "0044-03-15 12:00:00 BC")]
    [InlineData("FF1AF9E8FB46D000", true, "0044-03-15 12:00:00Z BC")]
    [InlineData("0000000000000000", false, null)]
    [InlineData("0002B5E7C4F6C200", true, null)]
    public void Timestamp_HandlesInfinityAndBc(string hex, bool withZone, string? expected)
        => Assert.Equal(expected, PostgresValueDecoder.FormatTimestamp(Hex(hex), withZone));

    [Theory]
    [InlineData("000000141DD76000", "24:00:00")]
    [InlineData("0000000000000000", null)]
    [InlineData("000000087D2A5C80", null)]
    public void Time_OnlyDeviatesFor24Hours(string hex, string? expected)
        => Assert.Equal(expected, PostgresValueDecoder.FormatTime(Hex(hex)));

    [Theory]
    [InlineData("00000008AB14B780 FFFFE3E0", "10:20:30+02:00")]
    [InlineData("00000008AB14B780 00000000", "10:20:30+00:00")]
    [InlineData("00000008AB14B780 00004E20", "10:20:30-05:33:20")]
    [InlineData("00000008AB14B780 FFFFB2A8", "10:20:30+05:30")]
    public void TimeTz_KeepsItsOffsetAndHasNoDate(string hex, string expected)
        => Assert.Equal(expected, PostgresValueDecoder.FormatTimeTz(Hex(hex)));

    [Theory]
    [InlineData("00000004A0", "1010")]
    [InlineData("00000003A0", "101")]
    [InlineData("0000000180", "1")]
    [InlineData("00000009FF80", "111111111")]
    [InlineData("00000000", "")]
    public void BitStrings_AreWrittenAsZerosAndOnes(string hex, string expected)
        => Assert.Equal(expected, PostgresValueDecoder.FormatBits(Hex(hex)));

    [Theory]
    [InlineData(0, 1970, 1, 1)]
    [InlineData(10957, 2000, 1, 1)]
    [InlineData(19787, 2024, 3, 5)]
    [InlineData(-719162, 1, 1, 1)]
    [InlineData(-719163, 0, 12, 31)]        // astronomical year 0 = 1 BC
    [InlineData(2932896, 9999, 12, 31)]
    public void CivilDates_AreProlepticGregorian(long days, long year, int month, int day)
        => Assert.Equal((year, month, day), PostgresValueDecoder.CivilFromDays(days));

    // ---------------------------------------------------------------- MariaDB value rules

    [Fact]
    public void MySqlTime_OrdinaryTimes_AreUnchanged()
    {
        Assert.Equal("00:00:00", MySqlValueFormatter.TimeText(TimeSpan.Zero));
        Assert.Equal("10:20:30", MySqlValueFormatter.TimeText(new TimeSpan(10, 20, 30)));
    }

    [Fact]
    public void MySqlTime_BeyondADay_KeepsCountingHours()
    {
        Assert.Equal("838:59:59", MySqlValueFormatter.TimeText(new TimeSpan(34, 22, 59, 59)));
        Assert.Equal("-838:59:59.000001", MySqlValueFormatter.TimeText(-(new TimeSpan(34, 22, 59, 59) + TimeSpan.FromTicks(10))));
        Assert.Equal("27:46:40", MySqlValueFormatter.TimeText(TimeSpan.FromSeconds(100_000)));
        Assert.Equal("00:00:01.5", MySqlValueFormatter.TimeText(TimeSpan.FromMilliseconds(1500)));
    }

    [Fact]
    public void MySqlZeroDates_AreShownAsStored()
    {
        Assert.Equal("0000-00-00", MySqlValueFormatter.InvalidDateText(0, 0, 0, 0, 0, 0, 0));
        Assert.Equal("2024-00-05", MySqlValueFormatter.InvalidDateText(2024, 0, 5, 0, 0, 0, 0));
        Assert.Equal("2024-03-00 04:05:06", MySqlValueFormatter.InvalidDateText(2024, 3, 0, 4, 5, 6, 0));
        Assert.Equal("0000-00-00 00:00:01.25", MySqlValueFormatter.InvalidDateText(0, 0, 0, 0, 0, 1, 250000));
    }

    [Fact]
    public void MySqlConnection_KeepsZeroDatesInsteadOfConvertingThem()
    {
        var settings = new DatabaseConnectionSettings { Name = "m", Provider = DatabaseProvider.MySql, Host = "h", UserName = "u" };
        var descriptor = ProviderRegistry.Default.Resolve(settings);
        var b = new MySqlConnectionStringBuilder(descriptor.BuildConnectionString(settings, "pw"));
        Assert.True(b.AllowZeroDateTime);
        Assert.False(b.ConvertZeroDateTime);
    }

    [Fact]
    public void MariaDbTlsHelp_ExplainsTheFingerprintCheck_AndPostgreSqlKeepsTheGeneralHelp()
    {
        string? Help(DatabaseProvider p)
        {
            var settings = new DatabaseConnectionSettings { Name = "m", Provider = p, Host = "h", UserName = "u" };
            return ProviderRegistry.Default.Resolve(settings).Options.Single(o => o.Key == ProviderOptionKeys.TrustServerCertificate).HelpKey;
        }
        Assert.Equal(DataKeys.MariaDbCertificateFingerprintHelp, Help(DatabaseProvider.MySql));
        Assert.Equal("Data.Option.TrustServerCertificate.Help", Help(DatabaseProvider.PostgreSql));
    }

    // ---------------------------------------------------------------- login reset against a fake server

    /// <summary>A TCP server that resets (RST) the first <paramref name="resets"/> connections, then answers like PostgreSQL refusing the login.</summary>
    private sealed class FakePostgres : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly int _resets;
        private int _accepted;

        public FakePostgres(int resets)
        {
            _resets = resets;
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Accepted => Volatile.Read(ref _accepted);

        private async Task AcceptLoop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    int number = Interlocked.Increment(ref _accepted);
                    _ = Task.Run(() => Serve(client, number));
                }
            }
            catch (Exception) { /* stopped */ }
        }

        private async Task Serve(TcpClient client, int number)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var head = new byte[8];
                    await stream.ReadExactlyAsync(head);
                    if (number <= _resets)
                    {
                        // Linger 0 turns Close into a reset, the way the real server drops a connection it has just refused.
                        client.Client.LingerState = new LingerOption(true, 0);
                        return;
                    }
                    bool sslRequest = head[4] == 0x04 && head[5] == 0xD2 && head[6] == 0x16 && head[7] == 0x2F;   // code 80877103
                    if (sslRequest)
                    {
                        // Decline TLS, then read the real startup packet (its first four bytes are its own length).
                        await stream.WriteAsync(new[] { (byte)'N' });
                        await stream.ReadExactlyAsync(head.AsMemory(0, 4));
                        await stream.ReadExactlyAsync(new byte[PacketLength(head) - 4]);
                    }
                    else
                    {
                        await stream.ReadExactlyAsync(new byte[PacketLength(head) - 8]);
                    }

                    var body = new List<byte>();
                    void Field(char code, string text) { body.Add((byte)code); body.AddRange(Encoding.UTF8.GetBytes(text)); body.Add(0); }
                    Field('S', "FATAL"); Field('V', "FATAL"); Field('C', "28P01"); Field('M', "password authentication failed for user \"u\"");
                    body.Add(0);
                    var message = new List<byte> { (byte)'E' };
                    int total = body.Count + 4;
                    message.AddRange(new[] { (byte)(total >> 24), (byte)(total >> 16), (byte)(total >> 8), (byte)total });
                    message.AddRange(body);
                    await stream.WriteAsync(message.ToArray());
                    await stream.FlushAsync();
                }
            }
            catch (Exception) { /* the client went away */ }
        }

        private static int PacketLength(byte[] head) => (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    private static DatabaseConnectionSettings PgSettings(int port) => new()
    {
        Name = "fake", Provider = DatabaseProvider.PostgreSql, Host = "127.0.0.1", Port = port, Database = "d", UserName = "u",
        Authentication = DatabaseAuthentication.UserPassword, Encrypt = false, ConnectTimeoutSeconds = 5, CommandTimeoutSeconds = 5,
    };

    /// <summary>
    /// How many TCP connections ONE connect attempt of the bare driver makes against a server that behaves like <paramref name="resets"/>
    /// (Npgsql itself may try a second, plain connection after a failed one when TLS is only "preferred"). The product's counts are judged
    /// against this baseline so the test does not depend on that driver detail.
    /// </summary>
    private static async Task<int> DriverAttemptsAsync(int resets)
    {
        using var server = new FakePostgres(resets);
        var settings = PgSettings(server.Port);
        await using var conn = new NpgsqlConnection(ProviderRegistry.Default.Resolve(settings).BuildConnectionString(settings, "pw"));
        await Assert.ThrowsAnyAsync<Exception>(() => conn.OpenAsync());
        return server.Accepted;
    }

    [Fact]
    public async Task PostgreSql_AResetOnTheFirstLogin_IsRetriedOnceAndTheServersRefusalIsReported()
    {
        int perAttempt = await DriverAttemptsAsync(resets: 1000);
        // The whole first attempt (every connection the driver makes for it) is reset; the second one meets the real refusal.
        using var server = new FakePostgres(resets: perAttempt);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseSource.Create(PgSettings(server.Port), "pw").TestConnectionAsync());
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.Null(ex.ReasonKey);                       // the server's own answer arrived on the second try
        Assert.Contains("28P01", ex.Detail ?? "", StringComparison.Ordinal);
        Assert.True(server.Accepted > perAttempt, "the product did not retry: " + server.Accepted + " connections, " + perAttempt + " per attempt");
    }

    [Fact]
    public async Task PostgreSql_ResetsOnBothTries_AreAProbableAuthenticationFailureAfterExactlyOneRetry()
    {
        int perAttempt = await DriverAttemptsAsync(resets: 1000);
        using var server = new FakePostgres(resets: 1000);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseSource.Create(PgSettings(server.Port), "pw").TestConnectionAsync());
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.Equal(DataKeys.AuthenticationProbable, ex.ReasonKey);
        Assert.Equal(2 * perAttempt, server.Accepted);
    }

    [Fact]
    public async Task PostgreSql_ARefusalWithoutAReset_IsNotRetried()
    {
        int baseline = await DriverAttemptsAsync(resets: 0);
        using var server = new FakePostgres(resets: 0);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseSource.Create(PgSettings(server.Port), "pw").TestConnectionAsync());
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.Null(ex.ReasonKey);
        Assert.Equal(baseline, server.Accepted);
    }

    [Fact]
    public async Task MySql_AServerThatKeepsResettingTheLogin_IsAProbableAuthenticationFailure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Close();
                }
            }
            catch (Exception) { /* stopped */ }
        });
        try
        {
            var settings = new DatabaseConnectionSettings
            {
                Name = "fake", Provider = DatabaseProvider.MySql, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, UserName = "u",
                Authentication = DatabaseAuthentication.UserPassword, Encrypt = false, ConnectTimeoutSeconds = 2, CommandTimeoutSeconds = 5,
            };
            var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseSource.Create(settings, "pw").TestConnectionAsync());
            Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
            Assert.Equal(DataKeys.AuthenticationProbable, ex.ReasonKey);
        }
        finally { stop.Cancel(); listener.Stop(); }
    }
}
