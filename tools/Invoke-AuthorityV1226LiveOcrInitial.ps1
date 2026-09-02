[CmdletBinding()]
param(
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-live-ocr-anki-v1.2.26\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z'
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AuthorityV1224LiveOcrInitial.ps1') -Raw
$replacements = [ordered]@{
    'v1.2.24-20260901T010500Z' = 'v1.2.26-20260901T013500Z'
    'AuthorityV1224' = 'AuthorityV1226'
    'v1224' = 'v1226'
    '1.2.24' = '1.2.26'
    'cb9f3291625cb6e7309aa3f677ce783fc713a18b54c29f65a553aa590c8187d7' = '8adc3d3e7ea8a149bea71a851e4f0de11af65bd02e9d4426520b909879b65ede'
}
foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not $template.Contains($replacement.Key, [StringComparison]::Ordinal)) { throw "v1.2.26 initial runner marker missing: $($replacement.Key)" }
    $template = $template.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
}
$temporaryScript = Join-Path $PSScriptRoot ('.Invoke-AuthorityV1226.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporaryScript, $template, [Text.UTF8Encoding]::new($false))
    & $temporaryScript -EvidenceRoot $EvidenceRoot -AnkiDeck $AnkiDeck
    if ($LASTEXITCODE) { throw "v1.2.26 live task failed: $LASTEXITCODE" }
}
finally {
    if (Test-Path -LiteralPath $temporaryScript) { [IO.File]::Delete($temporaryScript) }
}
