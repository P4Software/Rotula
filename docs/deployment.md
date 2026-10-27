# Deploying Rotula in an organization

This page is for administrators who install Rotula on many computers: silent installs, the machine-wide MSI, Intune,
Group Policy and Configuration Manager, and the policy settings that fix Rotula's settings for users.

## Which package to use

Every release has two installers, both signed by **Barrdega Sistemas NA LLC** (check with `Get-AuthenticodeSignature`, see
[releasing.md](releasing.md#verifying-a-signature)) and listed in `SHA256SUMS.txt`:

| Package | Installs to | Rights | Best for |
|---|---|---|---|
| `Rotula-X.Y.Z-win-x64.msi` | `C:\Program Files\Rotula`, for every user | Administrator (SYSTEM when deployed) | Intune, Group Policy software installation, Configuration Manager, any tool that deploys MSI packages |
| `Rotula-X.Y.Z-win-x64-setup.exe` (Inno Setup) | `%LocalAppData%\Programs\Rotula` for the current user, or `C:\Program Files\Rotula` with `/ALLUSERS` | None (per user) or administrator (`/ALLUSERS`) | People installing it themselves; per-user deployment scripts |

Use **one** package per computer. The MSI and the Inno Setup installer are separate products in Apps & features (different
product codes), so a computer that has both shows Rotula twice. To move a computer from the per-user installer to the MSI,
uninstall the per-user copy first (command below). Settings, saved connections and counters live in `%AppData%\Rotula` and
are kept either way.

Both packages install the same files: `Rotula.App.exe` (the designer), `Rotula.Mcp.exe` (the MCP server for AI assistants,
see [mcp.md](mcp.md)), `rotula.exe` (the print engine's command line and REST API, see [print-engine.md](print-engine.md)), the
licence files, `docs\mcp.md`, `docs\print-engine.md`, the document icon and an empty `packs` folder for data source
add-ons. Both register `.rotula` files to open with Rotula and add a Start menu entry. Neither installs a service, a
scheduled task, a driver or a browser extension, and neither needs a reboot.

**Licence terms.** Rotula is MIT, but it ships the SQL Server networking component under Microsoft's own licence terms. The
Inno Setup installer shows those terms and the user accepts them; the MSI has no wizard, so by deploying it you accept
`LICENSE-THIRD-PARTY.txt` (installed next to the program and attached to every release) on behalf of your users. See
[releasing.md](releasing.md#the-licence-page-and-the-sql-server-networking-component).

## The MSI

### Install

```
msiexec /i Rotula-X.Y.Z-win-x64.msi /qn /norestart /l*v "%TEMP%\Rotula-install.log"
```

`/qn` is completely silent; `/qb!` shows a small progress bar without a Cancel button. The command must run elevated
(Intune, Group Policy and Configuration Manager run it as SYSTEM). Public properties:

| Property | Default | Meaning |
|---|---|---|
| `INSTALLFOLDER` | `C:\Program Files\Rotula` | Where the program goes, for example `INSTALLFOLDER="D:\Apps\Rotula"` |
| `ASSOCIATE` | `1` | `0` leaves the `.rotula` file association alone |

Example: `msiexec /i Rotula-X.Y.Z-win-x64.msi /qn ASSOCIATE=0`.

### Upgrade

Install the newer MSI with the same command. The package has a fixed **UpgradeCode**
`{4B2C6076-5B82-411C-B751-EC4B70BEE9C6}` and a new **ProductCode** for every version (read it from the MSI with Orca, or from
the winget manifest of the release). The newer package removes the older one in the same transaction. Installing an older
version over a newer one is refused (error 1603 with "A newer version of Rotula is already installed" in the log).

### Uninstall

```
msiexec /x {ProductCode} /qn /norestart
```

or `msiexec /x Rotula-X.Y.Z-win-x64.msi /qn`. Uninstalling removes the program folder, the Start menu entry and the
file association. It never touches users' settings in `%AppData%\Rotula`.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 3010 | Success, a restart is required (Rotula itself never asks for one; another pending install can cause it) |
| 1602 | The user cancelled |
| 1603 | Fatal error: read the log (`/l*v`) |
| 1618 | Another installation is already running; try again later |
| 1619 | The MSI file could not be opened (wrong path, blocked download) |
| 1625 | Installation blocked by policy |
| 1925 | Not elevated: run the command as administrator |

## The Inno Setup installer

### Install silently

```
Rotula-X.Y.Z-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG="%TEMP%\Rotula-setup.log"
```

That installs for the **current user** without administrator rights. For **all users** (Program Files, needs elevation) add
`/ALLUSERS`:

```
Rotula-X.Y.Z-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS
```

Other switches:

| Switch | Meaning |
|---|---|
| `/SILENT` | Progress window only, no questions (instead of `/VERYSILENT`) |
| `/DIR="D:\Apps\Rotula"` | Install folder |
| `/TASKS="associate"` | Choose the tasks; leave `associate` out to skip the `.rotula` association |
| `/MERGETASKS="desktopicon"` | Also add a desktop icon (off by default) |
| `/LANG=spanish` | Language of the installer itself (`english`, `spanish`, `brazilianportuguese` or `french`) |
| `/CURRENTUSER` | Force a per-user install even when run elevated |

The licence page is accepted automatically by `/VERYSILENT`, so the same note about the licence terms applies as for the MSI.

### Uninstall silently

The uninstaller is `unins000.exe` in the install folder:

```
"%LocalAppData%\Programs\Rotula\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
"C:\Program Files\Rotula\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

The first line is a per-user install, the second an `/ALLUSERS` one. The uninstall entry is
`{6B0E5C1A-3F7D-4C52-9A1E-2D8F4B7A9C31}_is1` under `...\CurrentVersion\Uninstall` (HKCU for per-user, HKLM for all users);
its `QuietUninstallString` value holds the exact command.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Setup could not start |
| 2 | Cancelled before the installation began |
| 3 | Fatal error while preparing |
| 4 | Fatal error during the installation |
| 5 | Cancelled during the installation |
| 7 | Preparing to install failed (for example a file in use) |
| 8 | Preparing to install failed and a restart is needed first |

## Microsoft Intune

### As a Win32 app (recommended)

1. Put `Rotula-X.Y.Z-win-x64.msi` alone in a folder and wrap it with the
   [Microsoft Win32 Content Prep Tool](https://learn.microsoft.com/intune/intune-service/apps/apps-win32-prepare):
   `IntuneWinAppUtil.exe -c <folder> -s Rotula-X.Y.Z-win-x64.msi -o <output>`.
2. In the Intune admin center: **Apps > Windows > Add > Windows app (Win32)** and upload the `.intunewin` file. Intune
   reads the name, publisher, version and ProductCode from the MSI and fills the install and uninstall commands:
   - Install: `msiexec /i "Rotula-X.Y.Z-win-x64.msi" /qn /norestart`
   - Uninstall: `msiexec /x {ProductCode} /qn /norestart`
   - Install behavior: **System**. Device restart behavior: **Determine behavior based on return codes** (the defaults map
     0, 1707, 3010, 1641 and 1618 correctly).
3. **Detection rule**: choose *Manually configure detection rules > MSI*, with the ProductCode Intune filled in. To detect
   "this version or newer" instead, use a *File* rule: path `%ProgramFiles%\Rotula`, file `Rotula.App.exe`,
   *String (version)*, *Greater than or equal to* `X.Y.Z`.
4. For a later version, add it as a new app and set **Supersedence** to the previous one with *Uninstall previous version*
   off: the MSI upgrades in place.

The per-user Inno Setup installer can also be packaged as a Win32 app with install behavior **User**, the command
`Rotula-X.Y.Z-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`, and a file detection rule on
`%LocalAppData%\Programs\Rotula\Rotula.App.exe`.

### As a line-of-business app

**Apps > Windows > Add > Line-of-business app** accepts the MSI directly. It is simpler, but Win32 apps give you detection
rules, return codes and supersedence, so prefer Win32 when mixing with other app types.

### Policy settings with Intune

Either way below writes the same registry values as Group Policy, so Rotula behaves the same.

- **Imported administrative templates** (recommended): **Devices > Configuration > Import ADMX**, upload
  `packaging/policies/Rotula.admx` with `packaging/policies/en-US/Rotula.adml`. Then create a profile of type
  **Templates > Imported Administrative templates** and set the policies under *P4 Software > Rotula*, in Computer
  Configuration (for every user of the device) or User Configuration.
- **Custom OMA-URI (ADMX ingestion)**, for tenants without ADMX import:
  1. Ingest the template: OMA-URI `./Device/Vendor/MSFT/Policy/ConfigOperations/ADMXInstall/Rotula/Policy/RotulaAdmx`, data type
     *String*, value: the whole content of `Rotula.admx`.
  2. Set a policy: OMA-URI `./Device/Vendor/MSFT/Policy/Config/Rotula~Policy~P4Software~Rotula/<PolicyName>`, data type
     *String*, value `<disabled/>`, `<enabled/>`, or for a policy with a value
     `<enabled/><data id="Language" value="es"/>` (Language) or `<enabled/><data id="DefaultPrinter" value="Warehouse ZD420"/>`.
     Use `./User/...` instead of `./Device/...` for a per-user policy.
- **Settings catalog / remediation script**: where neither is possible, a PowerShell script can write the values in the
  table below directly under `HKLM\SOFTWARE\Policies\P4Software\Rotula`.

## Group Policy

### Software installation

1. Copy the MSI to a share that the computer accounts can read, for example `\\fileserver\Software$\Rotula\`.
2. In the Group Policy Management Editor: **Computer Configuration > Policies > Software Settings > Software installation >
   New > Package**, choose the MSI through its UNC path and pick **Assigned**. It installs at the next restart.
3. For a new version, add the new MSI to the same GPO and, on its **Upgrades** tab, mark it as upgrading the previous
   package (*Package can upgrade over the existing package*).

### Administrative templates

Copy `packaging/policies/Rotula.admx` to `\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions\` (the central store) and
`en-US\Rotula.adml` and `es-ES\Rotula.adml` to the language folders below it. For one computer, use
`C:\Windows\PolicyDefinitions` instead. The settings appear under **Administrative Templates > P4 Software > Rotula** in both
Computer Configuration and User Configuration.

## Configuration Manager

Create an application with the deployment type **Windows Installer (\*.msi file)** and point it at the MSI. Configuration
Manager reads the ProductCode for the detection method and fills
`msiexec /i "Rotula-X.Y.Z-win-x64.msi" /qn` and the uninstall command. Set the installation behavior to **Install for system**,
the logon requirement to **Whether or not a user is logged on**, and add the new version's application with a
**supersedence** relationship to the old one (without uninstalling it first: the MSI upgrades in place).

## Policy settings

Rotula reads the policies once at start-up from `HKLM\SOFTWARE\Policies\P4Software\Rotula` (the computer) and
`HKCU\SOFTWARE\Policies\P4Software\Rotula` (the user). When both set the same value, **the computer's value wins**. A
setting fixed by a policy is shown disabled in Rotula with the note "Managed by your organization", and the user's own
choice is kept in `settings.json` for the day the policy is removed. The MCP server (`Rotula.Mcp.exe`) and the print engine (`rotula.exe`) read the same key.

| Policy (template name) | Registry value | Type | Values | Effect |
|---|---|---|---|---|
| Language of Rotula | `Language` | REG_SZ | `auto`, `en`, `es`, `pt-BR`, `fr-CA` | Sets the language and disables **View > Language**. `auto` follows the Windows display language (any Portuguese gets `pt-BR`, any French `fr-CA`). Case does not matter |
| Check for updates | `UpdateCheck` | REG_DWORD | `1` on, `0` off | Switches the daily update check on or off and disables **Help > Check for updates once a day**. Not set: each user decides (off until they switch it on) |
| Default printer | `DefaultPrinter` | REG_SZ | A Windows printer name | Preselected in the Print dialog until the user picks a printer. Not locked; ignored quietly if the printer is not installed |
| Allow data source add-ons from the user's folder | `UserAddOns` | REG_DWORD | `0` blocks | Add-ons in `%AppData%\Rotula\packs` are neither loaded nor offered for approval; the installed `packs` folder still works ([data-providers.md](data-providers.md)) |
| Allow the MCP server to print | `McpPrinting` | REG_DWORD | `0` blocks | The MCP server refuses every print request, whatever its settings file says |
| Allow the MCP server to read databases | `McpDatabase` | REG_DWORD | `0` blocks | The MCP server refuses every database query |
| Allow the print engine to print | `PrintEngine` | REG_DWORD | `0` blocks | `rotula.exe` refuses to print or query printers (exit code 5) and its REST server answers 403, whatever its settings file says ([print-engine.md](print-engine.md)) |

A value that Rotula does not understand (a language other than the five values above, a number other than 0 or 1) is ignored, so
a typo never forces a wrong setting on users. The MCP policies can only take something away: setting them to `1` does not
switch printing or database access on; the MCP server's own settings file still has to allow it ([mcp.md](mcp.md)).

Example with PowerShell (elevated), for a whole computer:

```powershell
$key = 'HKLM:\SOFTWARE\Policies\P4Software\Rotula'
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name Language -Value 'es' -Type String
Set-ItemProperty -Path $key -Name UpdateCheck -Value 0 -Type DWord
Set-ItemProperty -Path $key -Name McpPrinting -Value 0 -Type DWord
```

Restart Rotula after a policy change.

## Network traffic and the update check

Rotula makes no network request of its own unless the update check is switched on, by the user (**Help > Check for updates
once a day**) or by the *Check for updates* policy. Otherwise the only traffic is what the user asks for: a label sent to a
network printer, and queries to the databases the user connects.

When the update check is on, Rotula sends at most one request a day: an anonymous HTTPS `GET` of
`https://api.github.com/repos/P4Software/Rotula/releases/latest`, with the User-Agent `Rotula` (no version) and no cookies,
no identifiers and nothing about the user or the computer. It uses the Windows proxy settings, with the signed-in user's
credentials for a proxy that asks for them. When a newer version exists, a notice under the toolbar links to its release page
on GitHub. Rotula never downloads or installs anything itself: updating stays with you (a new MSI through your deployment
tool, or `winget upgrade P4Software.Rotula`). To allow the check through a firewall, allow `api.github.com` on port 443.
A failed check is silent and is tried again the next day.

## winget

`winget install P4Software.Rotula` installs the per-user package; `winget install P4Software.Rotula --scope machine` installs
the MSI for all users (winget asks for administrator rights). The package is not in the winget catalogue yet; see the README.
