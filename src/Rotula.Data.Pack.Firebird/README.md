# Firebird add-on for Rotula

Lets Rotula read data from a **Firebird** database server (versions 2.5 to 5.x) to fill label fields. It talks to the server over the
network with the Firebird .NET driver; no Firebird client library has to be installed.

## Install

Copy this whole folder into the `packs` folder next to `Rotula.exe` (or into `%AppData%\Rotula\packs`; Rotula then asks you to approve it
once). Restart Rotula. "Firebird" appears in the list of data sources.

## What you enter

| Field | Meaning |
|---|---|
| Server address, Port | The computer running Firebird; port 3050 unless your administrator changed it. |
| Database path or alias on the server | Either the path **as the server sees it** (`C:\data\sales.fdb`) or an alias from the server's `databases.conf`. |
| User name | Sign-in account. The password is stored by Rotula's protected password store, never in the connection list. |
| Encrypt the connection | On by default. Firebird encrypts the whole connection (not TLS certificates). With a user name and password the encryption is **required**: if the server does not offer it, the connection fails instead of falling back to plain text. |
| SQL role (advanced) | A role with only SELECT rights is recommended. |

Windows sign-in (the server's `Win_Sspi` plugin) is offered too; it cannot carry an encryption key, so there the connection is encrypted
when the server can and is not required to be.

## What keeps your data safe (read-only)

1. Rotula's query check refuses everything that is not one `SELECT`, `GEN_ID` / `NEXT VALUE FOR` (they change a generator),
   `RDB$SET_CONTEXT`, `WITH LOCK` / `FOR UPDATE`, `EXECUTE BLOCK`.
2. Every query runs in a **READ ONLY snapshot transaction** (`isc_tpb_read`, `isc_tpb_concurrency`). The server itself refuses writes,
   also for the administrator, and also from a selectable procedure that writes, also with `IN AUTONOMOUS TRANSACTION` (checked on 5.0.4).
3. **Please connect with an account that can only read.** A selectable procedure cannot be recognised by looking at the text, and
   older server versions were not tested for the autonomous-transaction case.

## Tested

Against a real Firebird 5.0.4 server run for the tests (localhost only): reading with accents / Japanese / emoji (UTF-8), parameters,
row cap, catalogue (tables, views, columns), values of INT128, DECIMAL, DATE, TIME, TIMESTAMP, BOOLEAN, BLOB, DECFLOAT (the same text in every
regional setting), wrong password, unknown user, missing database, database outside the server's allowed folders, wrong port,
missing table right, long query timeout and cancellation, the connection really being encrypted, and the read-only transaction refusing
inserts, deletes, updates and writing procedures **as SYSDBA**. The guard and the engine were also compared for q-quotes and comments.

**Not tested:** Firebird 2.5, 3.0 and 4.0 servers, Windows sign-in (`Win_Sspi`), servers with `WireCrypt = Disabled` (then the connection
fails by design), the embedded engine (not offered: connect to `localhost`).

### Running the Firebird tests yourself

The tests skip when no server is running. To run them, start a Firebird 5 server for tests (the official zip from
https://github.com/FirebirdSQL/firebird/releases, SHA-256 of `Firebird-5.0.4.1812-0-windows-x64.zip` is
`01e844fce4d5f53272a76205dbc3a1ba4b782ab8e8eadcd808cdbccd9ce13b72`) bound to 127.0.0.1 with its own data folder, create the SYSDBA user and an
empty database, and write `%LOCALAPPDATA%\Rotula\pack-tests\servers.json`:

```json
{ "firebird": { "host": "127.0.0.1", "port": 33050, "adminUser": "SYSDBA", "adminPassword": "...", "database": "E:/path/to/TEST.FDB" } }
```

The tests create their own tables, a `ROTULA_READER` account and remove them again.

## Licence

FirebirdSql.Data.FirebirdClient is under the **Initial Developer's Public License 1.0** (file-level copyleft). It is shipped unmodified;
its source is at https://github.com/FirebirdSQL/NETProvider. The licence text is in `NOTICES.txt`; keep that file with this folder.
