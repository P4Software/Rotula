# Testing the database engine against real servers

The normal test suite (`dotnet test Rotula.sln`) needs no database server: it uses SQLite files and fakes. That proves the logic, but not that
Rotula really talks to PostgreSQL or MariaDB correctly. `tests/Rotula.Data.Integration` does: it runs the engine in `src/Rotula.Data` against
real servers started on your own PC for the duration of the tests.

Without a running server every one of these tests is **skipped with a reason** ("No test servers are running ..."), never failed, so the
normal build and `dotnet test Rotula.sln` are not affected.

## Run it locally

1. Double-click `tests\Rotula.Data.Integration\servers\Start-TestServers.cmd`.
   The first time it downloads the vendors' official portable Windows archives (about 480 MB in total) into
   `%LOCALAPPDATA%\Rotula\test-servers\cache`, checks them, unpacks them under `%LOCALAPPDATA%\Rotula\test-servers\run` and starts
   PostgreSQL and MariaDB. Later starts reuse the download and take about a minute.
2. Run the tests: `dotnet test tests\Rotula.Data.Integration` (about three minutes; they find the servers on their own).
3. Double-click `Stop-TestServers.cmd`. It stops both servers and deletes their data, certificate and generated passwords. Add `-PurgeCache`
   (from a command prompt: `Stop-TestServers.cmd -PurgeCache`) to delete the downloaded archives as well.

What the scripts do, and do not do:

* The servers listen on `127.0.0.1` only, on random high ports. The ports and the generated throw-away passwords are written to
  `%LOCALAPPDATA%\Rotula\test-servers\run\servers.json`, outside the repository. That file is how the tests find the servers.
* Nothing is installed: no Windows service, no firewall change, no registry entry. Everything lives in the scratch folder.
* Downloaded archives are treated as untrusted data: each goes into its own new folder, the checksum is verified before anything is unpacked
  (on every start, also for a cached file), the script refuses to continue on a mismatch, and no program is run from inside a download
  folder until it has been unpacked into the run folder. Entries that would unpack outside the target folder are refused.
* Versions, download addresses and checksums are in `servers/servers.settings.json` (commented). To test another version, change them there.

### Checksums

| Engine | Version | Source | Checksum |
|---|---|---|---|
| MariaDB | 11.8.9 (long-term support) | `downloads.mariadb.org` | The vendor publishes SHA-256 and SHA-512 (REST API); both are enforced. |
| PostgreSQL | 18.6-5 | EDB "PostgreSQL Binaries" (the community project ships no Windows archive) | EDB publishes **no** checksum. The SHA-256 of the file as first downloaded is recorded in the settings file (`recordedSha256`) so a later change is detected; that is trust-on-first-use, not a vendor guarantee. |

## What is covered

The same suite (`ServerSuite`) runs against both engines; PostgreSQL and MariaDB add their own checks.

* **Connecting**: user and password; wrong password and unknown user (authentication failed, password never in any message); unknown database;
  closed port (host unreachable, quickly); a server that accepts and never answers (timeout).
* **TLS** with a self-signed certificate: the default secure settings are refused with `CertificateNotTrusted`; the explicit trust option connects
  and the session is shown to be encrypted; handing over the certificate as the certificate authority verifies chain and host name; a missing
  authority file is an invalid-settings error; encryption off. One MariaDB subtlety: MySqlConnector accepts a self-signed certificate under
  `VerifyFull` for an account WITH a password, because the MariaDB 11.4+ handshake proves the server's identity from the password hash and the
  certificate fingerprint (`ServerSession.cs`, "mission impossible: zero-configuration SSL"). The refusal test therefore uses an account without a
  password, and a separate test pins the password-account behaviour so a driver change is noticed. Known driver behaviour, not a Rotula bug: the connection manager's help for the option says that for MariaDB with a password account the
  certificate is verified by fingerprint (see [data-providers.md](data-providers.md)).
* **Catalogue**: `ListTables` (tables, views, schemas, no system objects, sorted, privilege-aware), `Describe` (quoted names with dots, hostile names),
  `Preview`, `BuildSelectAll`.
* **Queries**: named parameters (text with accents, numbers, dates, booleans, null, repeated names, attack-looking values), unicode (accents, n-tilde,
  CJK, emoji, combining marks, right-to-left controls), the value formatting rules (NULL, decimal, date, boolean, uuid, json, binary), awkward values,
  the row cap and the `Truncated` flag, large values.
* **Time**: cancelling a long query (and the server stops running it), the command timeout, a pre-cancelled token.
* **Streaming**: 200,000 wide rows with bounded memory, early dispose of a huge result, cancel while rows flow, a server error in the middle of a stream.
* **Read-only, end to end**: the guard refuses writes and nothing changes; with the guard **bypassed on purpose** (the statements are sent over a
  connection built from the provider descriptor exactly as the product builds it) the server-side read-only session still blocks INSERT, UPDATE,
  DELETE, TRUNCATE, DDL, sequences (`nextval`, `NEXT VALUE FOR`), a function with side effects and `SELECT INTO`, for both a write-capable account
  and the administrator. PostgreSQL also checks the startup option alone, the pooler mode (`noStartupOptions`, in-transaction `SET` alone) and that a
  session cannot switch read-only off from inside.
* **Saved password**: save, reload from disk, connect for real; the file holds no clear password; the saved password is refused for a different account.

SQL Server is covered by `DataLocalDbTests` in `tests/Rotula.Tests` (LocalDB, skipped when it is not installed); it is deliberately not duplicated here.

The first run found twelve problems (B1 to B12: timeouts, certificate rejection, wide DECIMAL, awkward PostgreSQL values, Describe nullability, system-versioned
tables, side-effect functions, CHARACTER SET in a cast, rejected logins reported as unreachable servers, silent wrong output); all are fixed and their tests are ordinary
regression tests now (`KnownBugs.cs` is empty). The rejected-login tests run 25 attempts per account; the fake-server tests in `DataEngineFixTests` cover the
connection reset deterministically. MariaDB answers an unknown user by resetting the connection, so that one test takes as long as the connect timeout.

Tests that document a **known problem of the engine** carry `[Trait("Category", "KnownBug")]`. They fail today and will pass when the problem is
fixed. `dotnet test --filter "Category!=KnownBug"` runs everything else.

## GitHub workflow

`.github/workflows/integration.yml` runs the same scripts and tests on `windows-latest`, by hand (Run workflow) and every Monday. It uses no secrets, caches the
downloads (the script verifies them again), fails if the tests were skipped instead of run, and runs the known-problem tests in a separate, informational step.

## Adding integration tests for another engine

1. Add the server to `servers/servers.settings.json` (official download, vendor checksum if published) and to `Start-TestServers.ps1` / `Stop-TestServers.ps1`
   (init a throw-away data folder, bind to 127.0.0.1 on a random port, write the entry to `servers.json`). Keep the rules above: own folder per download,
   verify before unpacking, nothing installed.
2. Add a constant and a case to `ServerCatalog` and a `...Seed` class (database, a SELECT-only reader, a write-capable writer, the same sample tables).
3. Derive a class from `ServerSuite`, mark it `[Engine("...")]`, and fill in the few SQL texts that differ. Every `[RealFact]` of the suite then runs for
   the new engine, and is skipped with a clear reason when its server is not up.
4. Add engine-only tests (types, read-only specifics) in the same class. Use `[RealFact]`, never `[Fact]`, so the skip logic applies.
5. Tell the workflow about it (the executed-test minimum in `integration.yml`) and list it in the table above.
