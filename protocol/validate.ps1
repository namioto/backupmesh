$ErrorActionPreference = 'Stop'

$specPath = Join-Path $PSScriptRoot 'openapi.yaml'
$text = Get-Content -LiteralPath $specPath -Raw

$requiredFragments = @(
    'openapi: 3.1.0',
    '  /storage/status:',
    '  /backup/request:',
    '  /backup/targets/{source_agent_id}/{backup_set_id}:',
    '  /backup/progress:',
    '  /backup/result:',
    '  /backup/cancel:',
    '  /backup/status/{job_id}:',
    '  /source/catalog:',
    '  /source/catalogs:',
    '  /storage/configuration:',
    '  /storage/devices/status:',
    '  /storage/volumes:',
    '    mutualTLS:',
    '      type: mutualTLS'
)

foreach ($fragment in $requiredFragments) {
    if (-not $text.Contains($fragment)) {
        throw "Missing required OpenAPI fragment: $fragment"
    }
}

$references = [regex]::Matches($text, '\$ref: ''#/components/(?<section>[^/]+)/(?<name>[^'']+)''\s*')
foreach ($reference in $references) {
    $name = [regex]::Escape($reference.Groups['name'].Value)
    if ($text -notmatch "(?m)^    ${name}:\s*$") {
        throw "Unresolved local component reference: $($reference.Value.Trim())"
    }
}

$operationIds = [regex]::Matches($text, '(?m)^      operationId: (?<id>[A-Za-z][A-Za-z0-9]+)$') |
    ForEach-Object { $_.Groups['id'].Value }
if ($operationIds.Count -ne 13) {
    throw "Expected 13 operations, found $($operationIds.Count)."
}
if (($operationIds | Sort-Object -Unique).Count -ne $operationIds.Count) {
    throw 'Duplicate operationId found.'
}

Write-Host "BackupMesh OpenAPI structural validation passed ($($operationIds.Count) operations)."
