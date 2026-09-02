[CmdletBinding()]
param(
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-live-ocr-anki-v1.2.33\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AuthorityV1229LiveOcrInitial.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.29-20260901T023500Z' = 'v1.2.33-20260901T041000Z'
    'AuthorityV1229' = 'AuthorityV1233'
    'v1229' = 'v1233'
    '1.2.29' = '1.2.33'
    '584b76d471374d96a6732afbf22d3fd2a08ffbb7ddec92c5d7733983747a9735' = '82c168a61eb2261f25cb85ec0b9d4a32c96cf4aa6d5b3a17fd5891ead7bb7f6c'
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) { throw "v1.2.33 initial runner marker missing: $($replacement.Key)" }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.Invoke-AuthorityV1233.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "v1.2.33 live initial task failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}
