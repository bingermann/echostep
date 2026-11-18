; Echostep installer (Inno Setup 6). Built automatically by GitHub Actions (.github/workflows/release.yml);
; nobody installing Echostep needs Inno Setup, the .NET SDK or a command prompt.
;
; Builds from dist\Echostep.exe (made by build.cmd / dotnet publish). Version comes from /DAppVersion=x.y.z.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6E3F2B71-5C4A-4E8B-9C1D-3A7E5B0C1D42}
AppName=Echostep
AppVersion={#AppVersion}
AppVerName=Echostep {#AppVersion}
AppPublisher=Echostep
VersionInfoVersion={#AppVersion}
; Installs for the current user by default (no admin prompt); the first page offers "all users" instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Echostep
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
SetupIconFile=..\src\Echostep\app.ico
UninstallDisplayIcon={app}\Echostep.exe
UninstallDisplayName=Echostep
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
CloseApplications=yes
OutputDir=Output
OutputBaseFilename=Echostep-Setup-{#AppVersion}

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: associate; Description: "Open .estep macro files with Echostep"; GroupDescription: "Files:"

[Files]
Source: "..\dist\Echostep.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\examples\*"; DestDir: "{app}\examples"; Flags: ignoreversion recursesubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Echostep"; Filename: "{app}\Echostep.exe"
Name: "{autodesktop}\Echostep"; Filename: "{app}\Echostep.exe"; Tasks: desktopicon

[Registry]
; HKA is the current user's registry for a per-user install, the machine's for an all-users install.
Root: HKA; Subkey: "Software\Classes\.estep"; ValueType: string; ValueName: ""; ValueData: "Echostep.Macro"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.omr"; ValueType: string; ValueName: ""; ValueData: "Echostep.Macro"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Echostep.Macro"; ValueType: string; ValueName: ""; ValueData: "Echostep macro"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Echostep.Macro\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Echostep.exe,0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Echostep.Macro\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Echostep.exe"" ""%1"""; Tasks: associate

[Run]
Filename: "{app}\Echostep.exe"; Description: "Start Echostep now"; Flags: nowait postinstall skipifsilent
