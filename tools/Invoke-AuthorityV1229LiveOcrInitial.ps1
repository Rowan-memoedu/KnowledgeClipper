[CmdletBinding()]
param(
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-live-ocr-anki-v1.2.29\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AuthorityV1218LiveOcrInitial.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.18-20260831T085000Z' = 'v1.2.29-20260901T023500Z'
    'AuthorityV1218' = 'AuthorityV1229'
    'v1218' = 'v1229'
    '1.2.18' = '1.2.29'
    'c07266a9f4eb3e1a872c0c3cfcbd763315bbdb0f491de5448a49dd606406c742' = '584b76d471374d96a6732afbf22d3fd2a08ffbb7ddec92c5d7733983747a9735'
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) {
        throw "v1.2.29 initial runner marker missing: $($replacement.Key)"
    }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.Invoke-AuthorityV1229.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "v1.2.29 live initial task failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
}
