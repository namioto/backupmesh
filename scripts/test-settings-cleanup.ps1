$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\packaging\windows\Clear-BackupMeshSettings.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('BackupMesh-cleanup-test-' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $testRoot 'machine'
$user = Join-Path $testRoot 'user'
New-Item -ItemType Directory -Path $data, $user | Out-Null
foreach ($root in @($data, $user)) {
    [IO.File]::WriteAllText((Join-Path $root 'storage-configuration.json'), '{"mapping":"recovery-proof"}')
    [IO.File]::WriteAllText((Join-Path $root 'storage-agent.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $root 'pairing-authority.dpapi'), 'old pairing')
    foreach ($subdir in @('local-repository-passwords', 'repository', 'Source')) {
        $dir = New-Item -ItemType Directory -Path (Join-Path $root $subdir)
        [IO.File]::WriteAllText((Join-Path $dir.FullName 'proof'), 'must survive')
    }
}
Clear-BackupMeshSettings -DataRoot $data -UserRoot $user
Clear-BackupMeshSettings -DataRoot $data -UserRoot $user
foreach ($root in @($data, $user)) {
    foreach ($name in @('storage-configuration.json', 'storage-agent.json', 'pairing-authority.dpapi')) {
        if (Test-Path -LiteralPath (Join-Path $root $name)) { throw "Setting survived: $name" }
    }
    foreach ($subdir in @('local-repository-passwords', 'repository', 'Source')) {
        if ([IO.File]::ReadAllText((Join-Path $root "$subdir\proof")) -ne 'must survive') { throw "Protected data changed: $subdir" }
    }
    $recovery = @(Get-ChildItem -LiteralPath $root -Filter 'storage-configuration.json.recovery-*')
    if ($recovery.Count -ne 1 -or [IO.File]::ReadAllText($recovery[0].FullName) -ne '{"mapping":"recovery-proof"}') {
        throw 'Recovery metadata was not preserved.'
    }
}
Write-Host "Settings cleanup passed: settings removed; backups, passwords, Source data and recovery metadata preserved. Fixtures: $testRoot"
