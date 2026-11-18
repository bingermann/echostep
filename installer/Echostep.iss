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
; One folder holds the app and everything it saves (Macros, settings.json, rules.json); the user picks it.
; Per-user install with no admin prompt, so the folder is always writable.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Echostep
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
OutputBaseFilename=Echostep-Setup

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: associate; Description: "Open .estep macro files with Echostep"; GroupDescription: "Files:"

[Files]
Source: "..\dist\Echostep.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\examples\*"; DestDir: "{app}\Macros\Examples"; Flags: ignoreversion recursesubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{app}\Macros"

[Icons]
Name: "{autoprograms}\Echostep"; Filename: "{app}\Echostep.exe"
Name: "{autoprograms}\Echostep macros"; Filename: "{app}\Macros"
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

[Messages]
SelectDirLabel3=Echostep and everything it saves (your macros, settings.json and rules.json) will be kept together in this folder.
SelectDirBrowseLabel=Click Next to use this folder, or Browse to choose another (for example a folder in Documents). It must be a folder you can write to.

[UninstallDelete]
Type: files; Name: "{app}\settings.json"
Type: files; Name: "{app}\*.log"

[Code]
{ The folder must be writable without admin rights, because Echostep saves its settings and macros there. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  TestFile: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    TestFile := AddBackslash(WizardDirValue) + 'echostep-write-test.tmp';
    if not ForceDirectories(WizardDirValue) or not SaveStringToFile(TestFile, 'test', False) then
    begin
      MsgBox('Echostep can''t save files in that folder (folders like Program Files need admin rights).' + #13#10 +
             'Please choose a folder in your user folder, such as Documents.', mbError, MB_OK);
      Result := False;
    end
    else
      DeleteFile(TestFile);
  end;
end;

{ On uninstall, ask whether to keep the user's macros and rules. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if DirExists(ExpandConstant('{app}')) and
       (MsgBox('Also delete your saved macros and pixel rules in' + #13#10 + ExpandConstant('{app}') + '?' + #13#10#13#10 +
               'Choose No to keep them.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{app}'), True, True, True);
end;
