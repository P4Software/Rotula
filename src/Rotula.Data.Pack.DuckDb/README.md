# DuckDB add-on for Rotula

Lets Rotula read data from a **DuckDB database file** (`.duckdb`, `.db`) to fill label fields. DuckDB runs inside Rotula: there is no server,
no account and no network connection.

## Install

Copy this whole folder into the `packs` folder next to `Rotula.exe` (or into `%AppData%\Rotula\packs`; Rotula then asks you to approve it
once). Restart Rotula. "DuckDB file" appears in the list of data sources.

## What you enter

| Field | Meaning |
|---|---|
| Database file | The `.duckdb` file on this computer. Network paths (`\\server\share`), web addresses, `:memory:` and `md:` (MotherDuck cloud) are refused. |
| Memory limit (MB), advanced | Optional upper bound for the memory DuckDB may use for one query. |

## What keeps your data safe (read-only)

Rotula opens the file **read-only** and locks the engine's settings **while opening it**, so a query can never switch the protections off:

* `access_mode=READ_ONLY`: the engine itself refuses every write, `CHECKPOINT` and schema change.
* `enable_external_access=false`: no `read_csv`, `read_parquet`, `read_text`, `glob`, `FROM 'file.csv'`, `COPY`, `ATTACH`, `EXPORT`,
  no loading or installing extensions, no web access.
* `autoinstall_known_extensions=false`, `autoload_known_extensions=false`, no community or unsigned extensions: nothing is downloaded.
* `lock_configuration=true`: `SET ...`, `RESET ...` fail ("the configuration has been locked").
* Rotula's own query check refuses everything that is not one `SELECT`, plus the file-reading functions, before the engine sees the text.
* Every query runs in a transaction that is rolled back.

Only one SELECT per query. Use `$name` or `@name` for values you want to merge in (`@name` is what Rotula's data merge writes).

## Tested

Everything in this list was run against the real DuckDB engine (version 1.5.6, bundled) by `tests\Rotula.Data.Packs.Tests`:
read-only enforcement by the engine with the query check bypassed, the protections cannot be changed from SQL, external files / network /
extensions are blocked, unicode (accents, n-tilde, Japanese, emoji), parameters, row cap, streaming, catalogue (tables, views,
columns), timeout and cancellation of a long query, errors (missing file, damaged file, bad SQL), and the value formats of HUGEINT,
DECIMAL(38), TIMESTAMPTZ, INTERVAL, UUID, LIST, STRUCT and BLOB (the same text in every regional setting).

Known limits: a `TIMESTAMPTZ` is shown in UTC; a `STRUCT` / `MAP` is shown as `[key, value], [key, value]`; the command timeout is
enforced by Rotula (the engine has none), so it is the connect plus command timeout and a few seconds.

## Licence

DuckDB (MIT), DuckDB.NET (MIT), Apache Arrow (Apache-2.0). All texts are in `NOTICES.txt`; keep that file with this folder.
