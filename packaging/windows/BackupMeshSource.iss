#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef SourcePackage
  #define SourcePackage "..\..\artifacts\BackupMesh-Source-win-x64"
#endif
#ifndef OutputDirectory
  #define OutputDirectory "..\..\artifacts\installer"
#endif

#define AppName "BackupMesh Source Agent"
#define AppPublisher "BackupMesh"
#define AppUrl "https://github.com/namioto/backupmesh"

[Setup]
AppId={{2C6E6F03-3C7F-4B84-9A0A-8E7B6A9E9C2B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
; Installs entirely per-user, unlike BackupMesh.iss (the Storage side, which needs a machine-wide
; Windows service): backing up this PC's own files should not require administrator rights.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\BackupMesh Source Agent
DefaultGroupName=BackupMesh
DisableProgramGroupPage=yes
LicenseFile={#SourcePackage}\LICENSE
OutputDir={#OutputDirectory}
OutputBaseFilename=BackupMesh-Source-{#AppVersion}-win-x64-Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
MinVersion=10.0.17763
ChangesAssociations=no
ChangesEnvironment=no
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=BackupMesh Source Agent installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
Source: "{#SourcePackage}\backupmesh-agent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\restic.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\backupmesh.yaml.example"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\Install-BackupMeshSource.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\Uninstall-BackupMeshSource.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\VERSION"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePackage}\restic-BSD-2-Clause.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Uninstall BackupMesh Source Agent"; Filename: "{uninstallexe}"

[Run]
; Not run hidden, and no ImplicitSkipIfSilent: Install-BackupMeshSource.ps1 interactively asks for an
; Agent name and first Backup Set path, the same way packaging/linux/install.sh now does.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Install-BackupMeshSource.ps1"""; StatusMsg: "Setting up the Source Agent..."; Flags: waituntilterminated

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Uninstall-BackupMeshSource.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "BackupMeshSourceCleanup"
