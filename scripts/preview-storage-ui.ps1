param([switch]$Rebuild, [ValidateSet('ko', 'en')][string]$Language = 'ko')

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\storage-agent\src\BackupMesh.Storage.App\BackupMesh.Storage.App.csproj'
$app = Join-Path $PSScriptRoot '..\storage-agent\src\BackupMesh.Storage.App\bin\DesignCheck\BackupMesh.Storage.App.exe'
$app = [IO.Path]::GetFullPath($app)
$assembly = [IO.Path]::ChangeExtension($app, '.dll')
$languageMarker = Join-Path (Split-Path -Parent $app) '.preview-language'
$sourceRoot = Join-Path $PSScriptRoot '..\storage-agent\src'
$latestSource = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in '.cs', '.xaml', '.resx', '.csproj', '.png' } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

$existing = @(Get-Process BackupMesh.Storage.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app })
$needsBuild = $Rebuild -or -not (Test-Path -LiteralPath $assembly) -or $latestSource.LastWriteTimeUtc -gt (Get-Item -LiteralPath $assembly).LastWriteTimeUtc
if (-not $needsBuild -and $existing.Count -gt 0 -and (Test-Path -LiteralPath $languageMarker) -and (Get-Content -LiteralPath $languageMarker -Raw).Trim() -eq $Language) {
    $existing | Select-Object -First 1 Id, Path
    return
}
$existing | Stop-Process

if ($needsBuild) {
    dotnet build $project --no-restore '-p:OutputPath=bin/DesignCheck/' '-p:UseSharedCompilation=false' -m:1 -nr:false -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Preview build failed.' }
}

Start-Process -FilePath $app -WorkingDirectory (Split-Path -Parent $app) -ArgumentList '--demo', "--language=$Language", '--preview-backups' -PassThru |
    Select-Object Id, Path
Set-Content -LiteralPath $languageMarker -Value $Language
