[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must contain a semantic version such as 0.1.0; found '$version'." }

& (Join-Path $PSScriptRoot 'build-windows-test-package.ps1') -Runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw 'Windows package build failed.' }

$candidates = @(
    $InnoCompiler,
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $compiler) {
    throw 'Inno Setup 6 compiler was not found. Install it with: winget install --id JRSoftware.InnoSetup -e'
}

$packageRoot = Join-Path $repoRoot "artifacts\BackupMesh-Storage-$Runtime"
$outputRoot = Join-Path $repoRoot 'artifacts\installer'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$definition = Join-Path $repoRoot 'packaging\windows\BackupMesh.iss'
& $compiler '/Qp' "/DAppVersion=$version" "/DSourcePackage=$packageRoot" "/DOutputDirectory=$outputRoot" $definition
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$installer = Join-Path $outputRoot "BackupMesh-Storage-$version-win-x64-Setup.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer was not created: $installer" }
$versionInfo = (Get-Item -LiteralPath $installer).VersionInfo
if ($versionInfo.ProductVersion -notlike "$version*") { throw "Installer version '$($versionInfo.ProductVersion)' does not match $version." }
Write-Host "Windows installer: $installer"
