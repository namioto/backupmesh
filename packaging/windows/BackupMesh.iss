#ifndef AppVersion
  #define AppVersion "0.2.1"
#endif
#ifndef SourcePackage
  #define SourcePackage "..\..\artifacts\BackupMesh-Storage-win-x64"
#endif
#ifndef OutputDirectory
  #define OutputDirectory "..\..\artifacts\installer"
#endif

#define AppName "BackupMesh Storage Agent"
#define AppPublisher "BackupMesh"
#define AppUrl "https://github.com/namioto/backupmesh"
#define ServiceName "BackupMeshStorageAgent"

[Setup]
AppId={{CBAB1039-2CD5-4D4D-A5DD-7D956BA9A04F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\BackupMesh
DefaultGroupName=BackupMesh
DisableProgramGroupPage=yes
LicenseFile={#SourcePackage}\LICENSE
SetupIconFile={#SourcePackage}\App\Assets\backupmesh-tray.ico
UninstallDisplayIcon={app}\App\BackupMesh.Storage.App.exe
OutputDir={#OutputDirectory}
OutputBaseFilename=BackupMesh-Storage-{#AppVersion}-win-x64-Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
CloseApplicationsFilter=BackupMesh.Storage.App.exe
RestartApplications=no
WizardStyle=modern
MinVersion=10.0.17763
ChangesAssociations=no
ChangesEnvironment=no
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=BackupMesh Storage Agent installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
Source: "{#SourcePackage}\App\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePackage}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePackage}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePackage}\Install-BackupMesh.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\Uninstall-BackupMesh.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\VERSION"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\BackupMesh Storage Agent"; Filename: "{app}\App\BackupMesh.Storage.App.exe"
Name: "{group}\Uninstall BackupMesh"; Filename: "{uninstallexe}"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Install-BackupMesh.ps1"""; StatusMsg: "Installing and starting the BackupMesh service..."; Flags: runhidden waituntilterminated
Filename: "{app}\App\BackupMesh.Storage.App.exe"; Description: "Launch BackupMesh Storage Agent"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Uninstall-BackupMesh.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "BackupMeshServiceCleanup"

; Belt-and-suspenders for the tray app's forced-kill race (see Uninstall-BackupMesh.ps1): if Windows
; had not fully released a directory handle by the time Setup's own file removal ran, these folders
; are left behind empty. dirifempty only removes them if nothing meaningful remains.
[UninstallDelete]
Type: dirifempty; Name: "{app}\App\cs"
Type: dirifempty; Name: "{app}\App\de"
Type: dirifempty; Name: "{app}\App\es"
Type: dirifempty; Name: "{app}\App\fr"
Type: dirifempty; Name: "{app}\App\it"
Type: dirifempty; Name: "{app}\App\ja"
Type: dirifempty; Name: "{app}\App\ko"
Type: dirifempty; Name: "{app}\App\pl"
Type: dirifempty; Name: "{app}\App\pt-BR"
Type: dirifempty; Name: "{app}\App\ru"
Type: dirifempty; Name: "{app}\App\tr"
Type: dirifempty; Name: "{app}\App\zh-Hans"
Type: dirifempty; Name: "{app}\App\zh-Hant"
Type: dirifempty; Name: "{app}\App\Assets"
Type: dirifempty; Name: "{app}\App"
Type: dirifempty; Name: "{app}\Service"
Type: dirifempty; Name: "{app}\licenses"
Type: dirifempty; Name: "{app}"

[Code]
function IsServiceRunning(const Name: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query "' + Name + '"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if IsServiceRunning('{#ServiceName}') then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop "{#ServiceName}"', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;
  { CloseApplicationsFilter cannot close the tray app: its Closing handler hides it to the tray
    instead of exiting (see App.xaml.cs), so a graceful Restart Manager close request never actually
    frees its own exe/dll files. Without this, an upgrade over a running install leaves stale files. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM BackupMesh.Storage.App.exe /F', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
end;
