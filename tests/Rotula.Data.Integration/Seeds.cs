using System.Security.Cryptography;
using MySqlConnector;
using Npgsql;

namespace Rotula.Data.Integration;

/// <summary>Random throw-away passwords for accounts the tests create themselves (never written to any file).</summary>
internal static class TestSecrets
{
    /// <summary>Letters and digits only, 24 characters.</summary>
    public static string NewPassword()
    {
        const string chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(24);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }
}

/// <summary>
/// Creates the test database, accounts and data on the real PostgreSQL server, once per test run. Everything is dropped and rebuilt
/// so a second run starts from the same state.
/// </summary>
internal static class PostgreSqlSeed
{
    public const string Database = "rotula_it";
    public const string WriterUser = "rotula_writer";

    /// <summary>Password of the write-capable account created for the "server-side read-only" tests.</summary>
    public static readonly string WriterPassword = TestSecrets.NewPassword();

    private static Task? _ready;
    private static readonly object Gate = new();

    public static Task EnsureAsync(ServerInfo info)
    {
        lock (Gate) return _ready ??= SeedAsync(info);
    }

    /// <summary>A plain (unencrypted, unpooled) connection string for the administrator; tests use it to seed and to check effects.</summary>
    public static string AdminConnectionString(ServerInfo info, string database) => new NpgsqlConnectionStringBuilder
    {
        Host = info.Host, Port = info.Port, Username = info.AdminUser, Password = info.AdminPassword, Database = database,
        SslMode = SslMode.Disable, Pooling = false, Timeout = 15, CommandTimeout = 60,
    }.ConnectionString;

    private static async Task SeedAsync(ServerInfo info)
    {
        await using (var conn = new NpgsqlConnection(AdminConnectionString(info, "postgres")))
        {
            await conn.OpenAsync();
            await Run(conn, "DROP DATABASE IF EXISTS " + Database + " WITH (FORCE)");
            await Run(conn, "DROP ROLE IF EXISTS " + info.ReaderUser);
            await Run(conn, "DROP ROLE IF EXISTS " + WriterUser);
            await Run(conn, "CREATE ROLE " + info.ReaderUser + " LOGIN PASSWORD '" + info.ReaderPassword + "'");
            await Run(conn, "CREATE ROLE " + WriterUser + " LOGIN PASSWORD '" + WriterPassword + "'");
            await Run(conn, "CREATE DATABASE " + Database + " ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C' TEMPLATE template0");
        }

        await using var db = new NpgsqlConnection(AdminConnectionString(info, Database));
        await db.OpenAsync();
        string reader = info.ReaderUser;
        string[] script =
        {
            "CREATE TABLE customers (id integer PRIMARY KEY, name text NOT NULL, city text, credit numeric(12,2), born date, active boolean, " +
            "token uuid, payload jsonb, data bytea, created timestamp, seen timestamptz)",
            "INSERT INTO customers VALUES " +
            "(1, 'Alice Smith', 'London', 1234.50, '1990-05-17', true, 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11', '{\"k\": [1, 2]}', '\\xDEADBEEF', '2024-03-01 10:20:30.123456', '2024-03-01 10:20:30+00')," +
            "(2, 'José Muñoz', 'Bogotá', 0.10, '1985-12-01', false, NULL, NULL, NULL, NULL, NULL)," +
            "(3, '你好，世界', '東京', 99.99, '2000-01-01', true, NULL, NULL, NULL, NULL, NULL)," +
            "(4, '😀 emoji 🚀', 'Zürich', 500.00, '2024-02-29', true, NULL, NULL, NULL, NULL, NULL)," +
            "(5, 'Null Person', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)," +
            "(6, 'O''Brien; DROP TABLE customers; --', 'Dublin', -5.25, '1970-01-01', false, NULL, NULL, NULL, '2024-03-01 00:00:00', NULL)",
            "CREATE VIEW customers_v AS SELECT id, name FROM customers WHERE active",
            "CREATE SCHEMA sales",
            "CREATE TABLE sales.orders (id integer PRIMARY KEY, customer_id integer REFERENCES customers(id), total numeric(10,2) NOT NULL)",
            "INSERT INTO sales.orders VALUES (1, 1, 10.00), (2, 1, 20.50), (3, 2, 5.00)",
            "CREATE TABLE \"odd.name\" (id integer, \"Mixed Case\" text)",
            "INSERT INTO \"odd.name\" VALUES (1, 'dot in the table name')",
            "CREATE TABLE secret_notes (id integer, note text)",
            "INSERT INTO secret_notes VALUES (1, 'not for the reader')",
            "CREATE TABLE victim (id integer PRIMARY KEY, v text)",
            "INSERT INTO victim VALUES (1, 'keep'), (2, 'keep')",
            "CREATE TABLE audit (id serial PRIMARY KEY, msg text)",
            "CREATE SEQUENCE seq_probe",
            "CREATE FUNCTION side_effect() RETURNS integer LANGUAGE sql AS $$ INSERT INTO audit(msg) VALUES ('called') RETURNING id $$",
            // The reader may read what a report needs and nothing else; the writer may do anything, so that the server-side read-only
            // session is the ONLY thing standing between a bypassed guard and a write.
            "GRANT USAGE ON SCHEMA public, sales TO " + reader,
            "GRANT SELECT ON customers, customers_v, sales.orders, \"odd.name\", victim TO " + reader,
            "GRANT ALL ON SCHEMA public, sales TO " + WriterUser,
            "GRANT ALL ON ALL TABLES IN SCHEMA public, sales TO " + WriterUser,
            "GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO " + WriterUser,
        };
        foreach (string sql in script) await Run(db, sql);
    }

    private static async Task Run(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Same as <see cref="PostgreSqlSeed"/> for the real MariaDB server.</summary>
internal static class MariaDbSeed
{
    public const string Database = "rotula_it";

    /// <summary>A second database the reader has no rights on (holds the "private" tables).</summary>
    public const string PrivateDatabase = "rotula_private";
    public const string WriterUser = "rotula_writer";

    /// <summary>An account without any password: nothing the server could vouch with, so only the certificate check protects it.</summary>
    public const string NoPasswordUser = "rotula_nopw";

    /// <summary>Password of the write-capable account created for the "server-side read-only" tests.</summary>
    public static readonly string WriterPassword = TestSecrets.NewPassword();

    private static Task? _ready;
    private static readonly object Gate = new();

    public static Task EnsureAsync(ServerInfo info)
    {
        lock (Gate) return _ready ??= SeedAsync(info);
    }

    /// <summary>A plain (unencrypted, unpooled) connection string for the administrator.</summary>
    public static string AdminConnectionString(ServerInfo info, string? database) => new MySqlConnectionStringBuilder
    {
        Server = info.Host, Port = (uint)info.Port, UserID = info.AdminUser, Password = info.AdminPassword,
        Database = database ?? "", SslMode = MySqlSslMode.None, Pooling = false, ConnectionTimeout = 15, DefaultCommandTimeout = 60,
    }.ConnectionString;

    private static async Task SeedAsync(ServerInfo info)
    {
        await using var conn = new MySqlConnection(AdminConnectionString(info, null));
        await conn.OpenAsync();
        var statements = new List<string>
        {
            "DROP DATABASE IF EXISTS " + Database,
            "DROP DATABASE IF EXISTS " + PrivateDatabase,
            "DROP DATABASE IF EXISTS bypass_db",
            "CREATE DATABASE " + Database + " CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
            "CREATE DATABASE " + PrivateDatabase + " CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
        };
        // Accounts are created for both spellings of the loopback host so that a login failure can only mean a real problem.
        foreach (string host in new[] { "127.0.0.1", "localhost" })
        {
            statements.Add($"DROP USER IF EXISTS '{info.ReaderUser}'@'{host}'");
            statements.Add($"DROP USER IF EXISTS '{WriterUser}'@'{host}'");
            statements.Add($"DROP USER IF EXISTS '{NoPasswordUser}'@'{host}'");
            statements.Add($"CREATE USER '{NoPasswordUser}'@'{host}'");
            statements.Add($"CREATE USER '{info.ReaderUser}'@'{host}' IDENTIFIED BY '{info.ReaderPassword}'");
            statements.Add($"CREATE USER '{WriterUser}'@'{host}' IDENTIFIED BY '{WriterPassword}'");
        }
        foreach (string sql in statements) await Run(conn, sql);

        await conn.ChangeDatabaseAsync(Database);
        string[] script =
        {
            "CREATE TABLE customers (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL, city VARCHAR(100), credit DECIMAL(12,2), born DATE, active BOOLEAN, " +
            "token UUID, payload JSON, data BLOB, created DATETIME(6), seen TIMESTAMP(6) NULL)",
            "INSERT INTO customers VALUES " +
            "(1, 'Alice Smith', 'London', 1234.50, '1990-05-17', TRUE, 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11', '{\"k\": [1, 2]}', X'DEADBEEF', '2024-03-01 10:20:30.123456', '2024-03-01 10:20:30')," +
            "(2, 'José Muñoz', 'Bogotá', 0.10, '1985-12-01', FALSE, NULL, NULL, NULL, NULL, NULL)," +
            "(3, '你好，世界', '東京', 99.99, '2000-01-01', TRUE, NULL, NULL, NULL, NULL, NULL)," +
            "(4, '😀 emoji 🚀', 'Zürich', 500.00, '2024-02-29', TRUE, NULL, NULL, NULL, NULL, NULL)," +
            "(5, 'Null Person', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)," +
            "(6, 'O''Brien; DROP TABLE customers; --', 'Dublin', -5.25, '1970-01-01', FALSE, NULL, NULL, NULL, '2024-03-01 00:00:00', NULL)",
            "CREATE VIEW customers_v AS SELECT id, name FROM customers WHERE active",
            // MariaDB has no schemas inside a database, so the second PostgreSQL schema becomes a table prefix.
            "CREATE TABLE sales_orders (id INT PRIMARY KEY, customer_id INT, total DECIMAL(10,2) NOT NULL, FOREIGN KEY (customer_id) REFERENCES customers(id))",
            "INSERT INTO sales_orders VALUES (1, 1, 10.00), (2, 1, 20.50), (3, 2, 5.00)",
            "CREATE TABLE `odd.name` (id INT, `Mixed Case` VARCHAR(50))",
            "INSERT INTO `odd.name` VALUES (1, 'dot in the table name')",
            // The private tables live in another database so that the reader can hold SELECT on the whole test database
            // (the sequence engine's virtual tables need that) and still be refused these.
            "CREATE TABLE " + PrivateDatabase + ".secret_notes (id INT, note VARCHAR(100))",
            "INSERT INTO " + PrivateDatabase + ".secret_notes VALUES (1, 'not for the reader')",
            "CREATE TABLE victim (id INT PRIMARY KEY, v VARCHAR(20))",
            "INSERT INTO victim VALUES (1, 'keep'), (2, 'keep')",
            "CREATE TABLE " + PrivateDatabase + ".audit (id INT AUTO_INCREMENT PRIMARY KEY, msg VARCHAR(50))",
            "CREATE SEQUENCE seq_probe",
            "CREATE TABLE hist (id INT PRIMARY KEY, v VARCHAR(10)) WITH SYSTEM VERSIONING",
            "INSERT INTO hist VALUES (1, 'a')",
            "CREATE FUNCTION side_effect() RETURNS INT MODIFIES SQL DATA BEGIN INSERT INTO " + PrivateDatabase + ".audit(msg) VALUES ('called'); RETURN 1; END",
        };
        foreach (string sql in script) await Run(conn, sql);

        foreach (string host in new[] { "127.0.0.1", "localhost" })
        {
            await Run(conn, $"GRANT SELECT ON {Database}.* TO '{info.ReaderUser}'@'{host}'");
            await Run(conn, $"GRANT SELECT ON {Database}.* TO '{NoPasswordUser}'@'{host}'");
            await Run(conn, $"GRANT ALL ON {Database}.* TO '{WriterUser}'@'{host}'");
            await Run(conn, $"GRANT ALL ON {PrivateDatabase}.* TO '{WriterUser}'@'{host}'");
        }
    }

    private static async Task Run(MySqlConnection conn, string sql)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
