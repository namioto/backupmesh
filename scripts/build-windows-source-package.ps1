[CmdletBinding()]
param([ValidateSet('win-x64')][string]$Runtime = 'win-x64')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot "artifacts\BackupMesh-Source-$Runtime"
$toolRoot = Join-Path $repoRoot 'artifacts\tools\windows-x64'

$resolvedOutput = [IO.Path]::GetFullPath($outputRoot)
$resolvedArtifacts = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutput.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean package output outside the artifacts directory: $resolvedOutput"
}
if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

if (-not (Test-Path (Join-Path $toolRoot 'restic.exe'))) {
    & (Join-Path $PSScriptRoot 'fetch-third-party-tools.ps1')
}

$previousGoos, $previousGoarch = $env:GOOS, $env:GOARCH
$env:GOOS, $env:GOARCH = 'windows', 'amd64'
Push-Location (Join-Path $repoRoot 'source-agent')
try {
    & go build -trimpath -ldflags '-s -w' -o (Join-Path $resolvedOutput 'backupmesh-agent.exe') ./cmd/backupmesh-agent
    if ($LASTEXITCODE -ne 0) { throw 'Source Agent build failed.' }
}
finally {
    Pop-Location
    $env:GOOS, $env:GOARCH = $previousGoos, $previousGoarch
}

Copy-Item -LiteralPath (Join-Path $toolRoot 'restic.exe') -Destination (Join-Path $resolvedOutput 'restic.exe') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'source-agent\example.config.yaml') -Destination (Join-Path $resolvedOutput 'backupmesh.yaml.example') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\windows\Install-BackupMeshSource.ps1') -Destination $resolvedOutput -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\windows\Uninstall-BackupMeshSource.ps1') -Destination $resolvedOutput -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $resolvedOutput -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $resolvedOutput -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'VERSION') -Destination $resolvedOutput -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses\restic-BSD-2-Clause.txt') -Destination $resolvedOutput -Force

$requiredPackageFiles = @(
    'backupmesh-agent.exe'
    'restic.exe'
    'backupmesh.yaml.example'
    'Install-BackupMeshSource.ps1'
    'Uninstall-BackupMeshSource.ps1'
    'LICENSE'
    'THIRD_PARTY_NOTICES.md'
    'VERSION'
    'restic-BSD-2-Clause.txt'
)
foreach ($relativePath in $requiredPackageFiles) {
    $packageFile = Join-Path $resolvedOutput $relativePath
    if (-not (Test-Path -LiteralPath $packageFile -PathType Leaf)) {
        throw "Windows Source Agent package validation failed; missing $relativePath"
    }
}

Write-Host "Windows Source Agent package: $resolvedOutput"
Write-Host "This installs per-user under %LOCALAPPDATA% via a Scheduled Task and needs no administrator rights: run Install-BackupMeshSource.ps1 from the extracted package."
