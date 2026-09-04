#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'BackupMeshStorageAgent'
$firewallRuleName = 'BackupMesh Storage Agent (mTLS)'
$repositoryFirewallRuleName = 'BackupMesh Storage Agent (repositories)'

# The tray app hides to the tray instead of exiting on a close request (see App.xaml.cs), so Inno
# Setup's CloseApplications cannot make it release its own exe/dll files during uninstall. Without
# this, Setup reports "some elements could not be removed" and leaves files behind. Waiting for the
# process to actually exit (not just a fixed sleep) avoids a race where Windows has not yet released
# its open directory handles when Setup tries to remove the now-empty App folder right afterward.
$trayProcesses = Get-Process -Name 'BackupMesh.Storage.App' -ErrorAction SilentlyContinue
if ($trayProcesses) {
    $trayProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    $trayProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not remove the BackupMesh service.' }
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'BackupMesh Storage Agent' -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName $repositoryFirewallRuleName -ErrorAction SilentlyContinue
Write-Host 'BackupMesh was uninstalled. Configuration and repositories were preserved.'
