# Third-party notices

Rotula itself is MIT licensed (see [LICENSE](LICENSE)). It uses the components below. Versions and license expressions were
read from the project files and the license metadata in each package's `.nuspec`.

## Shipped with the application

| Component | Version | License | Used by | Project |
|---|---|---|---|---|
| SkiaSharp | 4.153.1 | MIT | Rotula.Rendering | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.Win32 (native Skia library, pulled in by SkiaSharp) | 4.153.1 | MIT | Rotula.Rendering | https://github.com/mono/SkiaSharp |
| ZXing.Net (barcode encoding only) | 0.16.11 | Apache-2.0 | Rotula.Rendering | https://github.com/micjahn/ZXing.Net |
| MiniExcel (reads `.xlsx`) | 1.46.0 | Apache-2.0 | Rotula.Core | https://github.com/mini-software/MiniExcel |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | Rotula.App | https://github.com/CommunityToolkit/dotnet |
| ModelContextProtocol (official C# SDK for the Model Context Protocol) | 2.2.0 | Apache-2.0 | Rotula.Mcp | https://github.com/modelcontextprotocol/csharp-sdk |
| ModelContextProtocol.Core (via ModelContextProtocol) | 2.2.0 | Apache-2.0 | Rotula.Mcp | https://github.com/modelcontextprotocol/csharp-sdk |
| Microsoft.Extensions.Hosting and the Microsoft.Extensions.* packages it pulls in (Configuration, Configuration.*, DependencyInjection, DependencyInjection.Abstractions, Diagnostics, Diagnostics.Abstractions, FileProviders.Abstractions, FileProviders.Physical, FileSystemGlobbing, Hosting.Abstractions, Logging, Logging.Abstractions, Logging.Configuration, Logging.Console, Logging.Debug, Logging.EventLog, Logging.EventSource, Options, Options.ConfigurationExtensions, Primitives, Caching.Abstractions) | 10.0.10 | MIT | Rotula.Mcp | https://github.com/dotnet/runtime |
| Microsoft.Extensions.AI.Abstractions (via ModelContextProtocol.Core) | 10.8.3 | MIT | Rotula.Mcp | https://github.com/dotnet/extensions |
| System.Configuration.ConfigurationManager, System.Security.Cryptography.Pkcs (via the Microsoft.Extensions.* packages) | 9.0.18 | MIT | Rotula.Mcp | https://github.com/dotnet/runtime |
| System.Diagnostics.EventLog (via Microsoft.Extensions.Logging.EventLog) | 10.0.10 | MIT | Rotula.Mcp | https://github.com/dotnet/runtime |
| Liberation Sans Regular / Bold and Liberation Mono Regular (embedded stand-in fonts) | 2.x | SIL Open Font License 1.1 | Rotula.Rendering | https://github.com/liberationfonts/liberation-fonts |

The MCP server (`Rotula.Mcp`) is a separate executable from the app. It uses the packages marked `Rotula.Mcp` above; its license 
expressions were read from each package's `.nuspec` in the restored NuGet cache (the SDK is Apache-2.0, used unmodified).

The Liberation fonts are embedded unmodified in `Rotula.Rendering`; their license text is in
`src/Rotula.Rendering/Fonts/LICENSE-LiberationFonts.txt` and is also embedded in the assembly. Apache-2.0 components are
used unmodified; the Apache License 2.0 text is available at https://www.apache.org/licenses/LICENSE-2.0.

The self-contained release builds additionally include the .NET runtime and the Windows Desktop (WPF) runtime, which are MIT
licensed by Microsoft and the .NET Foundation (https://github.com/dotnet/runtime, https://github.com/dotnet/wpf).

## Database access (Rotula.Data and everything it pulls in)

These are restored with `Rotula.Data` and therefore ship in the app and in the MCP server. Versions and license expressions were read from
each package's `.nuspec` in the NuGet cache.

| Component | Version | License | Why it is there |
|---|---|---|---|
| Microsoft.Data.SqlClient | 7.1.1 | MIT | SQL Server driver. https://github.com/dotnet/SqlClient |
| Microsoft.Data.SqlClient.Extensions.Abstractions, Microsoft.Data.SqlClient.Internal.Logging | 7.1.1 | MIT | Part of SqlClient 7. |
| **Microsoft.Data.SqlClient.SNI.runtime** (native SQL Server networking, `Microsoft.Data.SqlClient.SNI.dll` for win-x86/x64/arm64) | 7.1.0 | **Microsoft Software License Terms, "Distributable Code"** (not MIT; text in the package as `LICENSE.txt`) | SQL Server connections. Accepted by the owner on 2026-10-10; see "SQL Server networking component (SNI)" in `docs/decisions.md` for the conditions that apply to Rotula and to everyone who redistributes it. |
| Microsoft.SqlServer.Server | 1.0.0 | MIT | SqlClient dependency. |
| Microsoft.Bcl.Cryptography | 9.0.18 | MIT | SqlClient dependency. |
| Microsoft.Extensions.Caching.Abstractions, Caching.Memory, Options, Primitives | 9.0.18 | MIT | SqlClient dependencies. |
| Microsoft.Extensions.DependencyInjection.Abstractions, Logging.Abstractions | 10.0.0 | MIT | SqlClient dependencies. |
| Microsoft.IdentityModel.Abstractions, JsonWebTokens, Logging, Protocols, Protocols.OpenIdConnect, Tokens | 8.16.0 | MIT | SqlClient dependencies (token handling inside the driver). |
| System.IdentityModel.Tokens.Jwt | 8.16.0 | MIT | SqlClient dependency. |
| System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.Security.Cryptography.Pkcs, System.Threading.RateLimiting | 9.0.18 | MIT | SqlClient dependencies. |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | SQLite files. https://github.com/dotnet/efcore |
| SQLitePCLRaw.bundle_e_sqlite3, .core, .provider.e_sqlite3, .lib.e_sqlite3 (native `e_sqlite3` library) | 2.1.12 | Apache-2.0 (the bundled SQLite engine itself is in the public domain) | SQLite files. https://github.com/ericsink/SQLitePCL.raw |
| MySqlConnector | 2.6.2 | MIT | MySQL and MariaDB. https://github.com/mysql-net/MySqlConnector |
| Npgsql | 10.0.3 | PostgreSQL License | PostgreSQL and PostgreSQL-compatible services. https://github.com/npgsql/npgsql |
| System.Data.Odbc | 10.0.12 | MIT | Any ODBC driver the user installs. https://github.com/dotnet/runtime |
| System.Security.Cryptography.ProtectedData | 10.0.12 | MIT | Protects saved database passwords with Windows DPAPI. https://github.com/dotnet/runtime |

Rotula does not bundle any vendor ODBC driver (Microsoft Access Database Engine, SAP HANA, IBM Db2, Informix, Teradata, ...). The user
installs those, under their own licence.

### Optional provider packs (planned; nothing below ships yet)

Packs are separate folders next to the program, each with its own licence notices file (`pack.json` > `licenseNoticesFile`). The owner has
accepted the terms below for packs; each entry is completed (exact version, copyright, licence text copied into the pack) when that pack is
built. The decisions and conditions are recorded in `docs/decisions.md`.

| Pack | Component | License | Status |
|---|---|---|---|
| Oracle | Oracle.ManagedDataAccess.Core | Oracle Free Distribution, Hosting, and Use Terms and Conditions (not MIT) | Accepted 2026-10-10, as an optional pack only. Version and notice to be completed when the pack is built. |
| Firebird | FirebirdSql.Data.FirebirdClient | Initial Developer's Public License 1.0 (file-level copyleft) | Accepted 2026-10-10. To be completed with the pack. |
| Azure Cosmos DB | Microsoft.Azure.Cosmos (and the Microsoft Visual C++ runtime files it ships) | MIT; Microsoft redistribution terms for the VC++ files | Accepted 2026-10-10, as a pack. To be completed with the pack. |
| Microsoft Entra sign-in | Microsoft.Data.SqlClient.Extensions.Azure and its chain, including Microsoft.Identity.Client.NativeInterop (native `msalruntime.dll`) | MIT, except NativeInterop: Microsoft Software License Terms | Accepted 2026-10-10. To be completed with the pack. |

## Algorithms and tables adapted from other projects

No code is shipped as a separate package, but the following source was adapted into the application.

| Component | License | Where | What |
|---|---|---|---|
| libzint (zint barcode library), backend `rss.c`, `rss.h` and `general_field.c` | BSD-3-Clause | `src/Rotula.Rendering/Barcodes/DataBarEncoder.cs`, `DataBarTables.cs` | GS1 DataBar encoding (ISO/IEC 24724:2011) was ported to C#, including the element width tables transcribed from the standard. The BSD-3-Clause notice is kept at the top of those files. https://github.com/zint/zint |

The reference modules in `tests/Rotula.Tests/SymbDataBarVectors.cs` come from libzint's test suite (`backend/tests/test_rss.c`,
BSD-3-Clause, Copyright (c) Robin Stuart), whose expected results were verified against ISO/IEC 24724:2011 and the GS1 General
Specifications.

## Build and test only (not shipped)

| Component | Version | License |
|---|---|---|
| xunit (and xunit.core / assert / extensibility packages) | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Microsoft.CodeCoverage (via the test SDK) | 17.14.1 | MIT |
| Microsoft.TestPlatform.* (via the test SDK) | 17.14.1 | MIT |
| Newtonsoft.Json (via the test SDK) | 13.0.3 | MIT |
| coverlet.collector | 6.0.4 | MIT |
| dotnet-reportgenerator-globaltool (CI only, installed on demand) | latest | Apache-2.0 |
| Inno Setup (CI only, builds the installer) | 6.x | Inno Setup License (free, redistribution of the tool is not part of Rotula) |

## Data

The label stock library (`src/Rotula.Core/Stocks/stocks.json`) is data, not code. It holds part numbers and label
dimensions (width, height, labels across, corner radius, media type) and nothing else about the products: no prices,
images or marketing text. Its sources are described factually here and per entry in the file's `sources` list.

| Source | What was taken | Where it applies |
|---|---|---|
| Zebra ZipShip catalogue (public PDF, effective May 2022) | Part numbers, label sizes, core size, labels per roll, as printed in the catalogue; the page is cited in each entry's `sourceUrl` | Entries with source `zipship-catalogue-pdf` |
| A stock library table exported from the ZebraDesigner label program, supplied by the project maintainer | Part numbers, stock family, label width and height, labels across, rows, gap between labels across and corner radius. Printer settings, offsets, images and author fields are not used | Entries with source `zebradesigner-stock-export` |
| Public Avery and reseller product pages | Label size and labels per sheet of three sheet labels; the page is cited in each entry's `sourceUrl` | Entries `avery-*` |
| Common shipping, ISO and GS1 label sizes | Standard sizes quoted on the public page each entry cites, no vendor data | Entries `iso-*`, `ship-*`, `gs1-*` |

The project maintainer determined that part numbers and label dimensions are public product data. No licence is claimed
beyond that. Brand and product names belong to their owners; Rotula is not affiliated with them. Method, known limits and
the contribution rules are in [docs/stock-catalog.md](docs/stock-catalog.md).

## Copyright holders

- SkiaSharp: Copyright (c) Microsoft Corporation and contributors.
- ZXing.Net: Copyright ZXing.Net contributors (Michael Jahn and others).
- MiniExcel: Copyright Wei Lin, 2021 onwards.
- CommunityToolkit.Mvvm: Copyright (c) .NET Foundation and Contributors.
- ModelContextProtocol: Copyright (c) Model Context Protocol a Series of LF Projects, LLC.
- libzint: Copyright (C) 2008-2026 Robin Stuart <rstuart114@gmail.com>.
- Liberation fonts: Digitized data copyright (c) 2010 Google Corporation; copyright (c) 2012 Red Hat, Inc.
