[CmdletBinding()]
param([ValidateSet('linux-x64')][string]$Runtime = 'linux-x64')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot "artifacts\BackupMesh-Source-$Runtime"
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'tools\third-party-tools.json') -Raw | ConvertFrom-Json
$archive = Join-Path $env:TEMP "backupmesh-restic-$($manifest.restic.version)-linux-x64.bz2"

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$previousGoos, $previousGoarch, $previousCgo = $env:GOOS, $env:GOARCH, $env:CGO_ENABLED
$env:GOOS, $env:GOARCH, $env:CGO_ENABLED = 'linux', 'amd64', '0'
Push-Location (Join-Path $repoRoot 'source-agent')
try {
    & go build -trimpath -ldflags '-s -w' -o (Join-Path $outputRoot 'backupmesh-agent') ./cmd/backupmesh-agent
    if ($LASTEXITCODE -ne 0) { throw 'Source Agent build failed.' }
}
finally {
    Pop-Location
    $env:GOOS, $env:GOARCH, $env:CGO_ENABLED = $previousGoos, $previousGoarch, $previousCgo
}

Invoke-WebRequest -Uri $manifest.restic.'linux-x64'.url -OutFile $archive
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $manifest.restic.'linux-x64'.sha256) { throw 'restic checksum mismatch.' }
& go run (Join-Path $repoRoot 'tools\extract-bzip2.go') $archive (Join-Path $outputRoot 'restic')
if ($LASTEXITCODE -ne 0) { throw 'Could not extract restic.' }
Remove-Item -LiteralPath $archive -Force

Copy-Item -LiteralPath (Join-Path $repoRoot 'source-agent\example.config.json') -Destination (Join-Path $outputRoot 'backupmesh.json.example') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\linux\install.sh') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\linux\backupmesh-source-watch.service') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\linux\backupmesh-source@.service') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\linux\backupmesh-source@.timer') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses\restic-BSD-2-Clause.txt') -Destination $outputRoot -Force
Write-Host "Linux Source Agent package: $outputRoot"
