# ClickHouse add-on for Rotula

Lets Rotula read data from a **ClickHouse** server to fill label fields, over ClickHouse's HTTP(S) interface, with ClickHouse's own .NET
driver (`ClickHouse.Driver`, MIT).

## Install

Copy this whole folder into the `packs` folder next to `Rotula.exe` (or into `%AppData%\Rotula\packs`; Rotula then asks you to approve it
once). Restart Rotula. "ClickHouse" appears in the list of data sources.

## What you enter

| Field | Meaning |
|---|---|
| Server address, Port | The ClickHouse server. 8443 (HTTPS) when "Encrypt" is on, 8123 (plain HTTP) when it is off, unless you enter one. |
| Database | Optional starting database. |
| User name | The account. The password is stored by Rotula's protected password store. |
| Encrypt the connection | On by default (HTTPS). Plain HTTP is for a trusted local server only. |
| Trust this server's certificate (advanced) | Accept a certificate that cannot be verified. Weakens security. |
| Certificate authority file (advanced) | A PEM file of the authority that issued the server certificate; the server name must still match. |
| Let the server stop long queries (advanced) | Off by default; see "What keeps your data safe", point 3. |

## What keeps your data safe (read-only)

1. **`readonly=1` is sent with every request**, exactly once and as the last URL parameter (a handler in front of the driver removes any
   other `readonly` and appends it). The server then refuses writes, DDL, changing that setting, and most table functions. (A plain HTTP
   GET would only get `readonly=2`, where the session may still change settings and use table functions; that is why `readonly=1` is explicit.)
2. Rotula's query check refuses everything that is not one `SELECT`, `SETTINGS`, and the table functions that read files, the network or other
   servers (`url`, `file`, `s3`, `remote`, `mysql`, `postgresql`, `jdbc`, `odbc`, `hdfs`, `executable`, `input`, `cluster`, `iceberg`, ...). A column
   named `file` or `url` is fine; only a call is refused.
3. Only the settings that a read-only account may still send travel with a request (`database`, `default_format`, the query id and the
   query parameters). No session, no compression, no output-format toggles, and **no time limit by default**: a profile with `readonly=1`
   refuses a setting whose value changes ("Cannot modify 'max_execution_time' setting in readonly mode", code 164), which would make every query of the
   recommended account fail. If your account may change settings, turn on "Let the server stop long queries" (advanced) to send `max_execution_time`
   with the command timeout. Without it Rotula stops waiting at the timeout, but a cancelled query may keep running on the server until it finishes
   (`cancel_http_readonly_queries_on_client_close` is also a setting and cannot be sent either).
4. **Please connect with an account whose profile is `readonly=1`** (or an ordinary account: Rotula then makes the request read-only itself).
   An account whose profile is `readonly=2` cannot be used: ClickHouse forbids changing `readonly` from 2, so every query is refused with a
   permission error. Ask the administrator for `readonly=1` instead.

How this was established: read from ClickHouse's open-source code (`src/Access/SettingsConstraints.cpp`: a changed setting is refused under
`readonly=1` unless it is `changeable_in_readonly` or in the built-in list `database`, `default_format`, ...; `readonly` cannot be changed when it
is above 1; an unchanged value is skipped; `src/Server/HTTP/setReadOnlyIfHTTPMethodIdempotent.cpp`: only non-POST requests are forced to 2; and
`src/Server/HTTPHandler.cpp`: the URL settings are checked one by one against the settings in force before the request, then applied together). This is
**source-verified, not server-verified**.

Backslashes: ClickHouse reads a backslash as an escape inside strings **and** quoted names, so a name that ends in `\` is quoted as `\\`, and Rotula
refuses queries where a backslash makes the end of a string ambiguous. Write `''` for a quote inside a string.

## Tested (without a server)

ClickHouse has no native Windows build, so no server was available. Tested **without a server**: the connection string, the real HTTP
requests that the real driver produces (captured by a fake network layer): `readonly=1` once and last on every request, no other settings
than the ones listed, typed server parameters (`@id` becomes `{id:Int32}` with the value in `param_id`, never in the text, not inside string
literals), credentials in headers not in the URL, the mapping of server replies (codes 516, 497, 164, 81, 159, 60, 62, 394 and HTTP
statuses) to the stable messages, network and TLS failures, cancellation, the certificate rules (own CA file accepted only for its
certificates, a wrong name never accepted), identifier quoting, and the query check.

### UNVERIFIED against a real server

* That a real server accepts the request exactly as the driver builds it (everything above comes from reading the server's source and
  from the captured requests, not from a running server). If a server refused a setting ("Cannot modify ... in readonly mode"), every query
  would fail loudly; nothing would be written.
* That rows beyond the cap stop the download, and that the catalogue query works for your version (`system.tables`).
* The exact text of server messages (only the numeric codes are used).
* Behaviour with ClickHouse Cloud and proxies.

Manual smoke test: add the connection, press "Test", run `SELECT 1`, then `SELECT * FROM numbers(5)`; `INSERT ...` and
`SELECT * FROM url('http://localhost', CSV)` must be refused by Rotula.

## Licence

ClickHouse.Driver (MIT, with vendored MIT code), NodaTime (Apache-2.0), Microsoft libraries (MIT). All texts are in `NOTICES.txt`.
