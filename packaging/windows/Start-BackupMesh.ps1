$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dataRoot = Join-Path $env:LOCALAPPDATA 'BackupMesh'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

$env:StorageConfiguration__PersistencePath = Join-Path $dataRoot 'storage-configuration.json'
$env:SourceCatalog__PersistencePath = Join-Path $dataRoot 'source-catalogs.json'
$env:RepositoryServer__ExecutablePath = Join-Path $packageRoot 'Service\rest-server.exe'

$serviceRoot = Join-Path $packageRoot 'Service'
$service = Start-Process -FilePath (Join-Path $serviceRoot 'BackupMesh.Storage.Service.exe') -WorkingDirectory $serviceRoot -WindowStyle Hidden -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($service.HasExited) { throw 'BackupMesh Storage Service exited during startup.' }
        try {
            Invoke-RestMethod 'http://127.0.0.1:7444/api/v1/storage/status' -TimeoutSec 1 | Out-Null
            $ready = $true
            break
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'BackupMesh Storage Service did not become ready within five seconds.' }

    $appRoot = Join-Path $packageRoot 'App'
    $app = Start-Process -FilePath (Join-Path $appRoot 'BackupMesh.Storage.App.exe') -WorkingDirectory $appRoot -PassThru
    $app.WaitForExit()
}
finally {
    if (-not $service.HasExited) { Stop-Process -Id $service.Id }
    $service.WaitForExit()
}
