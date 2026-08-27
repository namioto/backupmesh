[CmdletBinding()]
param(
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot '..\artifacts\tools\windows-x64'
}
$manifestPath = Join-Path $PSScriptRoot '..\tools\third-party-tools.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$resolvedOutput = [IO.Path]::GetFullPath($OutputRoot)
$cache = Join-Path $resolvedOutput '.cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Install-VerifiedZip {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [object]$Release,
        [Parameter(Mandatory)] [string]$ExecutablePattern,
        [Parameter(Mandatory)] [string]$DestinationName
    )

    $archive = Join-Path $cache "$Name.zip"
    Invoke-WebRequest -Uri $Release.url -OutFile $archive
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $Release.sha256) {
        throw "$Name checksum mismatch. Expected $($Release.sha256), received $actualHash."
    }

    $extractRoot = Join-Path $cache "$Name-extracted"
    if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force }
    Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
    $executable = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Filter $ExecutablePattern)
    if ($executable.Count -ne 1) { throw "$Name archive contained $($executable.Count) matching executables." }
    Copy-Item -LiteralPath $executable[0].FullName -Destination (Join-Path $resolvedOutput $DestinationName) -Force
}

Install-VerifiedZip -Name 'restic' -Release $manifest.restic.'windows-x64' -ExecutablePattern 'restic*.exe' -DestinationName 'restic.exe'
Install-VerifiedZip -Name 'rest-server' -Release $manifest.'rest-server'.'windows-x64' -ExecutablePattern 'rest-server*.exe' -DestinationName 'rest-server.exe'

Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\THIRD_PARTY_NOTICES.md') -Destination $resolvedOutput -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\licenses\restic-BSD-2-Clause.txt') -Destination (Join-Path $resolvedOutput 'restic-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\licenses\rest-server-BSD-2-Clause.txt') -Destination (Join-Path $resolvedOutput 'rest-server-LICENSE.txt') -Force

$versions = [ordered]@{
    restic = $manifest.restic.version
    rest_server = $manifest.'rest-server'.version
    fetched_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
}
$versions | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resolvedOutput 'versions.json') -Encoding UTF8

Remove-Item -LiteralPath $cache -Recurse -Force
Write-Host "Verified third-party tools installed to $resolvedOutput"
