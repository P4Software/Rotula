; Inno Setup script for the Rotula installer.
; Built by .github/workflows/release.yml:
;   ISCC.exe /DAppVersion=0.1.0 /DSourceDir=<publish folder> /DOutputDir=<out> packaging\installer\Rotula.iss
; The installer is per-user (no administrator rights) and unsigned; see the README for the SmartScreen note.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

[Setup]
; AppId must never change: it is how Windows and winget recognise upgrades of the same product.
AppId={{6B0E5C1A-3F7D-4C52-9A1E-2D8F4B7A9C31}
AppName=Rotula
AppVersion={#AppVersion}
AppVerName=Rotula {#AppVersion}
AppPublisher=P4 Software
AppPublisherURL=https://github.com/P4Software/Rotula
AppSupportURL=https://github.com/P4Software/Rotula/issues
AppUpdatesURL=https://github.com/P4Software/Rotula/releases
DefaultDirName={autopf}\Rotula
DefaultGroupName=Rotula
DisableProgramGroupPage=yes
; Per-user install: no UAC prompt, and winget can install it silently without elevation.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#SourceDir}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=Rotula-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Rotula.App.exe
UninstallDisplayName=Rotula
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Rotula.App.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Rotula"; Filename: "{app}\Rotula.App.exe"
Name: "{autodesktop}\Rotula"; Filename: "{app}\Rotula.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Rotula.App.exe"; Description: "{cm:LaunchProgram,Rotula}"; Flags: nowait postinstall skipifsilent
