[CmdletBinding()]
param(
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-live-ocr-anki-v1.2.24\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AuthorityV1218LiveOcrInitial.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.18-20260831T085000Z' = 'v1.2.24-20260901T010500Z'
    'AuthorityV1218' = 'AuthorityV1224'
    'v1218' = 'v1224'
    '1.2.18' = '1.2.24'
    'c07266a9f4eb3e1a872c0c3cfcbd763315bbdb0f491de5448a49dd606406c742' = 'cb9f3291625cb6e7309aa3f677ce783fc713a18b54c29f65a553aa590c8187d7'
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) {
        throw "v1.2.24 runner marker missing: $($replacement.Key)"
    }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporaryScript = Join-Path $PSScriptRoot ('.Invoke-AuthorityV1224.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporaryScript, $template, [Text.UTF8Encoding]::new($false))
    & $temporaryScript -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck
    if ($LASTEXITCODE) { throw "v1.2.24 live task failed: $LASTEXITCODE" }
}
finally {
    if (Test-Path -LiteralPath $temporaryScript) { [IO.File]::Delete($temporaryScript) }
}
