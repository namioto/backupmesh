[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskName = 'BackupMesh Source Agent'
$dataRoot = Join-Path $env:LOCALAPPDATA 'BackupMesh\Source'

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

# Configuration, the paired identity, and the repository password stay - uninstalling only removes the
# scheduled task and binaries, the same way the Storage side's uninstaller preserves configuration and
# repositories. Delete %LOCALAPPDATA%\BackupMesh\Source by hand if you also want those gone.
Remove-Item -LiteralPath (Join-Path $dataRoot 'backupmesh-agent.exe') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $dataRoot 'restic.exe') -Force -ErrorAction SilentlyContinue

Write-Host "BackupMesh Source Agent scheduled task removed. Configuration and pairing files were kept under $dataRoot."
