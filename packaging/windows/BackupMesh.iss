#ifndef AppVersion
  #define AppVersion "0.3.14"
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
DisableWelcomePage=no
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

[CustomMessages]
english.ExistingSettings=Previous BackupMesh settings were found. This installation will reuse your backup rules, connections and history. Enabled rules may run when their storage becomes available.
korean.ExistingSettings=이전 BackupMesh 설정을 발견했습니다. 기존 백업 규칙, 연결 정보와 이력을 재사용합니다. 활성화된 규칙은 저장장치가 연결되면 실행될 수 있습니다.
english.RemoveSettings=Also remove Storage settings and connections?%n%nYes: remove backup rules, history, pairing information and this Windows user's app settings. Computers must be paired again.%nNo (default): keep settings for reinstallation.%nCancel: stop uninstalling.%n%nActual backups and encrypted repository passwords are always kept. Destination metadata is retained as recovery files. Passwords remain tied to this Windows installation; this is not a portable recovery export. Other Windows users' settings and the separate Source Agent are not removed.
korean.RemoveSettings=Storage 설정과 연결 정보도 삭제하시겠습니까?%n%n예: 백업 규칙, 이력, 페어링 정보와 현재 Windows 사용자의 앱 설정을 삭제합니다. 다른 컴퓨터는 다시 연결해야 합니다.%n아니요(기본값): 재설치를 위해 설정을 보존합니다.%n취소: 제거를 중단합니다.%n%n실제 백업과 암호화된 복원용 암호는 항상 보존합니다. 저장 경로 정보도 복구용 파일로 남깁니다. 암호는 이 Windows 설치에 종속되므로 다른 PC용 복구 사본은 아닙니다. 다른 Windows 사용자 설정과 별도 Source Agent는 삭제하지 않습니다.
english.CleanupFailed=BackupMesh cleanup failed. Settings may have been only partially removed. The uninstaller has stopped; retry after resolving the service or file access error.
korean.CleanupFailed=BackupMesh 정리에 실패했습니다. 일부 설정이 남아 있을 수 있습니다. 제거를 중단합니다. 서비스 또는 파일 접근 문제를 해결한 뒤 다시 시도하세요.

[Files]
Source: "{#SourcePackage}\App\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePackage}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePackage}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePackage}\Install-BackupMesh.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\Uninstall-BackupMesh.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\Clear-BackupMeshSettings.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\VERSION"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\BackupMesh Storage Agent"; Filename: "{app}\App\BackupMesh.Storage.App.exe"
Name: "{group}\Uninstall BackupMesh"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BackupMesh Storage Agent"; ValueData: """{app}\App\BackupMesh.Storage.App.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Install-BackupMesh.ps1"""; StatusMsg: "Installing and starting the BackupMesh service..."; Flags: runhidden waituntilterminated
Filename: "{app}\App\BackupMesh.Storage.App.exe"; Description: "Launch BackupMesh Storage Agent"; Flags: nowait postinstall skipifsilent runasoriginaluser

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
var
  RemoveSettings: Boolean;

procedure InitializeWizard();
begin
  if FileExists(ExpandConstant('{commonappdata}\BackupMesh\storage-configuration.json')) or
     FileExists(ExpandConstant('{localappdata}\BackupMesh\storage-agent.json')) then
    WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption + #13#10#13#10 + CustomMessage('ExistingSettings');
end;

function InitializeUninstall(): Boolean;
var
  Choice: Integer;
begin
  RemoveSettings := False;
  Result := True;
  if UninstallSilent then Exit;
  Choice := MsgBox(CustomMessage('RemoveSettings'), mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);
  RemoveSettings := Choice = IDYES;
  Result := Choice <> IDCANCEL;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Params: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Uninstall-BackupMesh.ps1') + '"';
  if RemoveSettings then Params := Params + ' -RemoveSettings';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException(CustomMessage('CleanupFailed'));
  if ResultCode <> 0 then RaiseException(CustomMessage('CleanupFailed'));
end;

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
