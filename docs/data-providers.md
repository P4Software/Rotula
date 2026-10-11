# Database providers and packs (developer guide)

This page is for people who add an engine to Rotula. Users do not need it. The decisions behind it are in
[decisions.md](decisions.md) ("Database providers"), the licence list in [../THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## The picture

```
connections.json  ->  DatabaseConnectionSettings  ->  ProviderRegistry  ->  IProviderDescriptor  ->  AdoDatabaseSource
 (name, provider id,    (password-free record)        (built-in + packs)     (dialect, read-only,      (guard, read-only
  preset, extras)                                                             connection, errors)        transaction, rows)
```

* `ProviderRegistry.Default` holds the five built-in providers (`sqlserver`, `postgresql`, `mysql`, `sqlite`, `odbc`) and the presets of
  `Presets/presets.json`. Packs add to it at start-up (`PackLoader.LoadAll`). It can only add: an id that exists can never be replaced.
* `DatabaseSource.Create(settings, password)` finds the descriptor of `settings.GetEffectiveProviderId()`. The ADO.NET implementation applies
  the guard, the read-only steps and the always-rolled-back transaction for every engine. An engine that is not ADO.NET returns its own
  `IDatabaseSource` from `CreateCustomSource`.
* The UI renders the connection form from `IProviderDescriptor.Options` and the engine list from `ProviderRegistry.GetDisplayEntries()`.
  Labels are message keys (`Data.Option.*`, `Data.Preset.*`, `Conn.Provider.*`); the app supplies the Spanish and English texts.

## Adding a provider that is just another server of a known kind (a preset)

No code. Add an object to `src/Rotula.Data/Presets/presets.json`:

| Field | Meaning |
|---|---|
| `id`, `nameKey`, `name` | lower-case id; message key starting `Data.Preset.`; English name |
| `providerId` | the provider that connects (`postgresql`, `mysql`, `sqlserver`, `odbc`, or a pack provider) |
| `defaultPort`, `hostPatterns` | port to suggest; `*` patterns that recognise the service from a pasted host (at least 3 literal characters) |
| `encrypt`, `trustServerCertificate` | must stay `true` / `false`: presets are secure by default and a test enforces it |
| `extras` | provider options (for example `{ "noStartupOptions": true }`, `{ "flavor": "mariadb" }`); only options the provider declares |
| `poolerHost` | the patterns are a connection pooler: the `options` startup parameter is not sent to such hosts |
| `hintKeys`, `driverMissingHintKey` | `Data.Hint.*` message keys shown next to the form / when the ODBC driver is missing |
| `odbc` | for file formats served by a vendor ODBC driver: `drivers`, `pathKey`, `pathKind` (`file`/`folder`), `extensions`, `parameters`, `underlyingEngine` |
| `requiresEntra`, `verified` | the service accepts Microsoft Entra only / host patterns not yet confirmed against vendor documentation |

`DataPresetTests` validates the whole catalogue (provider exists, ports, patterns, secure defaults, extras known, hint keys). Run it.

## Adding an engine (a new provider)

1. **Dialect** (`SqlDialect`). Add or reuse an entry in `SqlDialects`. Fields:

   | Field | Purpose |
   |---|---|
   | `LexRules` | where strings, comments and quoted names end (see "Lexical rules"). Several entries = text must be clean under all (ODBC) |
   | `AmbiguityChecks` | features lexed under both readings; a disagreement refuses the query (see "Differential lexing") |
   | `IdentifierQuote`, `ParameterStyle`, `ParameterNamesCaseSensitive` | quoting of table names; marker the driver wants for `@name` (`?`, `:name`, `$name`) |
   | `RowLimit` | `LIMIT`, `TOP`, `FETCH FIRST`, `FIRST`, `ROWNUM`: `ApplyRowLimit` builds it for statements Rotula writes |
   | `CatalogueSql` | three columns (schema or NULL, name, type containing "view" for views); null = driver schema collections |
   | `ConnectivityProbeSql` | statement the connection test runs when opening is lazy |
   | `ForbiddenKeywords` | extra words refused anywhere |
   | `ForbiddenFunctions` / `ForbiddenFunctionPrefixes` | refused wherever the word appears (Oracle `UTL_`, `DBMS_`) |
   | `ForbiddenCalls` / `ForbiddenCallPrefixes` | refused only when followed by `(`; for names that are also column names (ClickHouse `file`, `url`) |
   | `AllowedFunctions` | exceptions to the prefixes (`DBMS_LOB`) |
   | `ForbiddenCallQualifiers` | schemas whose routines cannot be called (`SYS.pkg.fn(`, `SYSPROC.x(`); reading their tables stays possible |
   | `ForbiddenAfterWith` | words that may not follow `WITH` (Oracle `FUNCTION`, `PROCEDURE`) |
   | `ForbiddenPhrases` | word sequences that change state (the base list already has `NEXT VALUE FOR`) |
   | `RejectFileTableReferences` | DuckDB: `FROM 'x.csv'`, `FROM "x.parquet"` |
   | `RejectDatabaseLinks` | Oracle: `table@link` |
   | `RejectInFileClause` | Jet: `FROM t IN 'other.mdb'` |

2. **Descriptor** (`IProviderDescriptor`, or derive in a pack). Provide: `Id`, `DisplayNameKey`/`DisplayName`, `Category`, `DefaultPort`,
   `SupportedAuthentication`, `Options` (the form; use `StandardOptions` for host, port, database, user, file, encrypt, trust, CA file),
   `RequiresServer`, `Dialect`, `ReadOnly`, `Errors`, `Validate`, `BuildConnectionString`, `CreateConnection`. Optional: `ResolveDialect` (flavours),
   `Preflight` (local checks), `BeginTransactionAsync` (native read-only transaction), `ParameterName`, `ConfigureCommand`, `CreateCustomSource`.
   Rules: build connection strings only with the driver's `ConnectionStringBuilder`; validate every user value with `ProviderValidation`;
   never put a secret in `Extras`; map native error numbers in `Errors.ClassifyNumber` (SQLSTATE classes are handled for you).

3. **Read-only checklist.** Fill `ReadOnlyPolicy` from the strongest switch the engine has, in this order, and write a test for each step that applies:
   * [ ] open the data as read-only (file mode `ReadOnly`, `access_mode=READ_ONLY`, `ApplicationIntent` is only a hint);
   * [ ] session statements (`SessionStatements`: `SET SESSION TRANSACTION READ ONLY`, `PRAGMA query_only = ON`, `SET enable_external_access = false`, `SET lock_configuration = true`);
   * [ ] a read-only transaction (`TransactionStatements`, or `BeginTransactionAsync` with the driver's flag);
   * [ ] connection-level options (`readonly=1` for ClickHouse, a startup `options` parameter where poolers allow it);
   * [ ] cost caps where reading costs money (BigQuery maximum bytes billed, Snowflake statement timeout, DynamoDB limit);
   * [ ] set `GuardIsPrimaryControl = true` if none of the above exists, so the UI recommends a SELECT-only account;
   * [ ] document in the descriptor `Description` what really enforces read-only.

   **The guard is a safety net, not the wall.** It refuses everything that is not one SELECT, but SQL is large and engines differ. The database
   account Rotula uses should be able to read and nothing else. Say so in the UI text and in the engine's help.

4. **Tests** (no server needed; they run everywhere): guard accept/reject table for the dialect in `DataDialectGuardTests`, ambiguity cases in
   `DataLexAmbiguityTests`, connection string and validation tests in the style of `DataConnectionStringTests`, error mapping in the style of
   `DataErrorMapperTests`. Engines that can run on this machine (SQLite, LocalDB, a local PostgreSQL) also get an end-to-end test that skips when the
   engine is absent (PostgreSQL and MariaDB are tested against real portable servers: see [testing-databases.md](testing-databases.md), which also says how to add yours). Servers that cannot run on a developer machine (Oracle, Db2, SAP HANA, Teradata, cloud services) are covered by the unit tests above plus a
manual smoke test against a real server, described in the pack's own README; say plainly in that README what was and was not tested.

## Lexical rules and differential lexing

`SqlLexRules` describes where a string, comment or quoted name ends: per quote kind backslash escapes (`SingleQuoteBackslash`,
`DoubleQuoteBackslash`, `BacktickBackslash`) and doubled-quote escapes, `#` / `//` / `{ }` comments, nested comments, dollar quotes, q-quotes,
triple quotes, bind-variable forms (`:name`, `$name`). Set each flag from the vendor's language reference and cite the page in a comment next to
the dialect. When unsure, choose the reading in which the guard skips LESS text (do not enable nesting or long strings on a guess).

For every feature in `SqlDialect.AmbiguityChecks` the guard lexes the text again with that feature flipped, all on and all off; if the token
boundaries differ the query is refused with `Data.Query.Rejected.Ambiguous`. Use it for anything that depends on a server setting or that the
vendor documents loosely. A quoted identifier that the dialect reads WITHOUT backslash escapes but that is checked for them (Snowflake `"..."`, MySQL backticks) is refused
if it contains a backslash. (Quoted strings are not refused just for containing a backslash: only the readings that move a boundary matter, and
those are exactly what the differential lexing catches; a Windows path in a MySQL string still works.)

**The ODBC engine hint only adds rules.** With `underlyingEngine` set, the text must still be clean under the four classic lexings and under the
engine's, with every backslash reading checked, and the engine's forbidden lists apply on top. Rotula cannot see what is behind a driver and the
query can come from an untrusted file, so a wrong hint must never loosen the guard. Consequence: an Oracle q-quote that contains an apostrophe is
refused through ODBC (it is allowed through a real Oracle pack connection).

Sources used so far (read in 2026-10): PostgreSQL "Lexical Structure"; MySQL "String Literals" / "Comments"; Oracle SQL Language Reference "Text
Literals" (q-quote: any delimiter except space, tab, return; bracket pairs); GoogleSQL "Lexical structure and syntax" (triple-quoted and raw
strings, `#` comments); ClickHouse "Syntax" (comments `--`, `#`, `#!`, `//`, nestable `/* */`; single-quote strings; heredocs); Snowflake "String &
binary data types" ($$ strings, backslash escapes in single quotes). Verified since: ClickHouse "Syntax > Identifiers" says the escaping rules of quoted
identifiers are those of string literals (backslash escapes apply inside `"..."` and backticks, and `QuoteIdentifier` doubles backslashes via
`SqlDialect.IdentifierBackslashEscapes`); DuckDB block comments nest (checked against the real engine: `SELECT 1 /* a /* b */ c */` runs); Firebird
comments do not nest and q-quotes exist from 3.0 (checked against a real Firebird 5.0.4). Still unverified and therefore checked both ways:
Informix backslashes, BigQuery doubled quotes. Informix `{ }` comments and Jet `#date#` / `IN 'file'` come from general knowledge of those products;
confirm them when the pack is built.

## Packs

A pack is a folder with a manifest, its entry assembly and everything it needs:

```
packs/
  oracle/
    pack.json
    Rotula.Data.Pack.Oracle.dll         <- entry assembly (IDatabaseProviderPack)
    Oracle.ManagedDataAccess.dll        <- the pack's own dependencies
    runtimes/win-x64/native/xyz.dll     <- the pack's own native libraries, if any
    NOTICES.txt                         <- licence and third-party notices (pack.json: licenseNoticesFile)
```

`pack.json`:

```json
{
  "id": "oracle",
  "name": "Oracle",
  "version": "1.0.0",
  "publisher": "P4 Software",
  "providerIds": [ "oracle" ],
  "minDataApiVersion": "1.0",
  "entryAssembly": "Rotula.Data.Pack.Oracle.dll",
  "entryType": "Rotula.Data.Pack.Oracle.OraclePack",
  "licenseNoticesFile": "NOTICES.txt"
}
```

* `providerIds` must be exactly the providers the entry class registers. `minDataApiVersion` is checked against `DataApi.Current` (same major, not newer).
* Project file: reference `Rotula.Data` with `Private="false" ExcludeAssets="runtime"` (compile only; the host's copy is shared, which is what makes the
  interface the same type on both sides), set `EnableDynamicLoading`, and let normal build output (with `.deps.json`) be the pack folder. See
  `tests/Rotula.Data.SamplePack` for a working example with a private managed dependency and a private native library.
  **Real packs use `src/PackProject.props` instead** (`<Import Project="..\PackProject.props" />`): a project reference, even with
  `ExcludeAssets="runtime"`, still copies the host's transitive packages into the pack folder (SQL Server networking, SQLite natives, ...), whose licences
  the pack's notices would not cover. The props file references the three contract assemblies as plain compile-only files and turns off debug files.
* **API versions.** 1.0 is the original contract. 1.1 added `IProviderDescriptor.FormatCell` (write a value as text when the driver's .NET type loses
  data) and `SqlDialect.IdentifierBackslashEscapes`. A pack that uses a newer member must say so in `minDataApiVersion` (Firebird, DuckDB and ClickHouse do: 1.1; Oracle uses nothing newer: 1.0).
* **Building the shipped packs.** `scripts\build-packs.cmd` (double-click; or `build-packs.ps1`) publishes every `src\Rotula.Data.Pack.*` project for
  `win-x64`, not self-contained, untrimmed, into `artifacts\packs\<id>\` and prints each pack's size. `tests\Rotula.Data.Packs.Tests` publishes the same way
  into `testpacks\<id>` next to the tests and loads each folder through `PackLoader.LoadAll`; it also checks that no host or foreign-stack file is in
  a pack folder and that every third-party DLL is named in `NOTICES.txt`.
* **The `NOTICES.txt` of a pack** is produced from the files the build really contains: component, version, copyright, project and the licence text
  verbatim from the package (the nuspec `license` file, or the standard text for packages that only carry an SPDX expression). `scriptsmake-pack-notices.ps1` writes it; regenerate it when a
  dependency version changes.
* The loader shares only `Rotula.Data`, `Rotula.Data.Abstractions`, `Rotula.Core` and the .NET framework with the pack; everything else is loaded from the pack
  folder (`AssemblyDependencyResolver`, plus a probe of `runtimes/{rid}/native` for packs built without a deps file). Native libraries are loaded from inside
  the pack folder only.
* Where packs are searched, and nowhere else: `packs` next to the executable (trusted) and `%AppData%\Rotula\packs` (needs approval; see below).
  The location comes from `AppContext.BaseDirectory`, so it works in a single-file `Rotula.App.exe` with a `packs` folder beside it.
* The host calls `PackLoader.LoadAll(new PackLoadOptions())` once at start-up and shows `report.Warnings` (message keys, plus `EnglishText` for logs)
  and `report.PendingApproval`. For each pending pack: `PackLoader.CreateTrustPrompt` gives the dialog data; only after the user confirms call
  `PackLoader.Approve(prompt, store)`, then `PackLoader.LoadOne`/`LoadAll` again. `PackTrustStore.Revoke` withdraws an approval.
* A pack failing (corrupt file, missing dependency, exception in its code, wrong API version, id clash, manifest mismatch) never throws out of
  `LoadAll`; the application starts without it.

Trust model in one paragraph: a pack is a program. The folder installed next to Rotula is as trusted as Rotula. A pack the user drops into their own folder
is loaded only after a warning in plain language and an explicit approval that records the SHA-256 of all its files; a changed pack (even one
dependency) is a new pack. Links inside pack folders are refused. The approval file itself is user-writable, so this protects against accidental and
remote placement, not against code already running as the user.

## Behaviour found by running against real servers

These rules come from the integration run against PostgreSQL 18.6 and MariaDB 11.8.9 (see [testing-databases.md](testing-databases.md)); each has a
regression test there or in `DataEngineFixTests` / `DataDialectGuardTests`.

**Functions a READ ONLY transaction does not block.** Read-only sessions still allow functions with side effects outside the data, so the
dialects refuse them in the guard: PostgreSQL `pg_advisory_*` / `pg_try_advisory_*` (session locks that outlive the rolled-back transaction),
`pg_reload_conf`, `pg_switch_wal`, `pg_rotate_logfile`, `pg_create_restore_point`, `pg_start_backup` / `pg_backup_start` and friends,
`pg_notify`, the `pg_stat_reset*` family and replication slot/origin functions; MySQL/MariaDB `GET_LOCK`, `RELEASE_LOCK`, `RELEASE_ALL_LOCKS`,
`BENCHMARK`, `MASTER_POS_WAIT` / `SOURCE_POS_WAIT` and the GTID wait functions (refused only when called, so a column called `benchmark` still
works). **`SLEEP()` and `pg_sleep()` stay allowed on purpose**: the command timeout (and cancellation) bounds them, and the integration tests
use them to prove exactly that. In MySQL/MariaDB `SET` directly after `CHARACTER` inside parentheses is the type clause of
`CAST(x AS CHAR CHARACTER SET latin1)` and is accepted (`SqlDialect.AllowsCharacterSetClause`); everywhere else, and through an ODBC engine
hint, `SET` is still refused. `CHARSET` needs no exemption because no `SET` follows it.

**MariaDB TLS.** `MySqlConnector` accepts a self-signed server certificate under `VerifyFull` for an account WITH a password when the server is
MariaDB 11.4 or newer: the password handshake (zero-configuration SSL) proves the server's identity from the password hash and the certificate
fingerprint, so the certificate chain is not what protects that connection. An account without a password has nothing to prove it with and is
refused with `Data.Error.CertificateNotTrusted`. This is the driver's behaviour, not Rotula's; the MySQL/MariaDB "Trust this server's certificate" help
(`Data.Option.TrustServerCertificate.Help.MariaDb`) says so, and an integration test pins it so a driver change is noticed.

**Login failures.** PostgreSQL and MariaDB on Windows close the connection right after refusing a login, and the driver sometimes reports the reset
instead of the refusal (about 3 in 100 PostgreSQL attempts). `IProviderErrorMapper.RejectedLoginShowsAsReset` (true for both) makes the engine
retry a connect that was reset within 3 seconds ONCE on a fresh connection; if the reset repeats, the error is
`Data.Error.AuthenticationFailed` with reason `Data.Error.AuthenticationFailed.Probable` ("the server closed the connection while logging in: the
user name or password is probably wrong"), never "server unreachable". A refused connection (nobody listening) is not a reset and stays
`Data.Error.HostUnreachable`. MySqlConnector wraps errors (`1042` around `-1` for a connect timeout, `1042` around `1045` for a slow refusal, `-1` around
`1317` for a command timeout), so the MySQL mapper reads the whole chain: a login refusal wins, then a timeout (`-1` is `CommandTimeoutExpired`), then
the outermost number; a TLS handshake failure (`AuthenticationException` anywhere in the chain) is `CertificateNotTrusted`. MariaDB answers an unknown
user by resetting the connection, and the driver then retries until the connect timeout, so that answer takes as long as the connect timeout
(10-20 s with the defaults); nothing Rotula can do makes the server answer faster.

**Values with no faithful .NET type.** Provider hooks (`FormatCell`) read them from the wire: PostgreSQL `numeric` (`NaN`, `Infinity`, any number of
digits, declared scale kept), `interval` (`1 year 2 mons 3 days 04:05:06.7`), `date` / `timestamp` / `timestamptz` before year 1 (`0044-03-15 BC`),
after 9999 or infinite (`infinity` / `-infinity`; Npgsql would silently print 9999-12-31 / 0001-01-01), `time` 24:00:00, `timetz` (`10:20:30+02:00`,
no made-up date) and bit strings (`1010`); MariaDB `DECIMAL` of up to 65 digits, zero dates (`0000-00-00`; the connection uses `AllowZeroDateTime`
instead of `ConvertZeroDateTime`, which turned them into 0001-01-01) and `TIME` beyond 24 hours (`838:59:59`). A value no reader can convert shows
`(unreadable value)` instead of failing the query. **Time zones:** a point in time known to be UTC (PostgreSQL `timestamptz`, a `DateTime` of kind
Utc) is written with a trailing `Z` and always with its time (`2024-03-05 00:00:00Z`); an explicit offset is written `+hh:mm`; a date and time
without zone information is written as stored. See `DatabaseValueFormatter`.

**Parameters.** PostgreSQL types parameters at their first use: `(@x IS NULL OR col = @x)` with a null value fails with SQLSTATE 42P18 ("could not
determine data type of parameter"); Rotula reports `Data.Error.ParameterTypeUnknown` with the hint `Data.Error.ParameterTypeUnknown.Hint` (cast the
parameter once, for example `@x::text`). A text value compared with a number or date column fails with 42883 ("operator does not exist"), reported as
`Data.Error.ParameterTypeMismatch` (hint `.Hint`) when the query has parameters; without parameters 42883 is an unknown function and stays a failed query.

**Describe.** PostgreSQL reports whether a column allows NULL only for `SchemaOnly | KeyInfo`, so `SqlDialect.DescribeWithKeyInfo` is set for it
alone (KeyInfo changes what SQL Server, ODBC and the packs run). MariaDB lists system-versioned tables as `TABLE_TYPE = 'SYSTEM VERSIONED'`, which the
catalogue query includes.

## Message keys added by this layer (for the UI to translate)

The keys live next to the code that raises them: `PackKeys`, `ProviderKeys`, `DataKeys.RejectAmbiguous`, the option keys in `StandardOptions` and the
descriptors, the preset names and hints in `Presets/presets.json`. Groups: `Data.Option.*` (field labels and help),
`Data.Preset.*` (names), `Data.Hint.*` (hints), `Data.Dialect.*` (engine names in the ODBC "database behind the driver" list), `Data.Pack.*` (pack
warnings and the approval prompt), `Data.Store.PasswordReenterRequired`, `Data.Error.ProviderUnavailable`, `Data.Query.Rejected.Ambiguous`.
Error keys added after the real-server run: `Data.Error.AuthenticationFailed.Probable` (reason), `Data.Error.ParameterTypeUnknown` and `Data.Error.ParameterTypeUnknown.Hint`,
`Data.Error.ParameterTypeMismatch` and `Data.Error.ParameterTypeMismatch.Hint`; option help `Data.Option.TrustServerCertificate.Help.MariaDb`.
