[CmdletBinding()]
param([switch]$RequireSecondDevice, [switch]$FolderTargets, [switch]$AutomaticOnly, [switch]$SourceArrival)

$ErrorActionPreference = 'Stop'
if ($SourceArrival -and -not $AutomaticOnly) { throw '-SourceArrival requires -AutomaticOnly so the test exercises the Source-side arrival trigger without a manual command.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$runId = [Guid]::NewGuid().ToString('N')
$workRoot = Join-Path $artifactsRoot ("local-e2e-" + $runId)
$serviceExe = Join-Path $artifactsRoot 'BackupMesh-Storage-win-x64\Service\BackupMesh.Storage.Service.exe'
$restServerExe = Join-Path $artifactsRoot 'BackupMesh-Storage-win-x64\Service\rest-server.exe'
$resticExe = Join-Path $artifactsRoot 'tools\windows-x64\restic.exe'
$sourceExe = Join-Path $workRoot 'backupmesh-agent.exe'
$service = $null
$sourceProcess = $null
$externalCleanupRoots = @()
$completed = $false

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
    $secretsRoot = Join-Path $workRoot 'pairing'
    if (-not $SourceArrival) {
        New-Item -ItemType Directory -Path $sourceData -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $sourceData 'proof.txt'), "BackupMesh E2E proof`nline two`n")
        [IO.File]::WriteAllBytes((Join-Path $sourceData 'payload.bin'), [Security.Cryptography.RandomNumberGenerator]::GetBytes(65536))
    }
    [IO.File]::WriteAllText($passwordFile, "local-e2e-password`n")

    $sourceId = [Guid]::NewGuid()
    $backupSetId = [Guid]::NewGuid()
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

    if ($SourceArrival) {
        Invoke-RestMethod -Method Put -Uri 'http://127.0.0.1:7444/api/v1/automation/settings' -ContentType 'application/json' -Body '{"enabled":false}' | Out-Null
    }

    $pairing = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:7444/api/v1/pairing/sessions'
    & $sourceExe pair -config $configPath -storage $pairing.control_endpoint -code $pairing.code -fingerprint $pairing.certificate_sha256 -output $secretsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Source pairing failed.' }

    $driveRoot = [IO.Path]::GetPathRoot($workRoot)
    if ($FolderTargets) {
        $selectedVolumes = 1..2 | ForEach-Object {
            $folderRoot = [IO.Path]::GetFullPath((Join-Path $workRoot "folder-device-$_"))
            [IO.Directory]::CreateDirectory($folderRoot) | Out-Null
            [pscustomobject]@{ root = $folderRoot; stableId = 'folder:' + $folderRoot.TrimEnd([IO.Path]::DirectorySeparatorChar).ToUpperInvariant(); volumeLabel = "Folder $_" }
        }
    }
    else {
        $volumes = Invoke-RestMethod -Uri 'http://127.0.0.1:7444/api/v1/storage/volumes'
        $primaryVolume = $volumes.Where({ $_.root -eq $driveRoot }, 'First')
        if (-not $primaryVolume) { throw "Could not find the volume containing $workRoot." }
        $selectedVolumes = @($primaryVolume)
        if ($RequireSecondDevice) {
            $secondVolume = $volumes.Where({ $_.root -ne $driveRoot -and (Test-Path -LiteralPath $_.root) }, 'First')
            if ($secondVolume) { $selectedVolumes += $secondVolume }
            else { throw 'A second connected storage volume is required for this E2E run.' }
        }
    }
    $devices = @()
    $mappings = @()
    $repositories = @()
    $expectedTargetCount = @($selectedVolumes).Count
    foreach ($selectedVolume in $selectedVolumes) {
        $deviceId = [Guid]::NewGuid()
        $mappingId = [Guid]::NewGuid()
        if ($FolderTargets) {
            $repository = Join-Path $selectedVolume.root 'repository'
        }
        elseif ($selectedVolume.root -eq $driveRoot) {
            $repository = Join-Path $workRoot 'repository'
        }
        else {
            $externalRoot = Join-Path $selectedVolume.root ("BackupMesh-E2E-" + $runId)
            $externalCleanupRoots += $externalRoot
            $repository = Join-Path $externalRoot 'repository'
            [IO.Directory]::CreateDirectory($repository) | Out-Null
        }
        $relativeRepository = [IO.Path]::GetRelativePath($selectedVolume.root, $repository)
        $devices += @{ id = $deviceId; stableId = $selectedVolume.stableId; displayName = "E2E $($selectedVolume.volumeLabel)"; volumeLabel = $selectedVolume.volumeLabel; lastKnownRoot = $selectedVolume.root; registeredAt = [DateTimeOffset]::UtcNow; lastSeenAt = [DateTimeOffset]::UtcNow; arrivalDelayMinutes = 0 }
        $mappings += @{ id = $mappingId; backupSetId = $backupSetId; deviceId = $deviceId; repositoryPath = $relativeRepository; enabled = $true }
        $repositories += $repository
    }
    if ($SourceArrival) {
        $sourceDeviceId = [Guid]::NewGuid()
        $sourceStableId = 'folder:' + [IO.Path]::GetFullPath($sourceData).TrimEnd([IO.Path]::DirectorySeparatorChar).ToUpperInvariant()
        $devices += @{ id = $sourceDeviceId; stableId = $sourceStableId; displayName = 'E2E arriving source'; volumeLabel = 'Source folder'; lastKnownRoot = $sourceData; registeredAt = [DateTimeOffset]::UtcNow; lastSeenAt = $null; arrivalDelayMinutes = 0 }
    }
    $topology = @{
        expectedRevision = 0
        configuration = @{
            devices = $devices
            backupSets = @(@{ id = $backupSetId; sourceAgentId = $sourceId; sourceAgentName = 'Local E2E Source'; name = 'e2e'; sourcePaths = @($sourceData) })
            mappings = $mappings
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

    $sourceOutput = Join-Path $workRoot 'source.stdout.log'
    $sourceError = Join-Path $workRoot 'source.stderr.log'
    $sourceArguments = "watch -config `"$configPath`" -restic `"$resticExe`" -poll-interval 500ms"
    $sourceProcess = Start-Process -FilePath $sourceExe -ArgumentList $sourceArguments -NoNewWindow -PassThru -RedirectStandardOutput $sourceOutput -RedirectStandardError $sourceError

    if ($SourceArrival) {
        Start-Sleep -Seconds 1
        Invoke-RestMethod -Method Put -Uri 'http://127.0.0.1:7444/api/v1/automation/settings' -ContentType 'application/json' -Body '{"enabled":true}' | Out-Null
        New-Item -ItemType Directory -Path $sourceData -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $sourceData 'proof.txt'), "BackupMesh E2E source-arrival proof`nline two`n")
        [IO.File]::WriteAllBytes((Join-Path $sourceData 'payload.bin'), [Security.Cryptography.RandomNumberGenerator]::GetBytes(65536))
    }

    if (-not $AutomaticOnly) {
        $headers = @{
            'X-Request-ID' = [Guid]::NewGuid().ToString()
            'Idempotency-Key' = ([Guid]::NewGuid()).ToString('N')
            'X-BackupMesh-Sent-At' = [DateTimeOffset]::UtcNow.ToString('O')
        }
        $enqueue = @{
            mapping_ids = [Guid[]]@($mappings | ForEach-Object { [Guid]$_.id })
            reason = 'e2e-watch'
        } | ConvertTo-Json -Depth 5
        $enqueueResponse = Invoke-WebRequest -Method Post -Uri 'http://127.0.0.1:7444/api/v1/backup/commands/enqueue' -Headers $headers -ContentType 'application/json' -Body $enqueue -SkipHttpErrorCheck
        if (-not $enqueueResponse.StatusCode.ToString().StartsWith('2')) {
            throw "Backup commands were not queued ($($enqueueResponse.StatusCode)): $($enqueueResponse.Content)"
        }
    }

    $allJobsSucceeded = $false
    for ($attempt = 0; $attempt -lt 240; $attempt++) {
        if ($sourceProcess.HasExited) { break }
        $jobsJson = (Invoke-WebRequest -Uri 'http://127.0.0.1:7444/api/v1/backup/jobs').Content
        $jobs = @($jobsJson | ConvertFrom-Json)
        $terminalJobs = @($jobs | Where-Object { $_.state -in @('SUCCEEDED', 'FAILED', 'CANCELLED') })
        if ($terminalJobs.Count -ge $expectedTargetCount) {
            $failedJobs = @($terminalJobs | Where-Object { $_.state -ne 'SUCCEEDED' })
            if ($failedJobs.Count -gt 0) {
                throw "One or more queued backup jobs failed: $($failedJobs | ConvertTo-Json -Depth 8)"
            }
            $allJobsSucceeded = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $allJobsSucceeded) {
        if ($sourceProcess -and -not $sourceProcess.HasExited) {
            Stop-Process -Id $sourceProcess.Id -Force -ErrorAction SilentlyContinue
            $sourceProcess.WaitForExit(5000) | Out-Null
        }
        $sourceDiagnostics = @(
            if (Test-Path -LiteralPath $sourceOutput) { [IO.File]::ReadAllText($sourceOutput) }
            if (Test-Path -LiteralPath $sourceError) { [IO.File]::ReadAllText($sourceError) }
        ) -join [Environment]::NewLine
        throw "Queued Source backup did not complete.`n$sourceDiagnostics"
    }
    Stop-Process -Id $sourceProcess.Id -Force -ErrorAction SilentlyContinue
    $sourceProcess.WaitForExit(5000) | Out-Null
    $sourceProcess.Dispose()
    $sourceProcess = $null

    $originalHashes = Get-ChildItem -LiteralPath $sourceData -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    for ($index = 0; $index -lt $repositories.Count; $index++) {
        $targetRestore = Join-Path $restoreRoot $index
        & $resticExe -r $repositories[$index] --password-file $passwordFile restore latest --target $targetRestore
        if ($LASTEXITCODE -ne 0) { throw "Restic restore failed for target $index." }
        $restoredSource = Join-Path $targetRestore ($sourceData.TrimStart([IO.Path]::DirectorySeparatorChar).Replace(':', ''))
        $restoredHashes = Get-ChildItem -LiteralPath $restoredSource -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        if (($originalHashes -join ',') -ne ($restoredHashes -join ',')) { throw "Restored file content did not match the source for target $index." }
    }
    Write-Host "BackupMesh local E2E passed: $($originalHashes.Count) files backed up to $($repositories.Count) target(s) and restored with matching SHA-256 hashes."
    $completed = $true
}
finally {
    if ($sourceProcess -and -not $sourceProcess.HasExited) {
        Stop-Process -Id $sourceProcess.Id -Force -ErrorAction SilentlyContinue
        $sourceProcess.Dispose()
    }
    if ($service -and -not $service.HasExited) {
        try { Invoke-RestMethod -Method Post 'http://127.0.0.1:7444/api/v1/service/shutdown' -TimeoutSec 2 | Out-Null }
        catch { Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue }
        if (-not $service.WaitForExit(10000)) { Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($service) { $service.Dispose() }
    if (-not $completed -and $serviceOutput -and (Test-Path -LiteralPath $serviceOutput)) {
        Get-Content -LiteralPath $serviceOutput -Tail 120 | Write-Host
    }
    $resolvedWork = [IO.Path]::GetFullPath($workRoot)
    if ($completed -and $resolvedWork.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedWork)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($externalRoot in $externalCleanupRoots) {
        $resolvedExternal = [IO.Path]::GetFullPath($externalRoot)
        $externalDriveRoot = [IO.Path]::GetPathRoot($resolvedExternal)
        if ($resolvedExternal.StartsWith((Join-Path $externalDriveRoot 'BackupMesh-E2E-'), [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedExternal)) {
            Remove-Item -LiteralPath $resolvedExternal -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
