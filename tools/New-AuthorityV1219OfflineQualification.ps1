[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$EvidenceRoot, [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-AuthorityV1218OfflineQualification.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.18-20260831T085000Z' = 'v1.2.19-20260831T094000Z'
    "registryVersion -eq '1.2.18'" = "registryVersion -eq '1.2.19'"
    "registryVersion='1.2.18'" = "registryVersion='1.2.19'"
    "'content.deepseek'='0.9.0'" = "'content.deepseek'='0.9.1'"
    'authority-v1.2.18-offline-qualification.v1' = 'authority-v1.2.19-offline-qualification.v1'
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.19 qualification marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.New-AuthorityV1219OfflineQualification.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try { [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false)); & $temporary -EvidenceRoot $EvidenceRoot -ProjectRoot $ProjectRoot; if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.19 qualification failed with exit code $LASTEXITCODE." } }
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }
