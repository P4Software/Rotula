using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit.Abstractions;

namespace Rotula.Data.Integration;

/// <summary>
/// The test suite every engine must pass, written once and run against each real server. The engine classes supply the few
/// SQL texts that differ (series generators, sleep, null-safe equality) and the expected catalogue.
/// </summary>
[Collection(RealServersCollection.Name)]
public abstract class ServerSuite
{
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the suite instance for one test.</summary>
    protected ServerSuite(ITestOutputHelper output) => _output = output;

    // ---------------------------------------------------------------- what an engine must tell us

    internal abstract string Engine { get; }
    internal abstract DatabaseProvider Provider { get; }
    internal abstract ProviderExtras BaseExtras { get; }
    internal abstract string DatabaseName { get; }
    internal abstract string WriterUser { get; }
    internal abstract string WriterPassword { get; }
    internal abstract Task SeedAsync();

    /// <summary>Opens a raw driver connection as the administrator (to check effects and to count server-side activity).</summary>
    internal abstract Task<DbConnection> OpenAdminAsync(string? database);

    /// <summary>SQL with a null-safe comparison of <paramref name="column"/> to <paramref name="parameter"/> (a null parameter must match NULL).</summary>
    internal abstract string NullSafeEquals(string column, string parameter);

    /// <summary>One column <c>n</c> with the numbers 1 to <paramref name="count"/>.</summary>
    internal abstract string NumbersSql(int count);

    /// <summary>Two columns: <c>n</c> and a 232-character <c>pad</c>.</summary>
    internal abstract string WideRowsSql(long count);

    /// <summary>A query that sleeps for <paramref name="seconds"/> and carries a recognisable marker in its text.</summary>
    internal abstract string SleepSql(int seconds);

    /// <summary>How many sessions are currently running <see cref="SleepSql"/> with this duration (seen by the administrator).</summary>
    internal abstract Task<int> RunningSleepersAsync(int seconds);

    /// <summary>A text describing what the data looks like (row counts, sequences, objects) so a test can prove nothing changed.</summary>
    internal abstract Task<string> FingerprintAsync();

    /// <summary>SQL returning one row, one column: whether the session is encrypted ("1") or not ("0").</summary>
    internal abstract string IsEncryptedSql { get; }

    /// <summary>Statements that must fail inside the read-only session even though the guard is bypassed: (label, statement).</summary>
    internal abstract IReadOnlyList<(string Label, string Sql)> WriteAttempts { get; }

    /// <summary>Objects the reader account must see: (schema, name, kind).</summary>
    internal abstract IReadOnlyList<DatabaseObject> ExpectedReaderObjects { get; }

    /// <summary>Names the reader must NOT see (no privilege), and schemas that must never appear.</summary>
    internal abstract IReadOnlyList<string> HiddenFromReader { get; }

    internal abstract IReadOnlyList<string> ForbiddenSchemas { get; }

    /// <summary>True when the exception is the server's own "read-only transaction" refusal (and not a privilege, name or syntax problem).</summary>
    internal abstract bool IsReadOnlyRefusal(Exception error);

    /// <summary>A SELECT of a table the reader has no privilege on.</summary>
    internal abstract string SecretSelectSql { get; }

    /// <summary>Account whose connection can only be protected by the certificate check (no password the server could vouch with).</summary>
    internal virtual (string User, string Password) TlsRefusalAccount => (Info.ReaderUser, Info.ReaderPassword);

    /// <summary>How the "seen" column (2024-03-01 10:20:30 UTC) is shown: a PostgreSQL timestamptz carries the UTC marker, a MariaDB DATETIME has no zone.</summary>
    internal virtual string SeenText => "2024-03-01 10:20:30";

    /// <summary>Optional-filter SQL as a user would write it for this engine: a null parameter means "no filter".</summary>
    internal abstract string OptionalFilterSql { get; }

    /// <summary>Database to connect to when listing as the administrator (null = the test database).</summary>
    internal virtual string? AdminListDatabase => null;

    /// <summary>The qualified name of the order table (<c>sales.orders</c> or <c>sales_orders</c>).</summary>
    internal abstract string OrdersTable { get; }

    // ---------------------------------------------------------------- helpers

    internal ServerInfo Info => ServerCatalog.Get(Engine)!;

    /// <summary>Writes a diagnostic line that shows up in the test report.</summary>
    protected void Log(string message) => _output.WriteLine(message);

    /// <summary>Makes sure the data exists, then returns.</summary>
    protected Task ReadyAsync() => SeedAsync();

    internal string PasswordOf(string user)
        => user == Info.ReaderUser ? Info.ReaderPassword : user == Info.AdminUser ? Info.AdminPassword : user == WriterUser ? WriterPassword : "not-the-password";

    /// <summary>Settings for the reader account over an encrypted connection that trusts the self-signed certificate, with overrides.</summary>
    internal DatabaseConnectionSettings Make(string? user = null, string? database = null, bool encrypt = true, bool trust = true,
        string? ca = null, int? port = null, int command = 30, int connect = 10, ProviderExtras? extras = null, string? host = null)
        => new()
        {
            Name = "it-" + Engine, Provider = Provider, Host = host ?? Info.Host, Port = port ?? Info.Port, Database = database ?? DatabaseName,
            Authentication = DatabaseAuthentication.UserPassword, UserName = user ?? Info.ReaderUser,
            Encrypt = encrypt, TrustServerCertificate = trust, CaCertificatePath = ca, Extras = extras ?? BaseExtras,
            CommandTimeoutSeconds = command, ConnectTimeoutSeconds = connect,
        };

    internal IDatabaseSource Open(DatabaseConnectionSettings? settings = null, string? password = null)
    {
        settings ??= Make();
        return DatabaseSource.Create(settings, password ?? PasswordOf(settings.UserName!));
    }

    internal static async Task<DatabaseException> Fails(Func<Task> action)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(action);
        return Assert.IsType<DatabaseException>(ex);
    }

    private static string First(DatabaseQueryResult r) => r.Table.Rows[0][0];

    private static IEnumerable<string> Column(DatabaseQueryResult r, int i = 0) => r.Table.Rows.Select(x => x[i]);

    // ---------------------------------------------------------------- connecting, TLS

    /// <summary>User and password connect.</summary>
    [RealFact]
    public async Task Connects_with_user_and_password()
    {
        await ReadyAsync();
        await Open().TestConnectionAsync();
        var r = await Open().QueryAsync("SELECT 1 AS one");
        Assert.Equal("1", First(r));
    }

    /// <summary>The default secure settings must refuse a server whose certificate nobody vouches for.</summary>
    [RealFact]
    public async Task Default_secure_settings_refuse_a_self_signed_server()
    {
        await ReadyAsync();
        var (user, password) = TlsRefusalAccount;
        var ex = await Fails(() => Open(Make(user: user, trust: false), password).TestConnectionAsync());
        Log(ex.Key + " | " + ex.Detail);
        Assert.Equal(DataKeys.CertificateNotTrusted, ex.Key);
    }

    /// <summary>Explicit trust connects, and the session really is encrypted.</summary>
    [RealFact]
    public async Task Explicit_trust_connects_over_an_encrypted_session()
    {
        await ReadyAsync();
        var r = await Open(Make(trust: true)).QueryAsync(IsEncryptedSql);
        Assert.Equal("1", First(r));
    }

    /// <summary>Handing the server certificate over as the certificate authority verifies the chain and the host name, without blind trust.</summary>
    [RealFact]
    public async Task The_server_certificate_as_authority_connects_with_full_verification()
    {
        await ReadyAsync();
        var r = await Open(Make(trust: false, ca: Info.CaCertificate)).QueryAsync(IsEncryptedSql);
        Assert.Equal("1", First(r));
    }

    /// <summary>A certificate authority file that is not there is the user's typo, not a network problem.</summary>
    [RealFact]
    public async Task A_missing_authority_file_is_reported_as_invalid_settings()
    {
        await ReadyAsync();
        var ex = await Fails(() => Open(Make(trust: false, ca: Path.Combine(Path.GetTempPath(), "no-such-file.pem"))).TestConnectionAsync());
        Assert.Equal(DataKeys.InvalidSettings, ex.Key);
    }

    /// <summary>Turning encryption off (trusted local server) still connects.</summary>
    [RealFact]
    public async Task Encryption_can_be_turned_off_for_a_trusted_local_server()
    {
        await ReadyAsync();
        await Open(Make(encrypt: false)).TestConnectionAsync();
    }

    // ---------------------------------------------------------------- errors with real server answers

    /// <summary>A wrong password is an authentication failure and never leaks the password.</summary>
    [RealFact]
    public async Task A_wrong_password_is_an_authentication_failure_that_does_not_leak_secrets()
    {
        await ReadyAsync();
        const string secret = "Sup3r-Secret-Wrong-Password";
        var ex = await Fails(() => Open(Make(), secret).TestConnectionAsync());
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.DoesNotContain(secret, ex.Message);
        Assert.DoesNotContain(secret, ex.Detail ?? "");
        Assert.DoesNotContain(secret, ex.ToString());
        Assert.DoesNotContain(Info.ReaderPassword, ex.ToString());
    }

    /// <summary>An account that does not exist is also an authentication failure.</summary>
    [RealFact]
    public async Task An_unknown_user_is_an_authentication_failure()
    {
        await ReadyAsync();
        var ex = await Fails(() => Open(Make(user: "nobody_here"), "whatever").TestConnectionAsync());
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
    }

    /// <summary>Whether a login with an unknown user name is answered promptly by this server (MariaDB on Windows resets the connection).</summary>
    internal virtual bool UnknownUserIsAnsweredPromptly => true;

    /// <summary>
    /// Repeats the failed logins: a rejected password must ALWAYS be reported as an authentication failure. The server closes the
    /// connection right after its refusal, and the driver sometimes reports that reset instead of the refusal itself.
    /// </summary>
    [RealFact]
    public async Task Rejected_logins_are_always_reported_as_authentication_failures()
    {
        await ReadyAsync();
        var odd = new List<string>();
        for (int i = 0; i < 25; i++)
        {
            foreach (var (user, password) in new[] { (Info.ReaderUser, "wrong-" + i), UnknownUserIsAnsweredPromptly ? ("nobody_here", "x") : (Info.ReaderUser, "y") })
            {
                var ex = await Fails(() => Open(Make(user: user), password).TestConnectionAsync());
                if (ex.Key != DataKeys.AuthenticationFailed) odd.Add($"{user} attempt {i}: {ex.Key} | {ex.Detail}");
            }
        }
        Assert.True(odd.Count == 0, odd.Count + " of 50 rejected logins were mis-reported: " + string.Join(" || ", odd.Take(5)));
    }

    /// <summary>A database that does not exist.</summary>
    [RealFact]
    public async Task An_unknown_database_is_reported_as_such()
    {
        await ReadyAsync();
        // As the administrator: a MariaDB account without rights on the database is told "access denied", never "unknown database",
        // so that the server does not reveal which databases exist.
        var ex = await Fails(() => Open(Make(user: Info.AdminUser, database: "no_such_database")).TestConnectionAsync());
        Log(ex.Key + " | " + ex.Detail);
        Assert.Equal(DataKeys.DatabaseNotFound, ex.Key);
    }

    /// <summary>Nobody listens on the port.</summary>
    [RealFact]
    public async Task A_closed_port_is_reported_as_unreachable_quickly()
    {
        await ReadyAsync();
        int closed = FreePort();
        var sw = Stopwatch.StartNew();
        var ex = await Fails(() => Open(Make(port: closed, connect: 8)).TestConnectionAsync());
        Log(ex.Key + " | " + ex.Detail + " | " + sw.Elapsed);
        Assert.Equal(DataKeys.HostUnreachable, ex.Key);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "connection refused must not wait for the timeout");
    }

    /// <summary>A server that accepts the connection and never speaks.</summary>
    [RealFact]
    public async Task A_silent_server_ends_in_a_timeout_not_a_hang()
    {
        await ReadyAsync();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Accept and hold: the client connects at TCP level and then waits for a greeting that never comes.
            var held = new List<TcpClient>();
            using var stop = new CancellationTokenSource();
            var acceptor = Task.Run(async () =>
            {
                try { while (!stop.IsCancellationRequested) held.Add(await listener.AcceptTcpClientAsync(stop.Token)); }
                catch (OperationCanceledException) { }
            });
            var sw = Stopwatch.StartNew();
            var ex = await Fails(() => Open(Make(port: port, connect: 2, command: 2)).TestConnectionAsync());
            Log(ex.Key + " | " + ex.Detail + " | " + sw.Elapsed);
            stop.Cancel();
            await acceptor;
            foreach (var c in held) c.Dispose();
            Assert.Equal(DataKeys.Timeout, ex.Key);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        }
        finally { listener.Stop(); }
    }

    /// <summary>The server's own syntax error comes back as a failed query with the first line of the message.</summary>
    [RealFact]
    public async Task A_syntax_error_is_a_failed_query_with_a_readable_detail()
    {
        await ReadyAsync();
        var ex = await Fails(() => Open().QueryAsync("SELECT * FORM customers"));
        Log(ex.Key + " | " + ex.Detail);
        Assert.Equal(DataKeys.QueryFailed, ex.Key);
        Assert.False(string.IsNullOrWhiteSpace(ex.Detail));
        Assert.DoesNotContain(Info.ReaderPassword, ex.ToString());
    }

    /// <summary>A table the account has no right to read.</summary>
    [RealFact]
    public async Task Reading_a_table_without_the_privilege_is_permission_denied()
    {
        await ReadyAsync();
        var ex = await Fails(() => Open().QueryAsync(SecretSelectSql));
        Log(ex.Key + " | " + ex.Detail);
        Assert.Equal(DataKeys.PermissionDenied, ex.Key);
    }

    /// <summary>A table that does not exist is a failed query, not a crash.</summary>
    [RealFact]
    public async Task An_unknown_table_is_a_failed_query()
    {
        await ReadyAsync();
        var ex = await Fails(() => Open().QueryAsync("SELECT * FROM no_such_table"));
        Assert.Equal(DataKeys.QueryFailed, ex.Key);
    }

    // ---------------------------------------------------------------- catalogue

    /// <summary>The reader sees what it may read; system schemas never appear; the list is sorted.</summary>
    [RealFact]
    public async Task ListTables_returns_tables_and_views_without_system_objects()
    {
        await ReadyAsync();
        var list = await Open().ListTablesAsync();
        Log(string.Join("; ", list.Select(o => o.QualifiedName + " (" + o.Kind + ")")));
        foreach (var expected in ExpectedReaderObjects)
            Assert.Contains(expected, list);
        foreach (string hidden in HiddenFromReader)
            Assert.DoesNotContain(list, o => o.Name == hidden);
        foreach (string schema in ForbiddenSchemas)
            Assert.DoesNotContain(list, o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
        var sorted = list.OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted.Select(o => o.QualifiedName), list.Select(o => o.QualifiedName));
    }

    /// <summary>The administrator also sees the tables the reader may not read, still without system objects.</summary>
    [RealFact]
    public async Task ListTables_for_the_administrator_adds_the_private_tables_but_no_system_objects()
    {
        await ReadyAsync();
        var list = await Open(Make(user: Info.AdminUser, database: AdminListDatabase)).ListTablesAsync();
        Assert.Contains(list, o => o.Name == "secret_notes");
        Assert.Contains(list, o => o.Name == "audit");
        foreach (string schema in ForbiddenSchemas)
            Assert.DoesNotContain(list, o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Column names, order, nullability and type names of a table, a view and a name that contains a dot.</summary>
    [RealFact]
    public async Task Describe_returns_columns_for_tables_views_and_quoted_names()
    {
        await ReadyAsync();
        var src = Open();
        var cols = await src.DescribeAsync("customers");
        Assert.Equal(new[] { "id", "name", "city", "credit", "born", "active", "token", "payload", "data", "created", "seen" }, cols.Select(c => c.Name));
        Assert.Equal(Enumerable.Range(0, cols.Count), cols.Select(c => c.Ordinal));
        Assert.All(cols, c => Assert.False(string.IsNullOrWhiteSpace(c.DataType)));
        Log(string.Join(", ", cols.Select(c => c.Name + ":" + c.DataType + ":" + c.AllowsNull)));

        var view = await src.DescribeAsync(new DatabaseObject(null, "customers_v", DatabaseObjectKind.View));
        Assert.Equal(new[] { "id", "name" }, view.Select(c => c.Name));

        var odd = await src.DescribeAsync(src.QuoteForDescribe("odd.name"));
        Assert.Equal(new[] { "id", "Mixed Case" }, odd.Select(c => c.Name));

        var orders = await src.DescribeAsync(OrdersTable);
        Assert.Equal(3, orders.Count);
    }

    /// <summary>NOT NULL columns are reported as such.</summary>
    [RealFact]
    public async Task Describe_reports_which_columns_allow_null()
    {
        await ReadyAsync();
        var cols = await Open().DescribeAsync("customers");
        Assert.False(cols.Single(c => c.Name == "id").AllowsNull);
        Assert.False(cols.Single(c => c.Name == "name").AllowsNull);
        Assert.True(cols.Single(c => c.Name == "city").AllowsNull);
    }

    /// <summary>Describing a table that is not there, or a hostile name, never runs anything extra.</summary>
    [RealFact]
    public async Task Describe_rejects_unknown_and_hostile_names_safely()
    {
        await ReadyAsync();
        var src = Open();
        var before = await FingerprintAsync();
        var ex = await Fails(() => src.DescribeAsync("no_such_table"));
        Assert.Equal(DataKeys.QueryFailed, ex.Key);
        var hostile = await Fails(() => src.DescribeAsync("customers; DROP TABLE victim; --"));
        Assert.Equal(DataKeys.QueryFailed, hostile.Key);
        var bad = await Fails(() => src.DescribeAsync("a.b.c"));
        Assert.Equal(DataKeys.InvalidSettings, bad.Key);
        Assert.Equal(before, await FingerprintAsync());
    }

    /// <summary>Preview quotes the name itself and caps the rows.</summary>
    [RealFact]
    public async Task Preview_reads_the_first_rows_of_a_table_and_a_view()
    {
        await ReadyAsync();
        var src = Open();
        var tables = await src.ListTablesAsync();
        var customers = tables.Single(o => o.Name == "customers");
        var r = await src.PreviewAsync(customers, 3);
        Assert.Equal(3, r.Table.RowCount);
        Assert.True(r.Truncated);
        Assert.Equal(3, r.MaxRows);
        var all = await src.PreviewAsync(customers, 100);
        Assert.Equal(6, all.Table.RowCount);
        Assert.False(all.Truncated);
        var odd = tables.Single(o => o.Name == "odd.name");
        Assert.Equal("dot in the table name", (await src.PreviewAsync(odd)).Table.Rows[0][1]);
        var view = tables.Single(o => o.Name == "customers_v");
        Assert.Equal(3, (await src.PreviewAsync(view)).Table.RowCount);
    }

    // ---------------------------------------------------------------- parameters and unicode

    /// <summary>Text parameter (with accents), number, date, boolean, repeated name, any case, with or without @.</summary>
    [RealFact]
    public async Task Named_parameters_bind_text_numbers_dates_and_booleans()
    {
        await ReadyAsync();
        var src = Open();
        var byName = await src.QueryAsync("SELECT id FROM customers WHERE name = @name", new Dictionary<string, object?> { ["name"] = "José Muñoz" });
        Assert.Equal(new[] { "2" }, Column(byName));

        var byNumber = await src.QueryAsync("SELECT id FROM customers WHERE credit > @min ORDER BY id", new Dictionary<string, object?> { ["min"] = 100m });
        Assert.Equal(new[] { "1", "4" }, Column(byNumber));

        var byInt = await src.QueryAsync("SELECT id FROM customers WHERE id >= @a AND id < @b ORDER BY id", new Dictionary<string, object?> { ["a"] = 2, ["b"] = 4L });
        Assert.Equal(new[] { "2", "3" }, Column(byInt));

        var byDate = await src.QueryAsync("SELECT id FROM customers WHERE born = @d", new Dictionary<string, object?> { ["d"] = new DateTime(2024, 2, 29) });
        Assert.Equal(new[] { "4" }, Column(byDate));

        var byDateOnly = await src.QueryAsync("SELECT id FROM customers WHERE born = @d", new Dictionary<string, object?> { ["@D"] = new DateOnly(2000, 1, 1) });
        Assert.Equal(new[] { "3" }, Column(byDateOnly));

        var byBool = await src.QueryAsync("SELECT id FROM customers WHERE active = @a ORDER BY id", new Dictionary<string, object?> { ["a"] = true });
        Assert.Equal(new[] { "1", "3", "4" }, Column(byBool));

        var twice = await src.QueryAsync("SELECT id FROM customers WHERE id = @i OR id = @I + 1 ORDER BY id", new Dictionary<string, object?> { ["i"] = 1 });
        Assert.Equal(new[] { "1", "2" }, Column(twice));
    }

    /// <summary>A null parameter reaches the server as SQL NULL.</summary>
    [RealFact]
    public async Task A_null_parameter_matches_null_values()
    {
        await ReadyAsync();
        var src = Open();
        var r = await src.QueryAsync("SELECT id FROM customers WHERE " + NullSafeEquals("city", "@c"), new Dictionary<string, object?> { ["c"] = null });
        Assert.Equal(new[] { "5" }, Column(r));
        var plain = await src.QueryAsync("SELECT id FROM customers WHERE city = @c", new Dictionary<string, object?> { ["c"] = null });
        Assert.Empty(plain.Table.Rows);
        var v = await src.QueryAsync("SELECT id FROM customers WHERE city = @c", new Dictionary<string, object?> { ["c"] = "Dublin" });
        Assert.Equal(new[] { "6" }, Column(v));
    }

    /// <summary>A parameter that looks like an attack stays data, and a missing parameter is refused before any connection.</summary>
    [RealFact]
    public async Task Parameter_values_are_data_and_missing_parameters_are_refused()
    {
        await ReadyAsync();
        var src = Open();
        var before = await FingerprintAsync();
        var r = await src.QueryAsync("SELECT id FROM customers WHERE name = @n", new Dictionary<string, object?> { ["n"] = "x' OR '1'='1" });
        Assert.Empty(r.Table.Rows);
        // Row 6 is named exactly like an injection attempt; finding it proves the text travelled as data and nothing was executed.
        var drop = await src.QueryAsync("SELECT id FROM customers WHERE name = @n", new Dictionary<string, object?> { ["n"] = "O'Brien; DROP TABLE customers; --" });
        Assert.Equal(new[] { "6" }, Column(drop));

        var ex = await Fails(() => src.QueryAsync("SELECT id FROM customers WHERE name = @n"));
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
        Assert.Equal(DataKeys.RejectMissingParameter, ex.ReasonKey);
        Assert.Equal(before, await FingerprintAsync());

        var literal = await src.QueryAsync("SELECT '@not_a_parameter' AS lit");
        Assert.Equal("@not_a_parameter", First(literal));
    }

    /// <summary>Accents, ñ, CJK, emoji, combining marks and right-to-left controls survive storage, parameters and column names.</summary>
    [RealFact]
    public async Task Unicode_survives_stored_values_parameters_and_column_names()
    {
        await ReadyAsync();
        var src = Open();
        var rows = await src.QueryAsync("SELECT name, city FROM customers WHERE id IN (2, 3, 4) ORDER BY id");
        Assert.Equal(new[] { "José Muñoz", "你好，世界", "😀 emoji 🚀" }, Column(rows));
        Assert.Equal(new[] { "Bogotá", "東京", "Zürich" }, Column(rows, 1));

        foreach (string text in new[] { "José Muñoz", "señor niño", "你好，世界", "😀🚀 👨‍👩‍👧", "é (e + combining acute)", "‮evil‬", "tab\tand\nnewline" })
        {
            var echo = await src.QueryAsync("SELECT @s AS s", new Dictionary<string, object?> { ["s"] = text });
            Assert.Equal(text, First(echo));
        }

        var alias = await src.QueryAsync("SELECT 1 AS " + QuoteAlias("año 名前"));
        Assert.Equal("año 名前", alias.Table.Columns[0]);
    }

    /// <summary>A null parameter used the way people write optional filters: "no filter when the value is empty".</summary>
    [RealFact]
    public async Task An_optional_filter_with_a_null_parameter_works()
    {
        await ReadyAsync();
        var src = Open();
        var all = await src.QueryAsync(OptionalFilterSql, new Dictionary<string, object?> { ["x"] = null });
        Assert.Equal(6, all.Table.RowCount);
        var one = await src.QueryAsync(OptionalFilterSql, new Dictionary<string, object?> { ["x"] = "Dublin" });
        Assert.Equal(new[] { "6" }, Column(one));
    }

    // ---------------------------------------------------------------- formatting

    /// <summary>The documented text form of numbers, dates, booleans, uuid, json and binary values, as the real driver delivers them.</summary>
    [RealFact]
    public async Task Values_are_formatted_by_the_documented_rules()
    {
        await ReadyAsync();
        var src = Open();
        var r = await src.QueryAsync("SELECT credit, born, active, token, payload, data, created, seen FROM customers WHERE id = 1");
        Assert.Equal(new[]
        {
            "1234.50", "1990-05-17", "TRUE", "a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11", "{\"k\": [1, 2]}", "0xDEADBEEF",
            "2024-03-01 10:20:30.123456", SeenText,
        }, r.Table.Rows[0]);

        var nulls = await src.QueryAsync("SELECT city, credit, born, active, token, payload, data, created, seen FROM customers WHERE id = 5");
        Assert.All(nulls.Table.Rows[0], cell => Assert.Equal(string.Empty, cell));

        var midnight = await src.QueryAsync("SELECT created FROM customers WHERE id = 6");
        Assert.Equal("2024-03-01", First(midnight));
        var negative = await src.QueryAsync("SELECT credit, active FROM customers WHERE id = 6");
        Assert.Equal(new[] { "-5.25", "FALSE" }, negative.Table.Rows[0]);
    }

    /// <summary>Big single values pass through intact.</summary>
    [RealFact]
    public async Task Large_text_and_binary_values_pass_through_intact()
    {
        await ReadyAsync();
        var src = Open();
        var r = await src.QueryAsync(LargeValueSql(2_000_000));
        Assert.Equal(2_000_000, r.Table.Rows[0][0].Length);
        Assert.Equal(2 + 2 * 100_000, r.Table.Rows[0][1].Length);
    }

    internal abstract string LargeValueSql(int textLength);

    internal abstract string QuoteAlias(string alias);

    // ---------------------------------------------------------------- row cap

    /// <summary>The cap stops the read at one row more than allowed and says so.</summary>
    [RealFact]
    public async Task The_row_cap_truncates_and_exactly_at_the_cap_does_not()
    {
        await ReadyAsync();
        var src = Open();
        var cut = await src.QueryAsync(NumbersSql(500), null, 100);
        Assert.True(cut.Truncated);
        Assert.Equal(100, cut.Table.RowCount);
        Assert.Equal("100", cut.Table.Rows[^1][0]);
        Assert.Contains(cut.Table.Issues, i => i.MessageKey == DataKeys.QueryTruncated);

        var exact = await src.QueryAsync(NumbersSql(500), null, 500);
        Assert.False(exact.Truncated);
        Assert.Equal(500, exact.Table.RowCount);
        Assert.Empty(exact.Table.Issues);

        var one = await src.QueryAsync(NumbersSql(3), null, 1);
        Assert.True(one.Truncated);
        Assert.Equal(1, one.Table.RowCount);

        var tooMany = await Fails(() => src.QueryAsync(NumbersSql(3), null, DatabaseSource.HardMaxRows + 1));
        Assert.Equal(DataKeys.TooManyRows, tooMany.Key);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => src.QueryAsync(NumbersSql(3), null, 0));
    }

    /// <summary>Hitting the cap on a huge result must not make the caller wait for the server to produce the rest.</summary>
    [RealFact]
    public async Task The_row_cap_on_a_huge_result_returns_quickly()
    {
        await ReadyAsync();
        var sw = Stopwatch.StartNew();
        var r = await Open().QueryAsync(WideRowsSql(50_000_000), null, 50);
        Log("elapsed " + sw.Elapsed);
        Assert.True(r.Truncated);
        Assert.Equal(50, r.Table.RowCount);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "took " + sw.Elapsed);
    }

    // ---------------------------------------------------------------- cancellation and timeouts

    /// <summary>The caller cancels a long query: quick Cancelled error, and the server stops working on it.</summary>
    [RealFact]
    public async Task Cancelling_a_long_query_stops_it_on_the_server_too()
    {
        await ReadyAsync();
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var task = Open().QueryAsync(SleepSql(37), null, 100, cts.Token);
        // Wait until the server is really running it, so the cancel arrives mid-query and not before the connection exists.
        await WaitUntilAsync(async () => await RunningSleepersAsync(37) > 0, TimeSpan.FromSeconds(10));
        cts.Cancel();
        var ex = await Fails(() => task);
        Log(ex.Key + " after " + sw.Elapsed);
        Assert.Equal(DataKeys.Cancelled, ex.Key);
        Assert.True(ex.IsCancellation);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        await WaitUntilAsync(async () => await RunningSleepersAsync(37) == 0, TimeSpan.FromSeconds(10));
        Assert.Equal(0, await RunningSleepersAsync(37));
    }

    /// <summary>A token that is already cancelled never touches the server.</summary>
    [RealFact]
    public async Task A_token_cancelled_in_advance_is_cancelled_without_connecting()
    {
        await ReadyAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Fails(() => Open().QueryAsync("SELECT 1", null, 10, cts.Token));
        Assert.Equal(DataKeys.Cancelled, ex.Key);
    }

    /// <summary>The command timeout ends a query that runs too long and is reported as a timeout.</summary>
    [RealFact]
    public async Task The_command_timeout_ends_a_long_query_as_a_timeout()
    {
        await ReadyAsync();
        var sw = Stopwatch.StartNew();
        var ex = await Fails(() => Open(Make(command: 1)).QueryAsync(SleepSql(38)));
        Log(ex.Key + " | " + ex.Detail + " | " + sw.Elapsed);
        Assert.Equal(DataKeys.Timeout, ex.Key);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "took " + sw.Elapsed);
        await WaitUntilAsync(async () => await RunningSleepersAsync(38) == 0, TimeSpan.FromSeconds(10));
        Assert.Equal(0, await RunningSleepersAsync(38));
    }

    // ---------------------------------------------------------------- streaming

    /// <summary>200,000 wide rows stream with a bounded working set, not a materialised table.</summary>
    [RealFact]
    public async Task Streaming_200000_rows_keeps_memory_bounded()
    {
        await ReadyAsync();
        const int total = 200_000;
        await using var stream = await Open().OpenStreamAsync(WideRowsSql(total));
        Assert.Equal(new[] { "n", "pad" }, stream.Columns);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long peakRetained = 0;
        long count = 0;
        long checksum = 0;
        await foreach (var row in stream.ReadRowsAsync())
        {
            count++;
            checksum += row[1].Length;
            if (count % 40_000 == 0)
            {
                // A forced collection measures what is still REFERENCED. A stream that kept its rows would grow by ~100 MB here.
                long retained = GC.GetTotalMemory(true) - baseline;
                peakRetained = Math.Max(peakRetained, retained);
            }
        }
        Log($"rows {count}, retained peak {peakRetained / 1024 / 1024} MB");
        Assert.Equal(total, count);
        Assert.Equal(232L * total, checksum);
        Assert.True(peakRetained < 25L * 1024 * 1024, $"retained {peakRetained / 1024 / 1024} MB while streaming");
    }

    /// <summary>Stopping early on a huge result and disposing is quick, and the server session goes away.</summary>
    [RealFact]
    public async Task Disposing_a_stream_early_releases_the_connection_quickly()
    {
        await ReadyAsync();
        var sw = Stopwatch.StartNew();
        await using (var stream = await Open().OpenStreamAsync(WideRowsSql(100_000_000)))
        {
            int n = 0;
            await foreach (var _ in stream.ReadRowsAsync()) { if (++n == 10) break; }
        }
        Log("elapsed " + sw.Elapsed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "took " + sw.Elapsed);
    }

    /// <summary>Cancelling while rows are flowing ends the enumeration with a Cancelled error.</summary>
    [RealFact]
    public async Task Cancelling_while_streaming_reports_cancelled()
    {
        await ReadyAsync();
        using var cts = new CancellationTokenSource();
        await using var stream = await Open().OpenStreamAsync(WideRowsSql(100_000_000));
        var ex = await Fails(async () =>
        {
            int n = 0;
            await foreach (var _ in stream.ReadRowsAsync(cts.Token)) { if (++n == 100) cts.Cancel(); }
        });
        Assert.Equal(DataKeys.Cancelled, ex.Key);
    }

    /// <summary>The server's error that appears only after the first row (division by zero in row 3) comes out as a failed query.</summary>
    [RealFact]
    public async Task An_error_in_the_middle_of_a_stream_is_a_failed_query()
    {
        await ReadyAsync();
        await using var stream = await Open().OpenStreamAsync(MidStreamErrorSql);
        var ex = await Fails(async () => { await foreach (var _ in stream.ReadRowsAsync()) { } });
        Log(ex.Key + " | " + ex.Detail);
        Assert.Equal(DataKeys.QueryFailed, ex.Key);
    }

    internal abstract string MidStreamErrorSql { get; }

    // ---------------------------------------------------------------- concurrency

    /// <summary>Many sources used at once do not interfere (every call has its own connection).</summary>
    [RealFact]
    public async Task Parallel_queries_each_get_their_own_answer()
    {
        await ReadyAsync();
        var src = Open();
        var tasks = Enumerable.Range(1, 12).Select(async i =>
        {
            var r = await src.QueryAsync("SELECT @i AS i", new Dictionary<string, object?> { ["i"] = i });
            return int.Parse(First(r));
        });
        Assert.Equal(Enumerable.Range(1, 12), (await Task.WhenAll(tasks)).OrderBy(x => x));
    }

    // ---------------------------------------------------------------- the guard, end to end

    /// <summary>Every write the guard knows is refused before the server sees it, and nothing changes.</summary>
    [RealFact]
    public async Task The_guard_refuses_writes_and_nothing_changes()
    {
        await ReadyAsync();
        var src = Open(Make(user: WriterUser));
        var before = await FingerprintAsync();
        var accepted = new List<string>();
        foreach (var (label, sql) in WriteAttempts.Concat(ExtraGuardAttempts))
        {
            try
            {
                await src.QueryAsync(sql);
                accepted.Add(label);
            }
            catch (DatabaseException ex) when (ex.Key == DataKeys.QueryRejected) { }
            // A user-defined function or a large-object call looks like a plain SELECT to any guard; the server-side read-only
            // session is what stops it (and the fingerprint below proves it did).
            catch (DatabaseException ex) when (ServerOnlyLabels.Contains(label) && ex.Key == DataKeys.QueryFailed) { }
            catch (DatabaseException ex) { accepted.Add(label + " (reached the server: " + ex.Key + ")"); }
        }
        Assert.Equal(before, await FingerprintAsync());
        Assert.True(accepted.Count == 0, "The guard let these through: " + string.Join("; ", accepted));
    }

    /// <summary>Attempts the guard cannot recognise by text (they call a routine), which the server must refuse instead.</summary>
    internal static readonly HashSet<string> ServerOnlyLabels = new() { "function with a side effect", "large object" };

    /// <summary>More statements the guard must refuse.</summary>
    internal virtual IReadOnlyList<(string Label, string Sql)> ExtraGuardAttempts => new (string, string)[]
    {
        ("two statements", "SELECT 1; DELETE FROM victim"),
        ("trailing statement after comment", "SELECT 1 /* x */; DROP TABLE victim"),
        ("with-delete", "WITH d AS (DELETE FROM victim RETURNING *) SELECT * FROM d"),
        ("empty", "   "),
        ("comment only", "-- nothing"),
        ("explain analyze write", "EXPLAIN ANALYZE DELETE FROM victim"),
        ("call", "CALL side_effect()"),
        ("set", "SET default_transaction_read_only = off"),
        ("begin", "BEGIN"),
        ("commit", "COMMIT"),
        ("grant", "GRANT ALL ON victim TO PUBLIC"),
    };

    /// <summary>
    /// The guard is bypassed on purpose: the statements are sent over a connection set up EXACTLY as Rotula sets it up (the
    /// descriptor's own connection string, session statements and read-only transaction). The server itself must refuse every write.
    /// </summary>
    [RealFact]
    public async Task The_server_side_read_only_session_blocks_writes_even_when_the_guard_is_bypassed()
    {
        await ReadyAsync();
        var failures = new List<string>();
        foreach (string user in new[] { WriterUser, Info.AdminUser })
        {
            foreach (var (label, sql) in WriteAttempts)
            {
                string before = await FingerprintAsync();
                Exception? error = await ReadOnlyBypass.RunAsync(Make(user: user), PasswordOf(user), sql);
                string after = await FingerprintAsync();
                Log($"{user,-14} {label,-28} -> {(error is null ? "ALLOWED" : error.GetType().Name + ": " + FirstLine(error.Message))}");
                if (error is null) failures.Add($"{user}: {label} was NOT blocked by the server");
                else if (!IsReadOnlyRefusal(error)) failures.Add($"{user}: {label} failed, but NOT because of the read-only session: {error.GetType().Name} {FirstLine(error.Message)}");
                if (before != after) failures.Add($"{user}: {label} CHANGED the data");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // ---------------------------------------------------------------- saved password

    /// <summary>Save with a password, reopen from disk, connect for real; the file holds no clear password and the password cannot be sent to another account.</summary>
    [RealFact]
    public async Task A_saved_password_round_trips_through_the_store_to_a_real_connection()
    {
        await ReadyAsync();
        if (!OperatingSystem.IsWindows()) return;
        string path = Path.Combine(Path.GetTempPath(), "rotula-it-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonConnectionStore(path);
            var settings = Make();
            await store.SaveAsync(settings, Info.ReaderPassword);

            string file = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(Info.ReaderPassword, file);

            var loaded = await new JsonConnectionStore(path).LoadAsync();
            Assert.True(loaded.Connections.Single().HasPassword);

            var source = await new JsonConnectionStore(path).OpenAsync(settings.Name);
            await source.TestConnectionAsync();
            Assert.Equal("1", First(await source.QueryAsync("SELECT 1")));

            // Same destination, different database name: allowed to reuse the password.
            var reuse = await new JsonConnectionStore(path).OpenWithSettingsAsync(settings with { Database = DatabaseName });
            await reuse.TestConnectionAsync();

            // A different account is a different destination: the saved password must NOT be sent along.
            var other = await Record.ExceptionAsync(() => new JsonConnectionStore(path).OpenWithSettingsAsync(settings with { UserName = Info.AdminUser }));
            var de = Assert.IsType<DatabaseException>(other);
            Assert.Equal(ProviderKeys.PasswordReenterRequired, de.Key);
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- utilities

    private static string FirstLine(string text)
    {
        int nl = text.IndexOfAny(new[] { '\r', '\n' });
        return nl > 0 ? text[..nl] : text;
    }

    /// <summary>A TCP port nobody listens on right now.</summary>
    internal static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition()) return;
            await Task.Delay(150);
        }
    }

    /// <summary>Reads one value as the administrator (for checks in subclasses).</summary>
    internal async Task<string> AdminScalarAsync(string sql, string? database = null)
    {
        await using var conn = await OpenAdminAsync(database ?? DatabaseName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}

/// <summary>Helpers that need to look at a source's quoting without a server round trip.</summary>
internal static class SourceExtensions
{
    /// <summary>Quotes a name that contains a dot for <see cref="IDatabaseSource.DescribeAsync(string, CancellationToken)"/> in the engine's own style.</summary>
    public static string QuoteForDescribe(this IDatabaseSource source, string name)
    {
        // BuildSelectAll shows the engine's quote character, so the same character is used for the "table" argument.
        string sql = source.BuildSelectAll(new DatabaseObject(null, "x", DatabaseObjectKind.Table));
        char quote = sql.Contains('`') ? '`' : '"';
        return quote + name + quote;
    }
}

/// <summary>
/// Opens a connection exactly like the product does (see AdoDatabaseSource.BeginReadOnlyAsync) but runs the given statement
/// WITHOUT the query guard, to prove that the server-side read-only controls hold on their own.
/// </summary>
internal static class ReadOnlyBypass
{
    /// <summary>Returns the exception the server raised for <paramref name="sql"/>, or null if the statement ran.</summary>
    public static async Task<Exception?> RunAsync(DatabaseConnectionSettings settings, string password, string sql)
    {
        IProviderDescriptor descriptor = ProviderRegistry.Default.Resolve(settings);
        string connectionString = descriptor.BuildConnectionString(settings, password);
        await using var conn = descriptor.CreateConnection(connectionString, settings);
        await conn.OpenAsync();
        var policy = descriptor.ReadOnly;
        foreach (string statement in policy.SessionStatements) await ExecAsync(conn, null, statement);
        DbTransaction? tx = policy.StartTransaction ? await descriptor.BeginTransactionAsync(conn, CancellationToken.None) : null;
        try
        {
            if (tx is not null) foreach (string statement in policy.TransactionStatements) await ExecAsync(conn, tx, statement);
            try
            {
                await ExecAsync(conn, tx, sql);
                return null;
            }
            catch (Exception ex) { return ex; }
        }
        finally
        {
            // The product always rolls back; so does the harness. A write that was NOT blocked is still detected by the fingerprint.
            if (tx is not null) { try { await tx.RollbackAsync(); } catch (Exception) { } await tx.DisposeAsync(); }
        }
    }

    private static async Task ExecAsync(DbConnection conn, DbTransaction? tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }
    }
}
