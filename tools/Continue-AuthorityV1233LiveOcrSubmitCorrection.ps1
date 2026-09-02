[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Continue-AuthorityV1229LiveOcrSubmitCorrection.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.29-20260901T023500Z' = 'v1.2.33-20260901T041000Z'
    'AuthorityV1229' = 'AuthorityV1233'
    'v1229' = 'v1233'
    '1.2.29' = '1.2.33'
    '584b76d471374d96a6732afbf22d3fd2a08ffbb7ddec92c5d7733983747a9735' = '82c168a61eb2261f25cb85ec0b9d4a32c96cf4aa6d5b3a17fd5891ead7bb7f6c'
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) { throw "v1.2.33 correction runner marker missing: $($replacement.Key)" }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.Continue-AuthorityV1233Correction.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "v1.2.33 correction continuation failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}
