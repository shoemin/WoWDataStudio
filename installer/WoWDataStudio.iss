; WoW Data Studio installer (Inno Setup 6). Built by ..\build_installer.ps1, which stages the files and passes:
;   /DAppVersion=0.3.0  /DStageDir=<staged files>  /DOutDir=<output folder>
; Per-user install (no administrator rights): %LOCALAPPDATA%\Programs\WoW Data Studio by default.
; Installs only into its own folder, the Start menu and (optionally) the desktop. It never touches a World of Warcraft folder.
; Uninstalling removes the program files; the user's data (%APPDATA%\WoWDataStudio, Documents\WoW Data Studio) is kept.

#ifndef AppVersion
  #error AppVersion is not defined: build with build_installer.ps1
#endif
#ifndef StageDir
  #error StageDir is not defined: build with build_installer.ps1
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

#define AppExe "WoWDataStudio.exe"
; 0.8.0: the command line (always installed with the app, the same version; build_installer.ps1 refuses a stage without it)
#define CliExe "wds.exe"

; /DTestBuild=1 (build_installer.ps1 -TestBuild): a separate TEST product for automated install / uninstall checks. It has
; its own AppId, name, folder, Start-menu group and Apps entry, so installing or uninstalling it can never touch the
; user's real "WoW Data Studio" installation (CONTRIBUTING.md, "Testing the installer").
#ifdef TestBuild
  #define AppName "WoW Data Studio TEST"
  #define AppIdValue "{{5D7C2F3E-9A41-4B8E-A6C0-3F1E2B7D9A55}"
  #define OutputName "WoWDataStudio-TestSetup-" + AppVersion
#else
  #define AppName "WoW Data Studio"
  ; never change the AppId: it is how an upgrade finds the installed version
  #define AppIdValue "{{8C1B6E9A-4F1D-4C55-9B7E-2D3A6E5F9C41}"
  #define OutputName "WoWDataStudio-Setup-" + AppVersion
#endif

[Setup]
AppId={#AppIdValue}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=WoWDataTool project
AppComments=Find, view, export and re-import World of Warcraft 1.12 game data.
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} {#AppVersion} setup
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
LicenseFile={#StageDir}\LICENSE
OutputDir={#OutDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
; the optional "Add the command line tool to PATH" task changes the user's PATH: tell Windows
ChangesEnvironment=yes
RestartApplications=no
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\bin\{#AppExe}
#ifdef TestBuild
; the test install passes /NOICONS: no Start-menu group at all
AllowNoIcons=yes
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "addtopath"; Description: "Add the command line tool to PATH (wds, for your Windows user; removed again on uninstall)"; GroupDescription: "Command line:"; Flags: unchecked

[InstallDelete]
; an upgrade replaces the program and the manual completely (no stale libraries or pages from an older version)
Type: filesandordirs; Name: "{app}\bin"
Type: filesandordirs; Name: "{app}\manual"
Type: filesandordirs; Name: "{app}\blender"
Type: filesandordirs; Name: "{app}\scripts"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\bin\{#AppExe}"; WorkingDir: "{app}\bin"; Comment: "Find, view, export and re-import World of Warcraft 1.12 game data"
Name: "{group}\{#AppName} user manual"; Filename: "{app}\manual\index.html"
Name: "{group}\{#AppName} command prompt"; Filename: "{cmd}"; Parameters: "/k ""{app}\bin\wds_prompt.cmd"""; WorkingDir: "{userdocs}"; Comment: "A command prompt for the command line of WoW Data Studio (wds)"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\bin\{#AppExe}"; WorkingDir: "{app}\bin"; Tasks: desktopicon

#ifndef TestBuild
[Run]
Filename: "{app}\bin\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
#endif

[Code]
// 0.8.0: "Add the command line tool to PATH": {app}\bin in the user's PATH (HKCU\Environment), removed on uninstall.
// The value is restored exactly (the rest of the PATH is not touched).
const
  EnvKey = 'Environment';

function BinDir(): string;
begin
  Result := ExpandConstant('{app}\bin');
end;

function InPath(const Paths, Dir: string): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath(const Dir: string);
var
  Paths: string;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Paths) then Paths := '';
  if InPath(Paths, Dir) then exit;
  if Paths = '' then Paths := Dir
  else if Copy(Paths, Length(Paths), 1) = ';' then Paths := Paths + Dir + ';'
  else Paths := Paths + ';' + Dir;
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Paths);
end;

procedure RemoveFromPath(const Dir: string);
var
  Paths, P: string;
  I: Integer;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Paths) then exit;
  P := ';' + Paths + ';';
  I := Pos(';' + Uppercase(Dir) + ';', Uppercase(P));
  if I = 0 then exit;
  Delete(P, I, Length(Dir) + 1);
  P := Copy(P, 2, Length(P) - 2);
  if P = '' then RegDeleteValue(HKCU, EnvKey, 'Path')
  else RegWriteExpandStringValue(HKCU, EnvKey, 'Path', P);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then AddToPath(BinDir());
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then RemoveFromPath(BinDir());
end;
