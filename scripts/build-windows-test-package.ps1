[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot "artifacts\BackupMesh-Storage-$Runtime"
$toolRoot = Join-Path $repoRoot 'artifacts\tools\windows-x64'

if ($Runtime -ne 'win-x64') {
    throw 'Bundled third-party tools are currently pinned only for win-x64.'
}
if (-not (Test-Path (Join-Path $toolRoot 'restic.exe')) -or -not (Test-Path (Join-Path $toolRoot 'rest-server.exe'))) {
    & (Join-Path $PSScriptRoot 'fetch-third-party-tools.ps1')
}

New-Item -ItemType Directory -Path (Join-Path $outputRoot 'App') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outputRoot 'Service') -Force | Out-Null

$common = @('--configuration', 'Release', '--runtime', $Runtime, '--self-contained', 'true', '-p:DebugType=None')
& dotnet publish (Join-Path $repoRoot 'storage-agent\src\BackupMesh.Storage.App\BackupMesh.Storage.App.csproj') @common --output (Join-Path $outputRoot 'App')
if ($LASTEXITCODE -ne 0) { throw 'Storage App publish failed.' }
& dotnet publish (Join-Path $repoRoot 'storage-agent\src\BackupMesh.Storage.Service\BackupMesh.Storage.Service.csproj') @common --output (Join-Path $outputRoot 'Service')
if ($LASTEXITCODE -ne 0) { throw 'Storage Service publish failed.' }

Copy-Item (Join-Path $toolRoot 'rest-server.exe') (Join-Path $outputRoot 'Service\rest-server.exe') -Force
Copy-Item (Join-Path $toolRoot 'restic.exe') (Join-Path $outputRoot 'Service\restic.exe') -Force
Copy-Item (Join-Path $repoRoot 'packaging\windows\Start-BackupMesh.ps1') $outputRoot -Force
Copy-Item (Join-Path $repoRoot 'LICENSE') $outputRoot -Force
Copy-Item (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') $outputRoot -Force
Copy-Item (Join-Path $repoRoot 'licenses') (Join-Path $outputRoot 'licenses') -Recurse -Force

Write-Host "Windows test package: $outputRoot"
