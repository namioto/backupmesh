[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$workRoot = Join-Path $artifactsRoot ("local-e2e-" + [Guid]::NewGuid().ToString('N'))
$serviceExe = Join-Path $artifactsRoot 'BackupMesh-Storage-win-x64\Service\BackupMesh.Storage.Service.exe'
$restServerExe = Join-Path $artifactsRoot 'BackupMesh-Storage-win-x64\Service\rest-server.exe'
$resticExe = Join-Path $artifactsRoot 'tools\windows-x64\restic.exe'
$sourceExe = Join-Path $workRoot 'backupmesh-agent.exe'
$service = $null

foreach ($required in @($serviceExe, $restServerExe, $resticExe)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required E2E binary was not found: $required" }
}
if (Get-NetTCPConnection -LocalPort 7444 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 7444 is already in use. Stop the running test Storage Agent first.' }

New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
try {
    $sourceData = Join-Path $workRoot 'source-data'
    $restoreRoot = Join-Path $workRoot 'restore'
    $passwordFile = Join-Path $workRoot 'repository.password'
    $configPath = Join-Path $workRoot 'backupmesh.json'
    $bundlePath = Join-Path $workRoot 'backupmesh-pairing.json'
    $secretsRoot = Join-Path $workRoot 'pairing'
    New-Item -ItemType Directory -Path $sourceData -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $sourceData 'proof.txt'), "BackupMesh E2E proof`nline two`n")
    [IO.File]::WriteAllBytes((Join-Path $sourceData 'payload.bin'), [Security.Cryptography.RandomNumberGenerator]::GetBytes(65536))
    [IO.File]::WriteAllText($passwordFile, "local-e2e-password`n")

    $sourceId = [Guid]::NewGuid()
    $backupSetId = [Guid]::NewGuid()
    $mappingId = [Guid]::NewGuid()
    $deviceId = [Guid]::NewGuid()
    $configuration = @{
        agent = @{ id = $sourceId; name = 'Local E2E Source' }
        storage = @{ controlEndpoint = 'https://localhost:7443'; repositoryPasswordFile = $passwordFile; resticCacheDirectory = (Join-Path $workRoot 'cache') }
        backupSets = @(@{ id = $backupSetId; name = 'e2e'; paths = @($sourceData) })
    }
    [IO.File]::WriteAllText($configPath, ($configuration | ConvertTo-Json -Depth 10))

    Push-Location (Join-Path $repoRoot 'source-agent')
    try { & go build -trimpath -o $sourceExe ./cmd/backupmesh-agent }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the Windows Source Agent.' }

    $env:Pairing__CredentialHashPath = Join-Path $workRoot 'credentials.sha256'
    $env:PairingCertificate__ProtectedAuthorityPath = Join-Path $workRoot 'authority.dpapi'
    $env:StorageConfiguration__PersistencePath = Join-Path $workRoot 'storage.json'
    $env:SourceCatalog__PersistencePath = Join-Path $workRoot 'catalog.json'
    $env:BackupJob__PersistencePath = Join-Path $workRoot 'jobs.json'
    $env:RepositoryServer__ExecutablePath = $restServerExe
    $env:RepositoryServer__CredentialDirectory = Join-Path $workRoot 'repository-credentials'
    $serviceOutput = Join-Path $workRoot 'service.stdout.log'
    $serviceError = Join-Path $workRoot 'service.stderr.log'
    $service = Start-Process -FilePath $serviceExe -WorkingDirectory (Split-Path $serviceExe) -WindowStyle Hidden -RedirectStandardOutput $serviceOutput -RedirectStandardError $serviceError -PassThru

    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { Invoke-RestMethod -Uri 'http://127.0.0.1:7444/api/v1/storage/status' | Out-Null; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'Storage Agent did not become ready.' }

    $pairing = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:7444/api/v1/pairing/credential'
    [IO.File]::WriteAllText($bundlePath, ($pairing | ConvertTo-Json -Depth 10))
    & $sourceExe apply-pairing -config $configPath -bundle $bundlePath -output $secretsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Source pairing failed.' }

    $volumes = Invoke-RestMethod -Uri 'http://127.0.0.1:7444/api/v1/storage/volumes'
    $driveRoot = [IO.Path]::GetPathRoot($workRoot)
    $volume = $volumes.Where({ $_.root -eq $driveRoot }, 'First')
    if (-not $volume) { throw "Could not find the volume containing $workRoot." }
    $relativeRepository = [IO.Path]::GetRelativePath($driveRoot, (Join-Path $workRoot 'repository'))
    $topology = @{
        expectedRevision = 0
        configuration = @{
            devices = @(@{ id = $deviceId; stableId = $volume.stableId; displayName = 'Local E2E Device'; volumeLabel = $volume.volumeLabel; lastKnownRoot = $volume.root; registeredAt = [DateTimeOffset]::UtcNow; lastSeenAt = [DateTimeOffset]::UtcNow; arrivalDelayMinutes = 0 })
            backupSets = @(@{ id = $backupSetId; sourceAgentId = $pairing.agent_id; sourceAgentName = 'Local E2E Source'; name = 'e2e'; sourcePaths = @($sourceData) })
            mappings = @(@{ id = $mappingId; backupSetId = $backupSetId; deviceId = $deviceId; repositoryPath = $relativeRepository; enabled = $true })
        }
    }
    $topologyJson = $topology | ConvertTo-Json -Depth 10
    $topologyResponse = Invoke-WebRequest -Method Put -Uri 'http://127.0.0.1:7444/api/v1/storage/configuration' -ContentType 'application/json' -Body $topologyJson -SkipHttpErrorCheck
    if (-not $topologyResponse.StatusCode.ToString().StartsWith('2')) {
        throw "Storage topology was rejected ($($topologyResponse.StatusCode)): $($topologyResponse.Content)`nRequest: $topologyJson"
    }

    $storageReady = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        $status = Invoke-RestMethod -Uri 'http://127.0.0.1:7444/api/v1/storage/status'
        if ($status.state -eq 'ready') { $storageReady = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $storageReady) { throw 'Registered local storage did not become ready.' }

    & $sourceExe backup -config $configPath -set e2e -restic $resticExe
    if ($LASTEXITCODE -ne 0) { throw 'Source backup failed.' }
    $repository = Join-Path $driveRoot $relativeRepository
    & $resticExe -r $repository --password-file $passwordFile restore latest --target $restoreRoot
    if ($LASTEXITCODE -ne 0) { throw 'Restic restore failed.' }
    $restoredSource = Join-Path $restoreRoot ($sourceData.TrimStart([IO.Path]::DirectorySeparatorChar).Replace(':', ''))
    $originalHashes = Get-ChildItem -LiteralPath $sourceData -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $restoredHashes = Get-ChildItem -LiteralPath $restoredSource -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    if (($originalHashes -join ',') -ne ($restoredHashes -join ',')) { throw 'Restored file content did not match the source.' }
    Write-Host "BackupMesh local E2E passed: $($originalHashes.Count) files backed up and restored with matching SHA-256 hashes."
}
finally {
    if ($service -and -not $service.HasExited) {
        try { Invoke-RestMethod -Method Post 'http://127.0.0.1:7444/api/v1/service/shutdown' -TimeoutSec 2 | Out-Null }
        catch { Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue }
        if (-not $service.WaitForExit(10000)) { Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($service) { $service.Dispose() }
    $resolvedWork = [IO.Path]::GetFullPath($workRoot)
    if ($resolvedWork.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedWork)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
    }
}
