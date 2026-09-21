function Clear-BackupMeshSettings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DataRoot,
        [Parameter(Mandatory)][string]$UserRoot
    )

    # Delete only known Storage settings, never repositories, Source credentials or passwords.
    $names = @(
        'storage-configuration.json', 'source-catalogs.json', 'backup-jobs.json',
        'backup-commands.json', 'automation-settings.json', 'source-display-names.json',
        'pairing-credential.sha256', 'pairing-authority.dpapi',
        'server-certificate.dpapi', 'pairing-authority.dpapi.server.dpapi',
        'revoked-agents.txt', 'issued-certificates.txt'
    )
    foreach ($root in @($DataRoot, $UserRoot) | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $directory = Get-Item -LiteralPath $root -Force -ErrorAction Stop
        if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to clear settings through a redirected directory: $root"
        }
        # Password filenames contain mapping IDs. Keep the matching destination metadata for recovery.
        foreach ($name in @('storage-configuration.json', 'storage-agent.json')) {
            $path = Join-Path $root $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Copy-Item -LiteralPath $path -Destination ($path + '.recovery-' + [Guid]::NewGuid().ToString('N')) -ErrorAction Stop
            }
        }
        foreach ($name in @($names) + @('storage-agent.json')) {
            $path = Join-Path $root $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Remove-Item -LiteralPath $path -Force -ErrorAction Stop
            }
        }
    }
}
