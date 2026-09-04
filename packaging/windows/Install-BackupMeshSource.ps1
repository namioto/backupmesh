[CmdletBinding()]
param()

# Unlike Install-BackupMesh.ps1 (the Storage side), this installs entirely per-user under
# %LOCALAPPDATA% and registers a per-user Scheduled Task rather than a machine-wide Windows service -
# no administrator rights are required, since backing up "this PC's own files" should not need them.
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceExe = Join-Path $packageRoot 'backupmesh-agent.exe'
$taskName = 'BackupMesh Source Agent'
$dataRoot = Join-Path $env:LOCALAPPDATA 'BackupMesh\Source'
$configPath = Join-Path $dataRoot 'backupmesh.yaml'

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "Source Agent executable was not found: $sourceExe"
}
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination (Join-Path $dataRoot 'backupmesh-agent.exe') -Force
$resticSource = Join-Path $packageRoot 'restic.exe'
if (Test-Path -LiteralPath $resticSource -PathType Leaf) {
    Copy-Item -LiteralPath $resticSource -Destination (Join-Path $dataRoot 'restic.exe') -Force
}
$agentExe = Join-Path $dataRoot 'backupmesh-agent.exe'

if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    Write-Host 'No existing configuration found. Answer a few questions to create a minimal one (Ctrl+C to skip and write one by hand instead).'
    $defaultName = $env:COMPUTERNAME
    $agentName = Read-Host "Name for this Source Agent [$defaultName]"
    if ([string]::IsNullOrWhiteSpace($agentName)) { $agentName = $defaultName }
    $setName = Read-Host 'Name for the first Backup Set [documents]'
    if ([string]::IsNullOrWhiteSpace($setName)) { $setName = 'documents' }
    $setPath = ''
    while ([string]::IsNullOrWhiteSpace($setPath) -or -not (Test-Path -LiteralPath $setPath)) {
        $setPath = Read-Host 'Absolute path to back up (e.g. C:\Users\you\Documents)'
    }
    # storage.repositoryPasswordFile is deliberately left unset: `pair` (applyPairingBundle) generates
    # and DPAPI-protects one automatically the first time this config is paired, the same as it does
    # for a Storage-adjacent config with no password file configured.
    $yamlPath = $setPath.Replace('\', '/')
    $yaml = @"
agent:
  name: $agentName
storage: {}
backupSets:
  - name: $setName
    paths:
      - $yamlPath
"@
    Set-Content -LiteralPath $configPath -Value $yaml -Encoding utf8 -NoNewline
    Write-Host "Wrote $configPath. Add more backupSets entries by hand any time; no ID or Storage connection field is required until you pair."
}

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
$action = New-ScheduledTaskAction -Execute $agentExe -Argument "watch -config `"$configPath`"" -WorkingDirectory $dataRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn
# ExecutionTimeLimit defaults to 72 hours, after which Task Scheduler kills a still-running task; watch
# is meant to run indefinitely, so this must be disabled.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Description 'Watches for BackupMesh Storage commands and runs backups for this PC.' | Out-Null
Start-ScheduledTask -TaskName $taskName

Write-Host "BackupMesh Source Agent installed and watching at sign-in (task: $taskName)."
Write-Host "Pair it with a Storage Agent (in the tray, choose Pair Source Agent) using:"
Write-Host "  `"$agentExe`" pair -config `"$configPath`" -storage https://STORAGE-PC:7443 -code CODE-FROM-TRAY -fingerprint FINGERPRINT-FROM-TRAY"
