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
$configPath = Join-Path $dataRoot 'backupmesh.json'

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "Source Agent executable was not found: $sourceExe"
}
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') {
        throw "Timed out stopping scheduled task: $taskName"
    }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination (Join-Path $dataRoot 'backupmesh-agent.exe') -Force
$resticSource = Join-Path $packageRoot 'restic.exe'
if (Test-Path -LiteralPath $resticSource -PathType Leaf) {
    Copy-Item -LiteralPath $resticSource -Destination (Join-Path $dataRoot 'restic.exe') -Force
}
$agentExe = Join-Path $dataRoot 'backupmesh-agent.exe'

if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    @{ agent = @{ name = $env:COMPUTERNAME }; storage = @{}; backupSets = @() } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $configPath -Encoding utf8
}

$action = New-ScheduledTaskAction -Execute $agentExe -Argument "watch -config `"$configPath`"" -WorkingDirectory $dataRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
# ExecutionTimeLimit defaults to 72 hours, after which Task Scheduler kills a still-running task; watch
# is meant to run indefinitely, so this must be disabled.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Watches for BackupMesh Storage commands and runs backups for this PC.' | Out-Null
Start-ScheduledTask -TaskName $taskName

Write-Host "BackupMesh Remote Agent installed and discoverable while you are signed in (task: $taskName)."
Write-Host "Approve a request locally when Storage asks to connect. Manual invitations remain available:"
Write-Host "  `"$agentExe`" pair -config `"$configPath`""
