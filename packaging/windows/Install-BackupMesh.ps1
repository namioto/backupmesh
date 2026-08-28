#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'BackupMeshStorageAgent'
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$serviceRoot = Join-Path $packageRoot 'Service'
$serviceExe = Join-Path $serviceRoot 'BackupMesh.Storage.Service.exe'
$dataRoot = Join-Path $env:ProgramData 'BackupMesh'

if (-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) {
    throw "Storage Service executable was not found: $serviceExe"
}
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

$arguments = @(
    "--contentRoot=`"$serviceRoot`""
    "--StorageConfiguration:PersistencePath=`"$(Join-Path $dataRoot 'storage-configuration.json')`""
    "--SourceCatalog:PersistencePath=`"$(Join-Path $dataRoot 'source-catalogs.json')`""
    "--BackupJob:PersistencePath=`"$(Join-Path $dataRoot 'backup-jobs.json')`""
    "--Pairing:CredentialHashPath=`"$(Join-Path $dataRoot 'pairing-credential.sha256')`""
    "--PairingCertificate:ProtectedAuthorityPath=`"$(Join-Path $dataRoot 'pairing-authority.dpapi')`""
    "--RepositoryServer:ExecutablePath=`"$(Join-Path $serviceRoot 'rest-server.exe')`""
)
$binaryPath = "`"$serviceExe`" $($arguments -join ' ')"

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not replace the existing BackupMesh service.' }
}
& sc.exe create $serviceName "binPath= $binaryPath" 'start= auto' 'DisplayName= BackupMesh Storage Agent' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not create the BackupMesh service.' }
& sc.exe description $serviceName 'Coordinates storage devices and BackupMesh repositories.' | Out-Null
& sc.exe failure $serviceName 'reset= 86400' 'actions= restart/5000/restart/15000/restart/60000' | Out-Null
Start-Service -Name $serviceName

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$trayCommand = "`"$(Join-Path $packageRoot 'App\BackupMesh.Storage.App.exe')`""
New-ItemProperty -Path $runKey -Name 'BackupMesh Storage Agent' -Value $trayCommand -PropertyType String -Force | Out-Null
Write-Host 'BackupMesh Storage Agent is installed and running. The tray app will start at sign-in.'
