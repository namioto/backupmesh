#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'BackupMeshStorageAgent'
$firewallRuleName = 'BackupMesh Storage Agent (mTLS)'
$repositoryFirewallRuleName = 'BackupMesh Storage Agent (repositories)'
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
