# Oracle add-on for Rotula (optional)

Lets Rotula read data from an **Oracle Database** (12c and later) to fill label fields, with Oracle's own managed driver (ODP.NET Core).
No Oracle client has to be installed.

This add-on is optional and separate from Rotula on purpose: the Oracle driver is **not open source**. It is distributed under the
"Oracle Free Distribution, Hosting, and Use Terms and Conditions" (see `NOTICES.txt`). Keep this folder exactly as it is (do not replace
or edit `Oracle.ManagedDataAccess.dll`), keep `NOTICES.txt` next to it, and do not charge a fee for the add-on itself.

## Install

Copy this whole folder into the `packs` folder next to `Rotula.exe` (or into `%AppData%\Rotula\packs`; Rotula then asks you to approve it
once). Restart Rotula. "Oracle Database" appears in the list of data sources.

## What you enter

| Field | Meaning |
|---|---|
| Server address, Port | The database server. Port: 2484 when "Encrypt" is on, 1521 when it is off, unless you enter one (many TLS listeners use 1522). |
| Service name | The service the listener offers (`ORCLPDB1`). Under advanced options you can say the name is a SID, or a name from `tnsnames.ora` (then also give its folder). |
| User name | The account. The password is stored by Rotula's protected password store. "Windows sign-in" uses Oracle's operating-system authentication (`User Id=/`). |
| Encrypt the connection (TLS) | On by default: TCPS, and the certificate must match the server name. Turn it off only for a listener without TLS (then ask your administrator about Oracle native network encryption). |
| Do not check the server name (advanced) | Keeps TLS on but skips the host name check. |
| Wallet folder (advanced) | An Oracle wallet with the trusted certificates, when the Windows certificate store is not enough. |

Every part of the connection is checked before it is put into the connect descriptor (letters, digits and `. - _ $ #` only for names), so a
value cannot add settings of its own.

## What keeps your data safe (read-only)

1. Rotula's query check refuses everything that is not one `SELECT`: also `UTL_*`, `DBMS_*` (except `DBMS_LOB` and `DBMS_RANDOM`), `OWA_*`,
   `HTTPURITYPE`, calls into `SYS.`, `WITH FUNCTION` / `WITH PROCEDURE`, database links (`table@link`, also quoted), `FOR UPDATE`, sequences.
2. Every query runs after `SET TRANSACTION READ ONLY`, the first statement of its transaction, and is rolled back.
3. **Please connect with an account that has SELECT rights only.** A read-only transaction does not stop code that runs its own autonomous
   transaction, and the query check cannot see inside a stored function.

## Tested (without an Oracle server)

No Oracle Database is available on the build computer, so these were tested **without a server**: the connect descriptor and connection
string (TLS, SID, alias, port, wallet), refusal of descriptor injection, `:name` parameters and `BindByName`, quoting, the query check
(vendor-documented q-quote delimiters, comments, hints, `WITH FUNCTION`, links, packages), the mapping of 24 ORA error numbers to the
stable messages, and a **real driver exception** (connecting to a port nobody listens on, which the managed driver reports as `ORA-50201`
wrapping `ORA-12541`; the pack digs out the real number).

### Still UNVERIFIED against a real server (smoke test before relying on it)

* That `SET TRANSACTION READ ONLY` is accepted as the first statement after the driver starts its transaction (if the driver sent an
  isolation statement first, Oracle would answer ORA-01453 and the query would fail, never write).
* TLS: the certificate check through the Windows store or a wallet, `SSL_SERVER_DN_MATCH`, and the `MY_WALLET_DIRECTORY` setting of the
  connect descriptor.
* `tnsnames.ora` aliases (`OracleConfiguration.TnsAdmin` is process-wide in the driver).
* Operating-system authentication, NUMBER columns with 38 digits, TIMESTAMP WITH TIME ZONE formatting.
* Oracle names .NET 8 as the newest supported .NET; the driver loads and runs on .NET 10 (the connection attempt above went through the
  whole network stack) but Oracle has not stated support.
* Oracle's licence mentions data sent by an installer or auto-update, "if any". A NuGet library has neither; no network trace was made.

A manual smoke test: add a connection, press "Test", run `SELECT * FROM DUAL`, then try `SELECT UTL_HTTP.REQUEST('http://localhost') FROM DUAL`
(must be refused by Rotula) and, with a privileged account, `SELECT * FROM T FOR UPDATE` (refused).

## Licence

Oracle.ManagedDataAccess.Core 23.26.301: Oracle Free Distribution, Hosting, and Use Terms and Conditions. The Microsoft .NET libraries in
this folder are MIT. All texts are in `NOTICES.txt`.
