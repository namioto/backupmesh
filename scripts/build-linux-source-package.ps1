[CmdletBinding()]
param([ValidateSet('linux-x64')][string]$Runtime = 'linux-x64')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must contain a semantic version such as 0.1.0; found '$version'." }
$packageName = "BackupMesh-Source-$version-$Runtime"
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "BackupMesh-Source-$Runtime"))
$packageArchive = Join-Path $artifactsRoot "$packageName.tar.gz"
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'tools\third-party-tools.json') -Raw | ConvertFrom-Json
$archive = Join-Path $env:TEMP "backupmesh-restic-$($manifest.restic.version)-linux-x64.bz2"

if (-not $outputRoot.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Linux package output escaped artifacts directory.' }
if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }
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
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\linux\backupmesh-setup') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\linux\backupmesh-source-watch.service') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\linux\backupmesh-source@.service') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\linux\backupmesh-source@.timer') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'VERSION') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses\restic-BSD-2-Clause.txt') -Destination $outputRoot -Force
$requiredPackageFiles = @(
    'backupmesh-agent'
    'restic'
    'backupmesh.json.example'
    'install.sh'
    'backupmesh-setup'
    'backupmesh-source-watch.service'
    'backupmesh-source@.service'
    'backupmesh-source@.timer'
    'LICENSE'
    'THIRD_PARTY_NOTICES.md'
    'VERSION'
    'restic-BSD-2-Clause.txt'
)
foreach ($relativePath in $requiredPackageFiles) {
    $packageFile = Join-Path $outputRoot $relativePath
    if (-not (Test-Path -LiteralPath $packageFile -PathType Leaf)) {
        throw "Linux package validation failed; missing $relativePath"
    }
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
foreach ($relativePath in $requiredPackageFiles | Where-Object { $_ -match '\.(sh|service|timer)$' -or $_ -eq 'backupmesh-setup' }) {
    $packageFile = Join-Path $outputRoot $relativePath
    $content = [System.IO.File]::ReadAllText($packageFile).Replace("`r`n", "`n").Replace("`r", "`n")
    [System.IO.File]::WriteAllText($packageFile, $content, $utf8NoBom)
}

if (Test-Path -LiteralPath $packageArchive) { Remove-Item -LiteralPath $packageArchive -Force }
& tar -czf $packageArchive -C (Split-Path $outputRoot) (Split-Path $outputRoot -Leaf)
if ($LASTEXITCODE -ne 0) { throw 'Could not create Linux Source Agent archive.' }
Write-Host "Linux Source Agent package: $packageArchive"
