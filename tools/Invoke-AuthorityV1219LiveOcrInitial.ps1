[CmdletBinding()]
param(
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-live-ocr-anki-v1.2.19\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AuthorityV1218LiveOcrInitial.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.18-20260831T085000Z' = 'v1.2.19-20260831T094000Z'
    'AuthorityV1218' = 'AuthorityV1219'
    'v1218' = 'v1219'
    '1.2.18' = '1.2.19'
    'c07266a9f4eb3e1a872c0c3cfcbd763315bbdb0f491de5448a49dd606406c742' = '37fb03973db20562d7fe6d25c0525cdb9aba301cb879a38307694b6827b1f783'
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Authority v1.2.19 live OCR marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.Invoke-AuthorityV1219LiveOcrInitial.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try { [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false)); & $temporary -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck; if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.19 live OCR initial runner failed with exit code $LASTEXITCODE." } }
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }
